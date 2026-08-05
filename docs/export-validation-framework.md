# Export Validation Framework

**Purpose.** Immediately after every single-document export, run a
deterministic set of checks against the on-disk artifact and the emitted
metadata. Classify every failure as either **retryable** (transient
filesystem hiccup) or **deterministic** (data corruption / config error)
so the retry engine can react correctly.

**Runs inline with each export** — not a batch verification step. Every
document's outcome is validated before it's marked Succeeded.

---

## 1. Seven validators

Every validator implements `IExportValidator` and is ordered so cheap
checks fail first — under `FailFast` mode we stop as soon as one fails,
never paying for the expensive checksum re-hash on a doomed export.

| # | Validator | Order | Cost | Retryable on failure? | Detects |
|---|---|---|---|---|---|
| 1 | `FileExistsValidator` | 0 | 1 stat | **Yes** — rename race | File missing |
| 2 | `OutputFolderValidator` | 10 | 1 stat + string compare | No (root breach) / Yes (dir missing) | Path outside root; missing directory |
| 3 | `ExtensionValidator` | 20 | pure | No (configurable → Warning) | Wrong file extension |
| 4 | `FileSizeValidator` | 30 | 1 stat | **No** — deterministic corruption | Truncated / over-long write |
| 5 | `ReadableValidator` | 40 | 1 open + 1-byte read | Yes on IO / Not-found; No on permission | Permission denied; locked file; corrupt FS state |
| 6 | `ChecksumValidator` | 50 | full re-hash | **No** — deterministic corruption | Bit-flip / silent write corruption |
| 7 | `MetadataConsistencyValidator` | 60 | pure | **No** — internal bug | Metadata catalog vs on-disk mismatch |

**Retryable vs Deterministic classification** is the single most
important decision each validator makes. A missing file at time of
validation could just be a rename race — retry the whole export and the
file will likely be there. A checksum mismatch, on the other hand, means
the file on disk disagrees with what the sink says it wrote — retrying
without changing the source of the corruption is pointless.

---

## 2. Pipeline

```
                     ┌──────────────────────────────┐
                     │  ExportValidationPipeline    │
                     │  (default IExportValidation- │
                     │   Pipeline)                  │
                     └──────────────┬───────────────┘
                                    │ sorted by Order
                                    ▼
              ┌─────────────────────────────────────────┐
              │  1. FileExistsValidator                 │
              │  2. OutputFolderValidator               │
              │  3. ExtensionValidator                  │
              │  4. FileSizeValidator                   │
              │  5. ReadableValidator                   │
              │  6. ChecksumValidator      (expensive)  │
              │  7. MetadataConsistencyValidator        │
              └──────────────┬──────────────────────────┘
                             ▼
              ┌─────────────────────────────────────────┐
              │  ExportValidationReport                 │
              │    Checks[], TotalElapsed,              │
              │    IsValid, HasFailures,                │
              │    HasWarnings,                         │
              │    AllFailuresRetryable, Failures       │
              └──────────────┬──────────────────────────┘
                             ▼
                        Reporters (fan-out)
                             │
                             ▼
                     Logs / metrics / audit
```

### FailFast vs RunAll

- **FailFast** (default) — stop on first failure. Best for production
  hot paths.
- **RunAll** — execute every validator regardless of failures.
  Recommended when running the pipeline post-mortem against a suspect
  export set.

Selected via `ExportValidationOptions.Mode`.

### Timeouts

Each validator runs under a per-validator timeout linked from the
caller's cancellation token. Exceeding the timeout produces a retryable
failure — filesystem stalls are common enough that we shouldn't fail
permanently for them.

---

## 3. Retry integration

The pipeline returns a report; the caller decides what to do. The
canonical caller is the batch item processor:

