# Domain Model — Property-by-Property Reference

The domain models the **export process** (a bounded activity with jobs,
batches, workers, and outcomes) rather than the M-Files schema. This document
is the authoritative reference for every model and every property.

Design tenets (all enforced across every model below):

1. **Single responsibility** — each type answers exactly one question.
2. **Immutable** — records with `init`-only setters or read-only properties.
3. **Serializable** — plain records with primitive fields, `IReadOnlyList<T>`
   for collections. `System.Text.Json` handles them natively.
4. **Validated** — invariants enforced in constructors + `Validate()`
   returning `ValidationResult` for business-rule checks.
5. **XML-documented** — every public member has a `<summary>` and, where the
   *why* is non-obvious, a `<remarks>` block.

Namespaces mirror the folder tree under `MFilesExporter.Domain`.

---

## 1. Cross-cutting primitives

### `Common.ValueObject`
Base for compositional value objects. Derived classes override
`GetEqualityComponents()` to enumerate the fields that participate in
equality — no reflection, no attributes.

### `Common.DomainResult<T>`
Discriminated result for factories that must model success/failure without
exceptions.

| Property | Why |
|---|---|
| `IsSuccess` / `IsFailure` | Explicit consumer-facing discriminator. |
| `Value` | The success payload. Throws when read on failure. |
| `Errors` | Human-readable failure messages. |

### `Validation.ValidationFailure`
| Property | Why |
|---|---|
| `PropertyName` | Dot-notated path of the failing property. |
| `ErrorCode` | Machine-readable stable identifier (e.g. `RANGE`, `REQUIRED`). |
| `Message` | Human-readable message for logs/UI. |

### `Validation.ValidationResult`
| Property | Why |
|---|---|
| `Failures` | Frozen list of `ValidationFailure`. |
| `IsValid` / `IsInvalid` | Consumer-side discriminator. |
| `Merge(other)` | Combine outputs from sub-validators. |
| `ThrowIfInvalid()` | Enforcement path for callers that prefer exceptions. |

---

## 2. Documents

### `Documents.DocumentFileVersionKey` (readonly record struct)
Composite key `(DocumentFilePartId, VersionPartId)` — the enumeration cursor.
Implements `IComparable<>` so it can be used as a keyset-pagination boundary.
`Origin` sentinel represents the beginning of enumeration.

### `Documents.DataFileVersionKey` (readonly record struct)
Composite key `(DocumentFilePartId, DataFileVersionId)` — used for the
per-document BLOB point lookup.

### `Documents.IdempotencyKey` (readonly record struct)
32-byte SHA-256 of the big-endian concatenation of the three int64 IDs.
Reasons: stability across processes, uniform sharding, no source-ID leakage
into filenames.

| Member | Why |
|---|---|
| `For(part, ver, dataFileVer)` | Constructs the key deterministically. |
| `AsSpan()` / `ToArray()` | Access the raw bytes for DB parameters. |
| `ToHex()` | Lowercase hex representation for manifest lines. |
| `ShardPrefix1` / `ShardPrefix2` | Two-char hex for filesystem fan-out. |

### `Documents.DocumentMetadata`
Descriptive attributes, extracted from the descriptor so it can flow
independently.

| Property | Why |
|---|---|
| `Title` | Original title from the source; preserved for audit. |
| `Extension` | File extension without leading dot; empty when absent. |
| `LogicalFileSize` | Uncompressed size; the sink verifies bytes written against this. |
| `PhysicalFileSize` | On-disk / compressed size as recorded upstream. |
| `LastWriteTimeUtc` | Last-write timestamp for chronological reporting. |

### `Documents.DocumentBlob`
Address + envelope for the binary payload. Never holds bytes.

| Property | Why |
|---|---|
| `Key` | Composite key that addresses the BLOB in the source. |
| `DeclaredLogicalSize` | Sink verifies bytes-written matches this value. |
| `DeclaredContentType` | Optional MIME type; derived from extension when missing. |
| `HasContent` | Convenience: is the source claiming a non-empty payload? |

### `Documents.DocumentDescriptor`
The unit of work in the pipeline. Composes the three above plus a computed
`IdempotencyKey`.

