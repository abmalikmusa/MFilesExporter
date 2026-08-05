# Binary Object Reader

**Purpose.** Copy a single `VARBINARY(MAX)` column from a
`SqlDataReader` to a destination `Stream` — streamed in bounded chunks,
never buffered, with inline checksum, progress, and validation. Supports
payloads larger than 4 GiB.

Used by verification tools, ad-hoc utilities, and any sink implementation
that wants an *inline* hash while it writes. Complementary to the pipeline
hot path (which uses `SqlBytesReadStream` + a separate sink hasher).

---

## 1. What it looks like

```csharp
public interface IBinaryObjectReader
{
    Task<BinaryReadResult> ReadAsync(
        SqlDataReader             reader,
        int                       ordinal,
        Stream                    destination,
        BinaryReadOptions         options,
        IProgress<BinaryReadProgress>? progress,
        CancellationToken         cancellationToken);
}
```

- **`reader`** — positioned on the row, opened with
  `CommandBehavior.SequentialAccess`. Not disposed by the reader.
- **`ordinal`** — column index of the `VARBINARY(MAX)`.
- **`destination`** — any writable `Stream`. Not flushed or closed by the
  reader; the caller owns its lifetime.
- **`options`** — buffer size, checksum algorithm, expected size/checksum,
  progress interval, validation policy.
- **`progress`** — optional; ticked at the configured interval and once
  at completion.
- **`cancellationToken`** — cancels the copy mid-loop.

---

## 2. Type map

| Type | Role |
|---|---|
| `IBinaryObjectReader` | The port — inject this. |
| `BinaryObjectReader` | Default implementation. |
| `BinaryReadOptions` | Per-invocation configuration. |
| `BinaryReadProgress` | Progress tick (bytes, chunks, elapsed, MiB/s, percent). |
| `BinaryReadResult` | Terminal result (bytes, chunks, checksum, elapsed, validation). |
| `BinaryReadValidation` | Sub-record with per-check pass/fail. |
| `BinaryChecksumAlgorithm` | Enum: `None / Sha256 / Sha1 / Sha512 / Md5`. |
| `BinaryValidationException` | Thrown on validation mismatch. Extends `DomainException` (deterministic). |

---

## 3. Implementation contract

**Requirement → mechanism** table:

| Requirement | How it is implemented |
|---|---|
| Read directly from `SqlDataReader` | `reader.GetBytes(ordinal, fieldOffset, buffer, 0, bufferSize)` per chunk. No wrapping stream, no `SqlBytes`, no `SqlBinary.Value`. |
| Never load the entire BLOB | Bounded `ArrayPool<byte>.Shared.Rent(bufferSize)`. One buffer per read; returned in the `finally`. |
| Support files > 4 GiB | Field offset is `long`. Bytes-read counter is `long`. `BinaryReadResult.BytesRead` is `long`. `BinaryReadOptions.ExpectedByteCount` is `long?`. |
| Support cancellation | `cancellationToken.ThrowIfCancellationRequested()` at the top of each iteration + threaded through `WriteAsync`. |
| Support progress | `IProgress<BinaryReadProgress>` with configurable throttling. Final tick guaranteed on completion. |
| Support checksum generation | `IncrementalHash.AppendData(...)` per chunk; hex-encoded on completion. |
| Support validation | Compare bytes-read to `ExpectedByteCount` and hash-hex to `ExpectedChecksumHex` on completion. Throw `BinaryValidationException` (or return validation block, per policy). |

The core loop is ~30 lines. Everything else is contracts and safety.

### 3.1 The loop (annotated)