```csharp
var result = await _sink.WriteAsync(descriptor, contentStream, ct);

var validationCtx = new ExportValidationContext
{
    Descriptor            = descriptor,
    OutputPath            = result.OutputPath,
    ExpectedByteCount     = result.BytesWritten,
    ExpectedChecksumHex   = result.ChecksumHex,
    ExpectedExtension     = descriptor.Extension,
    ExpectedRootDirectory = _storageOptions.RootPath,
    MetadataRecord        = metadataRecord,     // when available
};

var report = await _validation.ValidateAsync(validationCtx, ct);

if (report.IsValid)
{
    // Mark item Completed
    await _workStore.CompleteAsync(item.WorkItemId, item.ClaimToken,
        result.OutputPath, result.ChecksumHex, result.BytesWritten, ct);
    return BatchItemResult.Succeeded(result.BytesWritten);
}
else if (report.AllFailuresRetryable)
{
    // Transient — let the reaper reclaim, another worker retries
    await _workStore.FailAsync(item.WorkItemId, item.ClaimToken,
        report.Failures.First().FailureReason ?? "validation transient",
        isPermanent: false, TimeSpan.FromSeconds(30), ct);
    return BatchItemResult.Failed("validation transient (will retry)");
}
else
{
    // Deterministic — no point retrying
    await _workStore.FailAsync(item.WorkItemId, item.ClaimToken,
        report.Failures.First().FailureReason ?? "validation permanent",
        isPermanent: true, TimeSpan.Zero, ct);
    return BatchItemResult.Failed("validation permanent");
}
```

The **classification of each failure as retryable vs deterministic** is
the entire product of the framework — every downstream decision falls out
of that flag.

---

## 4. Reporting

Reporters implement `IValidationReporter` and are called after the report
is composed. Multiple reporters may be registered — the pipeline fans out
to each. A faulty reporter never fails the export (exceptions are logged
and swallowed).

Shipped reporter: **`LoggingValidationReporter`**. Emits:

- `Debug` — validation passed silently.
- `Information` — passed with warnings.
- `Warning` — retryable failure; per-check drill-down at Warning.
- `Error` — deterministic failure; per-check drill-down at Error.

Extension ideas (not shipped, easy to add):

- **`MetricsValidationReporter`** — increment `validations.passed`,
  `validations.failed{retryable}`, `validations.failed{deterministic}`
  counters on the OpenTelemetry Meter.
- **`ErrorRecordValidationReporter`** — call `IExportErrorRepository.LogAsync`
  for every deterministic failure so operators see them in the tracking DB.
- **`ManifestValidationReporter`** — append every validation report to a
  JSONL log so failures can be re-analysed offline.

---

## 5. Configuration

```jsonc
{
  "Exporter": {
    "Validation": {
      "Enabled": true,
      "Mode": "FailFast",                        // FailFast | RunAll
      "EnabledValidators": [],                   // empty = every registered validator
      "PerValidatorTimeout": "00:02:00",
      "RerunChecksumFromFile": true,
      "AllowExtensionMismatch": false,
      "ValidateMetadataConsistency": true
    }
  }
}
```

Every knob has a sensible default. Producers usually only tune:

- **`EnabledValidators`** — e.g., `["FileExistsValidator", "FileSizeValidator", "ChecksumValidator"]`
  to skip the more expensive metadata check on high-throughput exports.
- **`AllowExtensionMismatch`** — during migrations where the target
  extension convention differs.
- **`RerunChecksumFromFile`** — turn off in trusted environments where
  the sink's inline hash is authoritative (saves a full-file re-read).

---

## 6. What each validator actually checks

### FileExistsValidator
- `File.Exists(OutputPath)` must be true.
- Empty path → deterministic failure.
- Missing file → **retryable** (rename race).

### OutputFolderValidator
- `OutputPath` starts with `ExpectedRootDirectory + Path.DirectorySeparatorChar`,
  case-sensitive on POSIX and case-insensitive on Windows.
- Parent directory must exist (`Directory.Exists`).
- Outside root → deterministic; missing parent → retryable.