| Property | Why |
|---|---|
| `DocumentFileVersionKey` | Enumeration cursor for keyset resume. |
| `Blob` | Addressing for the BLOB fetch stage. |
| `Metadata` | Descriptive fields for reporting/manifest. |
| `IdempotencyKey` | Computed once at construction — cheap to compare. |
| `Title` / `Extension` / `LogicalFileSize` / ... | Forwarders to `Metadata` for backward compatibility. |

### `Documents.ExportStatus` (enum)
Terminal outcome band for a document: `Unknown`, `Pending`, `Succeeded`,
`Failed`, `Skipped`. Values are stable — never renumber.

### `Documents.ExportOutcome`
Compact terminal outcome used by the throughput-critical pipeline stages
(one per document).

| Property | Why |
|---|---|
| `IdempotencyKey` | The dedup key. |
| `DocumentFileVersionKey`, `DataFileVersionKey` | Cross-references to the source. |
| `Status` | Terminal state. |
| `BytesWritten` | Sink measured value. |
| `OutputPath` | Where the artifact ended up (null on failure/skip). |
| `Checksum` | SHA-256 of the payload. |
| `FailureReason` | Set on Failed/Skipped. |
| `ObservedAtUtc` | Time of observation. |
| `AttemptNumber` | 1-based; enables retry counting. |

### `Documents.ExportProgress`
Point-in-time progress snapshot.

| Property | Why |
|---|---|
| `TotalRecorded` / `TotalSucceeded` / `TotalFailed` / `TotalSkipped` | Aggregate counters. |
| `TotalBytesWritten` | Cumulative byte counter for byte-rate math. |
| `LastCheckpoint` | Most recent observed enumeration cursor. |
| `StartedAtUtc` / `ObservedAtUtc` | Bookend timestamps used to derive throughput. |
| `Elapsed`, `DocumentsPerSecond`, `MebibytesPerSecond` | Computed. |

---

## 3. Jobs

### `Jobs.ExportJobId` (readonly record struct)
Strongly-typed wrapper over `long`. `Unassigned` sentinel for entities not
yet persisted.

### `Jobs.ExportJobStatus` (enum) and `ExportJobStatusTransitions`
State machine: `Pending → Running → Paused ⇄ Running → Completed|Failed|Cancelled → Archived`.
`IsAllowed(from, to)` centralizes the allowed transitions so no adapter can
invent an illegal one.

### `Jobs.ExportConfiguration`
Domain projection of the Application-layer options tree — only the fields
that participate in business rules.

| Property | Why |
|---|---|
| `PartitionKey` | Scopes the enumeration cursor. Two jobs sharing this key share a checkpoint. |
| `BatchSize` | Rows fetched per enumeration query. Business-relevant: too small → too many round-trips; too large → memory pressure. |
| `ContentReaderConcurrency` / `SinkConcurrency` | Cap the parallel work; too high starves the source or the disk. |
| `MaxDocumentSizeMb` | Business guard — a runaway BLOB should not stall the pipeline. |
| `ProgressReportInterval` / `CheckpointFlushInterval` | RPO knobs — trade responsiveness against DB write rate. |
| `UseReadUncommittedForEnumeration` | Explicit policy on live-vault non-blocking reads. |
| `Retry` | The retry policy every I/O boundary inherits. |

### `Jobs.ExportJob` (aggregate root)
Root of the export process.

| Property | Why |
|---|---|
| `Id` | Surrogate identifier. |
| `JobName` | Operator label; unique with `PartitionKey`. |
| `SourceServer` / `SourceDatabase` | Provenance for audit. |
| `Configuration` | Immutable snapshot for the whole run. |
| `TotalDocumentsExpected` | Pre-flight count; used for ETA. |
| `StartedAtUtc` / `CompletedAtUtc` | Job bookends. |
| `Status` | Lifecycle discriminator. |
| `CancellationReason` | Free text for Failed / Cancelled. |
| `CreatedAtUtc` / `CreatedBy` | Standard audit fields. |
| `Elapsed`, `IsTerminal` | Computed helpers. |

Transitions are methods that return a new instance (`MarkStarted`,
`MarkCompleted`, ...). Illegal transitions throw
`InvalidOperationException`.

---

## 4. Batches

### `Batches.ExportBatchId` (readonly record struct)
Same design as `ExportJobId` — long-backed strongly-typed ID.

### `Batches.BatchStatus` (enum)
`Created → Enumerated → Processing → Completed | Failed`.

### `Batches.ExportBatch`