```csharp
long position = 0;                                          // 64-bit ⇒ > 4 GiB safe
byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);    // no allocation growth with BLOB size

using var hasher = IncrementalHash.CreateHash(hashName);    // optional
try
{
    while (true)
    {
        cancellationToken.ThrowIfCancellationRequested();

        long bytesRead = reader.GetBytes(                    // ← the entire "streaming" contract
            ordinal,
            fieldOffset: position,                            // long — supports > 4 GiB
            buffer,
            bufferOffset: 0,
            length: BufferSize);

        if (bytesRead <= 0) break;                            // EOF sentinel from GetBytes

        hasher.AppendData(buffer, 0, (int)bytesRead);         // streaming hash
        await destination.WriteAsync(buffer.AsMemory(0, (int)bytesRead), ct);

        position += bytesRead;
        // Optional progress report on interval
    }
}
finally
{
    ArrayPool<byte>.Shared.Return(buffer);
}
```

### 3.2 Testability

The public `ReadAsync` accepts a `SqlDataReader` — hard to fake in
isolation. So the copy loop is factored into an `internal static`
`ReadInternalAsync(GetBytesDelegate, ...)` that unit tests drive against an
in-memory byte source. The delegate signature is:

```csharp
public delegate long GetBytesDelegate(long fieldOffset, byte[] buffer, int bufferOffset, int length);
```

which is a byte-for-byte mirror of
`SqlDataReader.GetBytes(int, long, byte[], int, int)` (minus the ordinal,
already captured). Everything provable in tests holds against the real
`SqlDataReader`.

`InternalsVisibleTo("MFilesExporter.Tests")` is set globally in
`Directory.Build.props`, so tests reach the helper directly.

---

## 4. Configuration reference

`BinaryReadOptions` — all `init` properties on an immutable record:

| Property | Default | Purpose |
|---|---|---|
| `BufferSize` | `81920` (80 KiB) | Chunk size for `GetBytes`. Must be ≥ 4 096. |
| `Checksum` | `Sha256` | Algorithm. `None` disables hashing entirely. |
| `ExpectedByteCount` | `null` | When set, compared to actual bytes read. |
| `ExpectedChecksumHex` | `null` | When set, compared case-insensitively. |
| `ThrowOnValidationFailure` | `true` | Off ⇒ mismatch is surfaced via `Validation` instead of throwing. |
| `ProgressReportInterval` | 2 s | Time between ticks. `Zero` disables throttling. |

---

## 5. Result reference

```csharp
public sealed record BinaryReadResult
{
    public required long BytesRead { get; init; }
    public required long ChunkCount { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public string? ChecksumHex { get; init; }
    public required BinaryChecksumAlgorithm ChecksumAlgorithm { get; init; }
    public BinaryReadValidation? Validation { get; init; }

    public double MebibytesPerSecond { get; }
}
```

`Validation` is populated only when the caller supplied an expected value.
When `ThrowOnValidationFailure` is `true` (default), a returned
`Validation` always has `IsValid == true`.

---

## 6. Sample usage

### 6.1 Verify a document against a manifest entry

```csharp
await using var conn = await connFactory.OpenAsync(ct);
await using var cmd  = new SqlCommand("SELECT DATA FROM DATAFILEVERSION_BYTES WHERE ID_DOCUMENTFILEPART=@p AND ID_DATAFILEVERSION=@v", conn);
cmd.Parameters.Add("@p", SqlDbType.BigInt).Value = docPart;
cmd.Parameters.Add("@v", SqlDbType.BigInt).Value = dataFileVer;

await using var reader = await cmd.ExecuteReaderAsync(
    CommandBehavior.SingleRow | CommandBehavior.SingleResult | CommandBehavior.SequentialAccess, ct);

if (!await reader.ReadAsync(ct)) throw new DocumentContentMissingException(key);

await using var file = File.Create(outputPath);
var result = await binaryReader.ReadAsync(
    reader, ordinal: 0, file,
    new BinaryReadOptions
    {
        Checksum            = BinaryChecksumAlgorithm.Sha256,
        ExpectedByteCount   = manifestEntry.DeclaredLogicalSize,
        ExpectedChecksumHex = manifestEntry.Checksum,
    },
    progress: null, ct);

// result.BytesRead == manifestEntry.DeclaredLogicalSize
// result.ChecksumHex == manifestEntry.Checksum
```

### 6.2 Discard payload — just get the checksum