### ExtensionValidator
- `Path.GetExtension(OutputPath)` (trimmed of leading dot) equals
  `ExpectedExtension` (case-insensitive).
- Configurable: mismatch → Warning instead of Failure when
  `AllowExtensionMismatch = true`.

### FileSizeValidator
- `new FileInfo(OutputPath).Length == ExpectedByteCount`.
- Any mismatch is **deterministic** — the write did not produce the
  expected byte count.

### ReadableValidator
- Open with `FileMode.Open, FileAccess.Read, FileShare.Read`.
- If length > 0, read one byte and confirm > 0 bytes returned.
- `UnauthorizedAccessException` → deterministic (permission).
- `IOException` / `FileNotFoundException` → **retryable**.

### ChecksumValidator
- Re-computes SHA-256 with an `IncrementalHash` + `ArrayPool<byte>`
  (80 KiB buffer). Never buffers the whole file.
- Skipped if `RerunChecksumFromFile = false` or if
  `ExpectedChecksumHex` is null/empty.
- IO error during hashing → retryable.
- Actual hash ≠ expected → **deterministic**.

### MetadataConsistencyValidator
- Cross-references the emitted `MetadataRecord` against the actual export:
  - `record.ExportPath == context.OutputPath`
  - `record.LogicalFileSize == context.ExpectedByteCount`
  - `record.Checksum == context.ExpectedChecksumHex` (if expected present)
  - `record.Extension == context.ExpectedExtension`
- Skipped if no record was supplied or the option is disabled.
- Any mismatch is **deterministic** and always represents an internal bug.

---

## 7. Memory + performance

- Every validator's steady-state memory is bounded — no LINQ enumeration
  over large collections, no in-memory buffering.
- Checksum re-hash uses `ArrayPool<byte>` and streams 80 KiB at a time
  through `IncrementalHash` — matches the sink's writer profile.
- `Interlocked`-free — the pipeline is single-threaded per export.
- `Stopwatch` per validator + per-report — cheap enough to enable
  everywhere.

At 500 docs/s with all seven validators enabled, expect ~2 ms
non-checksum + ~10 ms checksum on a 2 MiB average payload (NVMe). Skip
the checksum re-hash to remove that cost when the sink's inline hash is
authoritative.

---

## 8. Testing

Under `tests/MFilesExporter.Tests/Export/Validation/`:

- **`ValidatorTests`** — one or more tests per validator against a real
  temp filesystem: pass paths, fail paths, retryable-vs-not, skipped
  paths, warning downgrades.
- **`ExportValidationPipelineTests`** — orchestration behaviour:
  - Runs validators in `Order`.
  - `FailFast` stops on first failure.
  - `RunAll` executes every validator.
  - `AllFailuresRetryable` correctly reflects the mixed-failure case.
  - Validator exceptions become non-retryable failures.
  - Pipeline disabled → empty report immediately.
  - Allow-list filters properly.
  - Reporters receive the final report.

---

## 9. Extension hooks

- **Add a validator** — implement `IExportValidator`, pick an `Order`,
  register in DI. The pipeline automatically discovers it.
- **Add a reporter** — implement `IValidationReporter`, register in DI.
  All reporters receive every report.
- **Change classification** — never edit the shipped validators to change
  their retryability; instead configure a wrapping reporter that
  overrides the classification for your policy.

---

## 10. What this framework does NOT do

- **No batch-level verification.** For "verify all files after export
  finishes" workflows, run a separate tool that reads the manifest and
  invokes the same validators in `RunAll` mode. Not shipped — trivial to
  build.
- **No auto-remediation.** A failed validation reports the failure; it
  does not attempt to re-download or re-write the file. The caller
  decides via the retry integration hooks above.
- **No cryptographic-signature check.** If signed exports are required,
  add a `SignatureValidator` and a `SigningReporter` — both are single-
  file extensions.
