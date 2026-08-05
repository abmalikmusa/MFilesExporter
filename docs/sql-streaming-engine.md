# SQL Streaming Engine

**Purpose.** Execute the canonical M-Files document query in a
memory-bounded, cancellable, resumable, and retryable fashion. The engine
is the single component that reads from the source vault; every downstream
stage consumes its output.

**Business contract preserved.** The engine produces the same row set as
the canonical query (`JOIN` of `DOCUMENTFILEVERSION` + `DATAFILEVERSION` +
`DATAFILEVERSION_BYTES` where `UPLOADCOMMITTED = 1`), decomposed into a
keyset-paginated metadata stream and per-document BLOB streams. See
`docs/mfiles-schema.md` and `docs/query-performance-review.md` for the
equivalence argument.

---

## 1. What the engine is

One interface, one implementation:

```csharp
public interface ISqlStreamingEngine
{
    IAsyncEnumerable<StreamedDocumentDescriptor> StreamAsync(
        DocumentFileVersionKey exclusiveLowerBound,
        SqlStreamingRunOptions? runOptions,
        IProgress<SqlStreamingProgress>? progress,
        CancellationToken cancellationToken);
}
```

Yielded elements combine an immutable `DocumentDescriptor` with a
`Task<DocumentContentStream> OpenContentStreamAsync(CancellationToken)`
factory — the BLOB is fetched only when the caller asks for it. This is
what lets downstream stages parallelize BLOB fetches independently from
the metadata cursor.

**Type rules enforced by the code:**

| Requirement | Enforcement |
|---|---|
| `Microsoft.Data.SqlClient` | Only SqlClient types used; no other data provider referenced. |
| `SqlDataReader` | Every read path uses `SqlDataReader`, drained with `ReadAsync(ct)`. |
| `CommandBehavior.SequentialAccess` | Applied to both metadata and BLOB readers. |
| `GetBytes()` for BLOBs | `SqlBytesReadStream` wraps `SqlDataReader.GetBytes(...)`. |
| No `DataTable` | Grep the assembly — never referenced. |
| No `DataSet` | Grep the assembly — never referenced. |
| No Entity Framework | The `Microsoft.EntityFrameworkCore.*` package family is not in the dependency graph. |

---

## 2. How each capability is implemented

### 2.1 Cancellation

Every internal method accepts a `CancellationToken` and threads it into:

- `SqlConnection.OpenAsync(ct)` — cancels connection establishment.
- `SqlDataReader.ReadAsync(ct)` — cancels the fetch of the next row.
- `SqlDataReader.IsDBNullAsync(ordinal, ct)` — cancels a column check.
- `Task.Delay(delay, ct)` — cancels the retry backoff.
- `yield return` sites — the `[EnumeratorCancellation]` attribute passes
  the caller's token through to the `IAsyncEnumerable`.

`OperationCanceledException` is **never** swallowed and **never**
classified as transient — it always propagates.

### 2.2 Progress

The engine accepts `IProgress<SqlStreamingProgress>`; ticks are emitted at
most every `SqlStreamingOptions.ProgressReportInterval` (default 5 s) plus
one final tick on stream completion.

Each tick reports:
- `RowsYielded` — cumulative rows sent to the caller
- `PagesFetched` — round-trips to SQL Server
- `RetryAttempts` — cumulative retry count
- `LastCursor` — the last metadata cursor observed
- `ObservedAtUtc`, `Elapsed`, and a computed `RowsPerSecond`

A progress consumer that throws does **not** fault the stream — the engine
catches and logs it, then continues.

### 2.3 Retries + connection resiliency

Retries are handled by an internal loop with exponential backoff + jitter:

- Base delay: `SqlStreamingOptions.RetryBaseDelay` (default 250 ms)
- Max delay: `SqlStreamingOptions.RetryMaxDelay` (default 30 s)
- Jitter: uniform in [0.75×, 1.25×]
- Attempts: `SqlStreamingOptions.MaxRetryAttempts` (default 5)

Classification is done by `SqlTransientErrorClassifier`:

| Kind | Retryable? | Notes |
|---|---|---|
| `SqlException` numbers 1205, 1222 | Yes | Deadlock / lock timeout |
| `SqlException` numbers -2, 233, 10053, 10054, 10060, 121 | Yes | Client-side connection transients |
| `SqlException` numbers 40197, 40501, 40613, 49918-20 | Yes | Server-busy / Azure SQL throttling |
| `IOException`, `TimeoutException` | Yes | Network transients |
| `OperationCanceledException` | **No** | Cancellation propagates |
| Everything else | **No** | Deterministic failure |

Deterministic failures (permission denied, missing row, etc.) surface
immediately as exceptions to the caller.

**Connection resiliency** is a subset of the retry story: `SqlConnection`
open failures are just another point that raises `SqlException` and gets
retried. Because every operation opens a fresh connection from the pool,
we do not need "long-running connection" resiliency at the client — the
pool re-establishes connections behind us.

### 2.4 Configurable timeout

Two timeouts, both configurable:

- **`CommandTimeoutSeconds`** (default 120) — applied to metadata queries.
- **`BlobCommandTimeoutSeconds`** (default 600) — applied to BLOB fetches.

The distinction matters: a multi-MB BLOB may legitimately take minutes to
stream, whereas a metadata page should finish in seconds.

Per-invocation overrides via `SqlStreamingRunOptions.CommandTimeout` and
`BlobCommandTimeout`.

### 2.5 Configurable fetch size

`SqlStreamingOptions.FetchSize` (default 1 000) controls how many rows the
keyset-paginated metadata query returns per round-trip. Sweet spot:

- **< 500** — round-trip cost dominates.
- **1 000–5 000** — best throughput on typical hardware.
- **> 10 000** — memory grows without much throughput gain.

Additionally, `NetworkPacketSizeBytes` (default 8 192) is composed into the
connection string as `Packet Size=`. Larger packets (e.g. 32 768) reduce
per-BLOB TDS overhead for large payloads.

---

## 3. End-to-end flow

```
Caller
   │
   ▼ (async foreach)
StreamAsync
   │
   ├─► FetchPageWithRetryAsync (per keyset page)
   │      │
   │      └─► FetchPageOnceAsync
   │             open connection → SqlCommand → SqlDataReader
   │             (SingleResult | SequentialAccess)
   │             yield DocumentDescriptor × N (up to FetchSize)
   │             close reader/command/connection
   │
   ▼ per descriptor
StreamedDocumentDescriptor
   │
   ▼ (on caller's demand)
OpenContentStreamAsync
   │
   └─► OpenContentWithRetryAsync
          │
          └─► OpenContentOnceAsync
                 open connection → SqlCommand → SqlDataReader
                 (SingleRow | SingleResult | SequentialAccess)
                 ReadAsync + IsDBNullAsync(0)
                 return SqlBytesReadStream (GetBytes-based chunk reader)
                 [caller streams the BLOB; disposing the stream closes reader+connection]
```

Metadata reader lifetime: bounded to one page (opened, drained, closed).

BLOB reader lifetime: bounded to one document (opened, streamed, closed
by the caller via `DocumentContentStream.DisposeAsync()`).

At any moment during a run there is at most:
- one metadata reader (or zero, between pages),
- plus N BLOB readers where N ≤ the caller's concurrency setting (typically
  8–16).

---

## 4. Memory usage analysis

### 4.1 Sizes of things

| Thing | Bytes | Notes |
|---|---|---|
| One `DocumentDescriptor` (record + inner records) | ~200 | Composes two 16-byte keys + strings |
| `DocumentDescriptor.Title` (typical) | 50–200 | Excluded on empty titles |
| One `StreamedDocumentDescriptor` | ~250 | Wraps `DocumentDescriptor` + a small `Func` closure |
| One `SqlConnection` in the pool | ~5–10 KB | Managed + native handles |
| One `SqlDataReader` (metadata) | ~2 KB | Bounded — SequentialAccess never buffers rows |
| One `SqlDataReader` (BLOB, SequentialAccess) | ~2 KB + TDS packet buffer (default 8 KB) | Whole BLOB never resident |
| Metadata page (1 000 descriptors) | ~250 KB | Approximation; grows with title length |
| BLOB read buffer (`ArrayPool<byte>` rental) | 80 KB | Reused across items; not permanent |

### 4.2 Steady-state working set

For a worker running with `Pipeline.ContentReaderConcurrency = 16`:

```
   1 metadata reader     ~2 KB + 8 KB packet buffer         =    10 KB
   1 metadata page       1 000 × ~250 B                     =   250 KB
   16 BLOB readers       16 × (2 KB + 8 KB packet buffer)   =   160 KB
   16 BLOB write buffers 16 × 80 KB (ArrayPool)             = 1 280 KB
   Connection pool (Max Pool Size=200)                       ~2 MB
   .NET runtime + GC roots                                   ~30 MB
   ───────────────────────────────────────────────────────
   Total steady-state RSS                                   ≤ 35 MB
```

Compare with a naive design:

```
   SELECT canonical_query INTO DataTable  →  hold whole result
   5 000 000 docs × avg BLOB size 2 MB   =   10 TB
```

The savings factor is **~10⁷×**. This is why streaming is not a
nice-to-have but a requirement.

### 4.3 GC pressure

- `DocumentDescriptor` — one small allocation per yield (~200 B), gen-0.
- `StreamedDocumentDescriptor` — one small allocation per yield (~250 B),
  gen-0.
- BLOB buffers — rented from `ArrayPool<byte>.Shared`, no allocation.
- Retry backoff — one `Task.Delay` continuation per retry (rare).
- Progress ticks — one record allocation per interval (rare).

Result: gen-0 pressure only; no gen-2 activity from the engine itself.
Bulk of the process-wide GC comes from the sink write buffers, which also
use `ArrayPool`.

### 4.4 Why `SequentialAccess` matters here