```csharp
var result = await binaryReader.ReadAsync(
    reader, ordinal: 0, Stream.Null,
    new BinaryReadOptions { Checksum = BinaryChecksumAlgorithm.Sha256 },
    progress: null, ct);

// result.ChecksumHex is the SHA-256; nothing written anywhere.
```

### 6.3 Progress bar (console)

```csharp
IProgress<BinaryReadProgress> bar = new Progress<BinaryReadProgress>(p =>
{
    if (p.PercentComplete is double pct)
    {
        Console.Write($"\r{pct:P1} — {p.MebibytesPerSecond:F1} MiB/s");
    }
});

await binaryReader.ReadAsync(reader, 0, file, new BinaryReadOptions
{
    ExpectedByteCount      = manifestEntry.DeclaredLogicalSize,
    ProgressReportInterval = TimeSpan.FromMilliseconds(250),
}, bar, ct);
```

---

## 7. Performance recommendations

### 7.1 Buffer size

- **Default (80 KiB) works well for most payloads.** Large enough to amortize
  ~10 GetBytes calls per MiB, small enough that pooled memory returns to
  the pool quickly.
- **For very large BLOBs (> 100 MiB)**, bump to 256 KiB–1 MiB. Fewer
  round-trips into TDS reduce syscall count and CPU.
- **On memory-constrained hosts** (containers with strict memory limits),
  32 KiB is safe.
- Do NOT go below the smallest expected TDS packet size (defaults to 8 KiB
  server-side; effective floor is 4 096 bytes enforced by the reader).

### 7.2 TDS packet size

Set the connection string's `Packet Size` to match your BLOB workload:

- `Packet Size=8192` — SQL Server default. Fine for many small BLOBs.
- `Packet Size=32768` — reduce TDS overhead for multi-MB BLOBs. Max
  supported.

Set once at connection-string level; the reader inherits it.

### 7.3 Choice of checksum

| Algorithm | Speed | Notes |
|---|---|---|
| `None` | ⚫️⚫️⚫️⚫️⚫️ (skip) | When only the byte count matters. |
| `Sha256` | ⚫️⚫️⚫️⚫️ | **Default.** Modern CPUs hit ~500 MiB/s via SHA-NI. |
| `Sha1` | ⚫️⚫️⚫️⚫️ | Slightly faster; use only for legacy manifest formats. |
| `Sha512` | ⚫️⚫️⚫️ | ~25 % slower than SHA-256 on 64-bit CPUs. |
| `Md5` | ⚫️⚫️⚫️⚫️⚫️ | Fast but broken; keep for legacy compat only. |

For 5 M documents at ~2 MiB each, SHA-256 hashing adds ~1 min of CPU per
worker at 500 MiB/s.

### 7.4 Never `SequentialAccess = false`

`GetBytes` still works without `SequentialAccess`, but the reader will
buffer the entire varbinary in memory before you can access column 0.
This is the exact failure mode this component exists to avoid. If you
open a reader for BLOB reads WITHOUT `SequentialAccess`, you are back to
loading everything.

### 7.5 Do not seek the destination

If the destination is a `FileStream`, use `FileMode.Create` +
`FileOptions.SequentialScan | FileOptions.Asynchronous`. Do not `Seek`
between chunks — the reader writes strictly forward.

### 7.6 Read exactly one BLOB per reader

`SqlDataReader.GetBytes` is meaningful only while the reader is positioned
on the row that owns the varbinary column. After the reader advances (or
closes), the BLOB is inaccessible. Design your queries with
`CommandBehavior.SingleRow` when the column will be streamed — that lets
the client library release row-state early.

### 7.7 Parallelism

Run **one `IBinaryObjectReader` invocation per connection**. Multiple
concurrent reads must be on separate connections (`SqlConnection` +
`SqlDataReader` + `SqlCommand` triples). Do not try to interleave reads on
one reader — `GetBytes` state is per-column, per-row.

### 7.8 Cancellation cost