| Property | Why |
|---|---|
| `Id` | Surrogate key. |
| `JobId` | Owning job. |
| `FromExclusive` / `ToInclusive` | Cursor range this batch covers — makes batches reproducible. |
| `ExpectedCount` | Enumeration produced this many rows. |
| `ProcessedCount` / `SuccessCount` / `FailureCount` / `SkipCount` | Live counters — kept small so `AccrueOutcome` is trivial. |
| `Status` | Lifecycle. |
| `CreatedAtUtc` / `StartedAtUtc` / `CompletedAtUtc` | Standard timestamps. |
| `IsFullyAccountedFor` | Predicate that decides when to mark the batch Complete. |

`AccrueOutcome(succeeded, failed, skipped)` returns a new instance with
counters advanced and Status possibly promoted.

---

## 5. Workers

### `Workers.ExportWorkerId` (readonly record struct)

### `Workers.WorkerStatus` (enum)
`Registered → Active ⇄ Idle → Stalled → Stopped | Failed → Archived`.

### `Workers.WorkerHeartbeat`
| Property | Why |
|---|---|
| `WorkerId` | The subject of the beat. |
| `ReportedStatus` | Self-reported status at the moment. |
| `ObservedAtUtc` | Freshness indicator. |
| `CurrentBatchId` | For detecting stuck workers. |
| `DocumentsPerSecond`, `BytesPerSecond` | Optional throughput samples. |
| `Age(now)` | Convenience for the stall sweep. |

### `Workers.ExportWorker`
| Property | Why |
|---|---|
| `Id` | Surrogate. |
| `JobId` | Owning job. |
| `WorkerName` / `MachineName` / `ProcessId` | Provenance for operator forensics. |
| `AssignedPartition` | The partition this worker is authoritative over. |
| `Concurrency` | Per-stage concurrency the worker was launched with. |
| `RegisteredAtUtc` / `StartedAtUtc` / `StoppedAtUtc` | Standard lifecycle timestamps. |
| `LastHeartbeat` | Most-recent beat; drives the `Status`. |
| `Status` | Lifecycle. |

---

## 6. Progress

### `Progress.ThroughputMetrics` (readonly record struct)
Composite value type wrapping documents/s and MiB/s. `From(documents, bytes,
elapsed)` factory prevents divide-by-zero.

### `Progress.ExportCheckpoint`

| Property | Why |
|---|---|
| `JobId` / `PartitionKey` | Composite scope. |
| `Cursor` | The high-water mark cursor. |
| `DocumentsProcessed` | Running total for this partition. |
| `CheckpointAtUtc` | When the checkpoint was recorded. |

`TryAdvance(candidate, ...)` enforces monotonicity — a lower candidate is
rejected in-place.

### `Progress.ExportStatistics`
Run-wide aggregate. Distinct from `ExportProgress` because *snapshot* and
*summary* have different consumers (dashboards vs. reports).

| Property | Why |
|---|---|
| `JobId` | Owning job. |
| `StartedAtUtc` / `CompletedAtUtc` | Elapsed calculation. |
| `Total*` | Terminal counters. |
| `TotalBytesWritten` | Byte-rate math. |
| `PeakDocumentsPerSecond` / `PeakMebibytesPerSecond` | High-water throughput. |
| `Elapsed`, `Average*PerSecond`, `FailureRatio` | Computed helpers. |

---

## 7. Results

### `Results.ExportResult`
Fuller per-document outcome used by operator-facing tooling.

| Property | Why |
|---|---|
| `JobId` / `WorkerId` | Context. |
| `IdempotencyKey` | Dedup identity. |
| `DocumentFileVersionKey` / `DataFileVersionKey` | Cross-reference to source. |
| `Status` | Terminal state. |
| `BytesWritten` | For byte-rate math. |
| `OutputPath` | Where the artifact was written. |
| `PayloadChecksum` | Verification aid. |
| `FailureReason` | Explanation string. |
| `AttemptNumber` | Retry counter. |
| `ObservedAtUtc` | Observation time. |
| `IsArtifactBearing` | Convenience discriminator. |

---

## 8. Retry

### `Retry.RetryPolicy`
Deterministic description of a retry schedule. Consumer-agnostic — Polly,
Azure SDK, or hand-rolled retry all interpret the same fields.