Without `SequentialAccess`, `SqlDataReader` buffers each row in full so
you can `reader.GetXXX(N)` any column in any order. For our metadata
rows, this is negligible (<1 KB per row). For our BLOB rows, this would
buffer the **entire varbinary(max)** value before you could read it —
turning a streaming operation into a materializing one and blowing the
memory model apart.

Under `SequentialAccess`:
- The row is not buffered.
- Columns must be read in ordinal order.
- Reading a column invalidates any earlier column.
- `GetBytes(ordinal, offset, buffer, ...)` streams the varbinary in
  arbitrarily sized chunks; the reader keeps only "position within the
  BLOB" as state.

The engine's readers are all written to read columns left-to-right in one
pass — matching the `SELECT` order. This is why hard-coded ordinals appear
throughout (no `GetOrdinal(name)` lookups in hot loops).

### 4.5 Bounded concurrency = bounded memory

The engine itself does not spawn parallel BLOB reads — that's the caller's
job. If the caller opens 16 BLOB streams simultaneously, memory grows to
16 × (2 KB + 80 KB) ≈ 1.3 MB regardless of BLOB sizes. If the caller
opens 1 000 BLOB streams simultaneously, memory grows to 80 MB. In both
cases memory is a function of concurrency × buffer size, **not** of BLOB
size or total document count.

---

## 5. Configuration

```jsonc
{
  "Exporter": {
    "SqlStreaming": {
      "FetchSize": 1000,                    // rows per keyset page
      "CommandTimeoutSeconds": 120,         // metadata query timeout
      "BlobCommandTimeoutSeconds": 600,     // BLOB fetch timeout
      "NetworkPacketSizeBytes": 8192,       // TDS Packet Size=
      "UseReadUncommittedForEnumeration": true,
      "ProgressReportInterval": "00:00:05",
      "MaxRetryAttempts": 5,
      "RetryBaseDelay": "00:00:00.250",
      "RetryMaxDelay": "00:00:30"
    }
  }
}
```

Bind at startup via `AddExporterConfiguration(builder.Configuration)`.
`ExporterOptionsValidator` will trip if any required setting is missing.

---

## 6. Usage sketch

```csharp
public sealed class ExampleWorker
{
    private readonly ISqlStreamingEngine _engine;
    private readonly IProgress<SqlStreamingProgress> _progress;

    public async Task RunAsync(CancellationToken ct)
    {
        var cursor = DocumentFileVersionKey.Origin;  // or resume from checkpoint
        var options = new SqlStreamingRunOptions
        {
            FetchSize        = 2_000,
            CommandTimeout   = TimeSpan.FromSeconds(60),
            CorrelationId    = Guid.NewGuid().ToString(),
        };

        await foreach (var descriptor in _engine.StreamAsync(cursor, options, _progress, ct))
        {
            await using var content = await descriptor.OpenContentStreamAsync(ct);
            // Stream content.Content to the sink...
        }
    }
}
```

Never `.ToListAsync()` the enumerable — that would defeat the streaming
guarantee.

---

## 7. What NOT to do

- **Do not** call `.ToList()` / `.ToListAsync()` / `.ToArrayAsync()` on the
  returned `IAsyncEnumerable`. Doing so materializes the entire result set
  and turns a streaming operation into a materializing one.
- **Do not** hold onto a `StreamedDocumentDescriptor` past the surrounding
  `foreach` iteration if you plan to open its content later — the closure
  captures the engine's state at yield time, and the connection pool /
  timeout budget must be respected.
- **Do not** call `OpenContentStreamAsync` twice on the same descriptor.
  Each call opens a fresh reader; there is no caching.
- **Do not** dispose the returned `DocumentContentStream` by simply reading
  to EOF — always `await using` so the connection is returned to the pool.

---

## 8. Testability

- **Unit tests** (`tests/MFilesExporter.Tests/Persistence/Streaming/`):
  - `SqlTransientErrorClassifierTests` — locks retry classification.
  - `StreamedDocumentDescriptorTests` — verifies the content factory is
    deferred (not invoked at yield time).
  - `SqlStreamingProgressTests` — computed properties.
- **Integration tests** (recommended, not shipped): use Testcontainers'
  `mssql:2022` image, seed the three vault tables, and drive the engine
  through the full read path. Assertions:
  - Yield count equals seeded row count.
  - Restart from cursor `k` yields only rows > `k`.
  - Killing the connection mid-stream causes a retried recovery under the
    default policy.
  - Cancellation aborts within the current `ReadAsync` await.

---

## 9. Where this fits in the layered architecture

| Layer | Component |
|---|---|
| Application | `ISqlStreamingEngine` port + DTOs (record types) |
| Persistence | `SqlStreamingEngine` implementation using SqlClient |
| Configuration | `SqlStreamingOptions` bound under `Exporter:SqlStreaming` |
| Domain | `DocumentDescriptor`, `DocumentFileVersionKey`, `DataFileVersionKey` — reused |

The engine composes the existing `MFilesQueries` (canonical query
derivatives), `SqlBytesReadStream` (chunked GetBytes), and
`ISqlConnectionFactory` (connection pool) rather than duplicating them.
