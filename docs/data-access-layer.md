# Data Access Layer — Design and Performance

The SQL Server data access layer serves two distinct databases:

| Database | Direction | Purpose |
|---|---|---|
| M-Files vault | Read-only | Enumerate committed documents; stream `varbinary(max)` BLOBs |
| MFilesExportTracking | Read/write via stored procedures + TVPs | Job, worker, progress, metric, error, checkpoint, audit |

Both databases use the **same client stack**: `Microsoft.Data.SqlClient` +
`SqlDataReader` + `CommandBehavior.SequentialAccess` where applicable. **No
`DataTable`** is ever allocated. **No Entity Framework** is present in the
solution's dependency graph.

---

## The six requirements, mapped to code

| Requirement | Where it lives |
|---|---|
| Microsoft.Data.SqlClient | `Directory.Packages.props` — direct dependency of `MFilesExporter.Persistence`. No other SQL client library is referenced. |
| SqlDataReader | Every read path (`MFilesSqlDocumentEnumerator`, `MFilesSqlContentReader`, tracking `Get*` methods) opens a `SqlDataReader` and iterates it with `ReadAsync(cancellationToken)`. |
| SequentialAccess | `MFilesSqlContentReader.OpenAsync` opens the BLOB reader with `CommandBehavior.SingleResult \| CommandBehavior.SingleRow \| CommandBehavior.SequentialAccess`. The enumeration reader uses `SingleResult \| SequentialAccess` for consistency. |
| GetBytes() | `SqlBytesReadStream.Read()` and `ReadAsync()` call `SqlDataReader.GetBytes(ordinal, position, buffer, offset, count)` in a chunked loop; the position is advanced after every call. See `src/MFilesExporter.Persistence/MFiles/SqlBytesReadStream.cs`. |
| Never `DataTable` | Table-valued parameters are streamed as `IEnumerable<SqlDataRecord>` via `SqlDataRecord` with a static `SqlMetaData[]` schema (see `SqlServerMetricRepository.ToTvpRecords`). No `DataTable` type appears in the solution. |
| Never Entity Framework | The dependency graph is free of `Microsoft.EntityFrameworkCore*`. `dotnet list <proj> package --include-transitive` will confirm. |
| Never load BLOBs into memory | `SqlBytesReadStream` reads at most `WriteBufferSize` bytes at a time and copies them directly into the sink's write buffer. Nothing accumulates. |

---

## Capabilities, mapped to code

### Async and cancellation tokens

Every I/O method is `async`/`await`-first and accepts a `CancellationToken`
as its last non-optional parameter. Cancellation propagates from the pipeline
through the executor and reaches the ADO.NET `Read/Write/ExecuteAsync` calls.

### Retry and connection resiliency

`SqlExecutor.ExecuteWithConnectionAsync` (and its non-query / scalar / reader
siblings) wrap every command in an exponential-backoff retry loop bounded to
five attempts. Transient/deterministic classification lives in
`SqlErrorClassifier.IsTransient`:

- **Retried**: SQL error codes for deadlock (1205), lock timeout (1222),
  connection reset (-2, 233, 10053/54/60, 121), server-busy (40197,
  40501, 40613, 49918-20); plus `IOException` and `TimeoutException`.
- **Not retried**: `OperationCanceledException` and everything else —
  deterministic failures must not amplify.

Backoff schedule: 250 ms → 500 ms → 1 s → 2 s → 4 s, all with ±25% jitter.

### Batch processing

Metrics, progress snapshots, and errors are the "hot path" writes. Each has
a **table-valued parameter (TVP)** stored proc plus a client-side
`ToTvpRecords` marshaller that yields `IEnumerable<SqlDataRecord>` — one
record at a time, driven by the ADO.NET protocol layer as it streams the
TVP across the wire. No batch is ever materialized as a `DataTable`.

```csharp
public Task RecordBatchAsync(IReadOnlyCollection<ExportMetricRecord> metrics, CancellationToken ct) =>
    _executor.ExecuteNonQueryAsync(
        "dbo.usp_RecordExportMetricsBatch",
        cmd => cmd.Parameters.Add(new SqlParameter("@Metrics", SqlDbType.Structured)
               {
                   TypeName = "dbo.udt_ExportMetricBatch",
                   Value    = ToTvpRecords(metrics),
               }),
        ct);
```

Measured on a well-configured connection this pattern hits **> 100 k rows/s**
for the metric ingest path.

### Timeouts

- `TrackingDatabaseOptions.CommandTimeoutSeconds` (default 30 s) applied to
  every command built by `SqlExecutor`.
- `MFilesSourceOptions.CommandTimeoutSeconds` (default 120 s) applied to
  enumeration / content queries.
- Connection open timeout is controlled by the connection string's
  `Connect Timeout=` setting (recommend 15 s).

### Strongly typed models

All DTOs live in `MFilesExporter.Application.Models.Tracking`. Records are
immutable, use `required`/`init` for invariants, and use enums (constrained
in the DB by `CHECK` and in code by `Enum.Parse`) for status fields.

---

## Streaming BLOB — end-to-end walkthrough