`cancellationToken.ThrowIfCancellationRequested()` is a cheap volatile
read — safe to check every iteration. Cancellation aborts within the
current chunk; the partial destination content remains and is the
caller's problem to clean up (they know whether it was a temp file, an
S3 upload, etc.).

### 7.9 Progress cost

Progress reporting is throttled to `ProgressReportInterval`. Even at
250 ms intervals on a 1-hour transfer, that's < 20 000 delegate
invocations — negligible.

`System.Progress<T>` marshals to the sync context. For UI apps this is
what you want; for hosted services this adds latency. In hot paths use a
`SynchronousProgress<T>` wrapper (see tests).

### 7.10 GC pressure

- Zero long-lived allocations: buffer is pooled, hash resets each call,
  result and progress records are ~200-byte gen-0 allocations.
- One 32-byte hex string allocated at completion (checksum).
- No boxing anywhere in the loop — all counters are `long` fields.

At 5 M documents, aggregate allocations from this component are on the
order of **kilobytes** — the rest is pool churn.

### 7.11 Streaming through `Stream.Null` for verification

If you want to verify the source (checksum + byte count) without keeping
the payload, pass `Stream.Null`. `Stream.Null.WriteAsync` is a fast no-op
JIT'd inline — the whole read becomes CPU (hashing) + network bound.

---

## 8. Failure semantics

| Failure | Handling |
|---|---|
| Cancellation | `OperationCanceledException` bubbles. Partial destination content is left behind. |
| Validation failure (size/checksum) | `BinaryValidationException` thrown when `ThrowOnValidationFailure = true`; otherwise the failure is reported via `Validation` on the result. Never retried — deterministic. |
| Transient SQL failure mid-chunk | `SqlException` bubbles. Caller decides whether to retry the whole read. This component does NOT retry — it holds a live reader and cannot re-fetch state after the mid-stream failure. |
| Destination stream throws | Bubbles. Buffer is returned to the pool in the `finally`. |
| Invalid options | `ArgumentException` / `ArgumentOutOfRangeException` at guard time; no I/O has started. |

---

## 9. Testing

Unit tests exercise the internal `ReadInternalAsync` helper via a
`GetBytesDelegate` that mirrors `SqlDataReader.GetBytes` — a byte-identical
substitute. Covered:

- Copies every byte to the destination.
- Computes SHA-256 that matches `SHA256.HashData(payload)`.
- `Checksum = None` leaves `ChecksumHex` null.
- Progress ticks fire at interval AND on completion.
- Validation success populates the validation block.
- Byte-count mismatch throws.
- Checksum mismatch throws.
- `ThrowOnValidationFailure = false` reports without throwing.
- Cancellation aborts within a bounded time.
- **5 GiB simulation** — proves the loop uses `long` offsets end-to-end
  (destination is `Stream.Null` to keep test memory bounded).
- Buffer-size guard rejects < 4 KiB.
- `ExpectedChecksumHex` with `Checksum = None` rejects at guard time.

Integration tests (recommended, not shipped) should:

1. Insert a > 4 GiB `VARBINARY(MAX)` value using
   `.WRITE(@chunk, @offset, NULL)` to prove SQL Server accepts and returns
   > 4 GiB payloads through this pathway.
2. Kill the underlying network connection mid-read and confirm the caller
   sees a `SqlException` (not a corrupted checksum).
3. Run 16 concurrent reads on 16 connections and confirm all checksums
   match.

---

## 10. What this component does NOT do

- **It does not retry.** The reader holds a live `SqlDataReader`; if TDS
  fails mid-stream, the row is gone. The caller must re-issue the query.
  (See `SqlStreamingEngine` for retries at the reader-open boundary.)
- **It does not open connections or commands.** The caller supplies a
  positioned reader. This keeps the reader focused on one thing.
- **It does not know about the M-Files schema.** It works with any
  varbinary(max) column. That's why it lives in `Persistence/MFiles/Blobs`
  — the folder groups vault-related utilities but the class itself is
  vault-agnostic.
- **It does not decompress or transform.** It writes bytes verbatim. A
  compression wrapper is a decorator, not a rewrite of this component.