| Property | Why |
|---|---|
| `MaxAttempts` | Ceiling on retry count. |
| `InitialDelay` | Base of the exponential backoff. |
| `MaxDelay` | Cap. |
| `BackoffMultiplier` | Growth factor. |
| `UseJitter` | Randomization on/off. |
| `AttemptTimeout` | Per-attempt timeout envelope. |

`ComputeDelay(attempt)` reproduces the schedule; `Default` and
`ForBlobRead` are canned policies.

---

## 9. Errors

### `Errors.ErrorSeverity` (enum)
`Warning`, `Error`, `Critical`.

### `Errors.ErrorCategory` (enum)
`Transient`, `Deterministic`, `Configuration`, `Security`, `Storage`,
`Unknown` — orthogonal to severity.

### `Errors.ErrorRecord`
| Property | Why |
|---|---|
| `JobId` / `WorkerId` | Context. |
| `DocumentFileVersionKey` / `DataFileVersionKey` / `IdempotencyKey` | Optional per-document context. |
| `Severity` | Impact band. |
| `Category` | Failure kind. |
| `Source` | Pipeline stage. |
| `ExceptionType` / `Message` / `StackTrace` | Forensic detail. |
| `AttemptNumber` | Retry counter. |
| `OccurredAtUtc` | Observation time. |

`FromException(...)` is the canonical constructor from a caught .NET
exception.

---

## 10. Manifest

### `Manifest.ExportManifestEntry`
One row of the JSON-lines manifest. Every field is present so consumers can
read the manifest without cross-referencing anything else.

| Property | Why |
|---|---|
| `IdempotencyKey` | Cross-reference. |
| `DocumentFilePartId` / `VersionPartId` / `DataFileVersionId` | Flat identifiers so consumers do not need to decompose the composite keys. |
| `Title` / `Extension` / `DeclaredLogicalSize` | Metadata snapshot. |
| `Status` | Terminal state. |
| `BytesWritten` | Sink measurement. |
| `OutputPath` / `Checksum` / `FailureReason` | Artifact details. |
| `ObservedAtUtc` / `AttemptNumber` | Observation context. |

`From(result, metadata)` projects an `ExportResult` + `DocumentMetadata`
into a manifest row.

### `Manifest.ExportManifest`
Materialized manifest — a collection of entries plus job header.

| Property | Why |
|---|---|
| `JobId` / `JobName` | Header for the manifest file. |
| `StartedAtUtc` / `CompletedAtUtc` | Bookends. |
| `Entries` | Immutable ordered list. |
| `Count`, `SucceededCount`, `FailedCount`, `SkippedCount`, `TotalBytesWritten` | Convenience rollups. |

---

## Serialization

Everything above serializes cleanly with `System.Text.Json` using default
settings:

```csharp
var json = JsonSerializer.Serialize(entry);
var back = JsonSerializer.Deserialize<ExportManifestEntry>(json);
```

`IdempotencyKey`, `DocumentFileVersionKey`, `DataFileVersionKey`, and the
strongly-typed IDs are `readonly record struct` and serialize as objects
with their component fields. For a compact wire format (e.g. hex-encoded
`IdempotencyKey`), add a `JsonConverter` at the boundary rather than
inside the domain.

---

## Validation

Two-tier strategy:

1. **Invariants** in constructors — enforced with
   `ArgumentNullException.ThrowIfNull`, `ArgumentOutOfRangeException.ThrowIfNegative`,
   and domain-specific `throw new ArgumentException` for cross-field rules.
2. **Business-rule validation** via a `Validate()` method returning
   `ValidationResult`. Callers choose between inspection (`IsValid`) or
   enforcement (`ThrowIfInvalid()`).

`ExportConfiguration.Validate()` and `RetryPolicy.Validate()` demonstrate
the pattern; extend it to any aggregate that grows conditional business
rules.

---

## What the domain intentionally does NOT contain

- **No M-Files schema names.** Column names appear only in the Persistence
  layer.
- **No infrastructure primitives.** No `SqlConnection`, no `Stream`, no
  `HttpClient`.
- **No configuration binding.** Options binding lives in Configuration; the
  domain receives already-validated snapshots.
- **No logging.** The domain returns results; observation is the job of the
  caller.
- **No async.** Domain methods are synchronous CPU-bound transformations.
  I/O is the Persistence layer's responsibility.