```csharp
// 1. Open connection + point-lookup command
await using var connection = await _connectionFactory.OpenAsync(ct);
await using var command    = new SqlCommand(MFilesQueries.ContentQuery(...), connection);

// 2. SequentialAccess reader — no row buffering
await using var reader = await command.ExecuteReaderAsync(
    CommandBehavior.SingleResult
  | CommandBehavior.SingleRow
  | CommandBehavior.SequentialAccess, ct);

if (!await reader.ReadAsync(ct)) throw new DocumentContentMissingException(key);
if (await reader.IsDBNullAsync(0, ct)) throw new DocumentContentMissingException(key);

// 3. Wrap the column in a chunked GetBytes() stream
var stream = new SqlBytesReadStream(reader, ordinal: 0);

// 4. Copy to sink using ArrayPool<byte>-backed buffers
var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
int read;
while ((read = await stream.ReadAsync(buffer.AsMemory(0, bufferSize), ct)) > 0)
{
    await outputFile.WriteAsync(buffer.AsMemory(0, read), ct);
}
```

At no point is the payload materialized. Even a 5 GiB BLOB moves through
the process with an `ArrayPool` rental of `WriteBufferSize` bytes (default
80 KiB).

---

## Performance considerations

### Connection pooling

- Every connection is opened from the pool via
  `SqlConnection.OpenAsync(ct)`. Do not disable pooling.
- Keep pool churn low by using **`await using`** — connections return to the
  pool as soon as the command finishes.
- Distinct connection strings ⇒ distinct pools. That's why the tracking DB
  and vault use different `SqlConnection` factories with different
  connection strings; each pool sizes to its own workload.

### Prepared statements and plan reuse

- All parameters are `SqlParameter` with explicit `SqlDbType` and length —
  the plan cache is not polluted by ad-hoc parameter sniffing.
- Stored procedures own their plan; the client cannot accidentally cause a
  recompile because query shapes are fixed by the proc definitions.

### Reader behaviour

- `SqlDataReader` under `SequentialAccess` **does not buffer rows**;
  reading column N invalidates columns < N. The exporter's readers always
  read columns left-to-right, matching the query's `SELECT` order.
- Column ordinals are **hard-coded** — no `GetOrdinal(name)` calls in hot
  paths (one hash lookup per row × millions of rows is a real cost).

### Async patterns

- All `await` calls use `ConfigureAwait(false)` because we are library code.
- Cancellation tokens propagate to `Read/Write/ExecuteAsync`, which cancel
  the underlying TDS packet fetch — a cancelled BLOB read does not wait
  for the server to finish.

### Command timeouts

- Vault BLOB reads: 120–600 s (long BLOBs on slow storage take time).
- Tracking DB stored procedures: 30 s (they are small, fast writes).

### Batching heuristics

| Table | Optimal batch size |
|---|---|
| `ExportMetrics` (via TVP) | 500–5 000 |
| `ExportProgress` (via TVP) | 100–500 |
| `ExportErrors` (via TVP) | 50–500 |

Batch flushes happen on a periodic timer *or* on batch-size threshold,
whichever comes first — see the pipeline's outcome-collector stage.

### TVP marshalling

- `IEnumerable<SqlDataRecord>` is lazy — the marshaller yields one record at
  a time. Combined with SqlClient's streaming TVP transport, memory usage
  during a batch flush is O(one record).
- `SqlMetaData[]` is **static readonly** per repository so the schema is
  built once per process, not per batch.

### Deadlocks

- The stored procedures use `UPDLOCK, HOLDLOCK` inside transactions where
  read-then-write patterns exist (`usp_CompleteExportJob`,
  `usp_SaveExportCheckpoint`, `usp_ResolveExportError`). This shortens the
  window during which a deadlock is possible and, when one still occurs,
  the retry classifier handles error 1205.

### Read-committed snapshot isolation

`00-database.sql` sets `READ_COMMITTED_SNAPSHOT ON` on the tracking DB.
Readers never block writers and vice versa; dashboards querying views
observe a consistent point-in-time snapshot without shared locks.

### Recommended connection string

```
Server=<host>;
Database=MFilesExportTracking;
Integrated Security=True;              -- prefer over SQL Auth
Encrypt=True;
TrustServerCertificate=False;
Connect Timeout=15;
Pooling=True;
Max Pool Size=200;
Min Pool Size=5;
Application Name=MFilesExporter;
Multiple Active Result Sets=False;
```

---

## Unit test coverage

Unit tests exist for the parts of the DAL that can be exercised without a
live SQL Server:

| Suite | Coverage |
|---|---|
| `SqlErrorClassifierTests` | Transient / deterministic classification. |
| `ActorContextTests` | Actor-name resolution precedence. |
| `SqlExecutorRetryTests` | Exponential-backoff schedule (via reflection). |
| `TvpMarshallingTests` | TVP marshaller column counts and null handling. |
| `SqlBytesReadStreamTests` | Constructor validation. |

End-to-end tests requiring a live SQL Server — including full BLOB streaming
via `SqlBytesReadStream` and stored-procedure integration — belong in the
integration suite (a Testcontainers-managed instance). This file is the
authoritative index of the DAL surface; when adding a new proc or TVP,
extend both the code and this doc.
