# Configuration Framework

> _Project: `MFilesExporter.Configuration`_
> _Root section: `Exporter`_
> _Root type: `MFilesExporter.Configuration.Options.ExporterOptions`_

## 1. Design

- One root type — **`ExporterOptions`** — bound to the `Exporter` configuration
  section. Every subsystem owns its own strongly-typed options class hanging
  off the root.
- Each sub-options class is registered as a **singleton** in DI so consumer
  services depend only on the slice they actually need (`StorageOptions`,
  `RetryHandlingOptions`, ...) rather than on the whole tree.
- Validation is **FluentValidation-backed** via `FluentValidateOptions<T>` and
  runs at host build time (`ValidateOnStart`) — the host refuses to run if
  any rule fails.
- Everything is **immutable-at-runtime**: after `Host.Build()` there is no
  writer path. Change appsettings, restart the process.

## 2. Configuration sources and precedence

Later sources override earlier ones:

1. `appsettings.json` — committed defaults, shipped with the binary.
2. `appsettings.{Environment}.json` — per-environment overrides (Development, Staging, Production).
3. **Environment variables** prefixed `MFILESEXPORTER_`, with `__` between sections.
4. **Command-line arguments** — `--Exporter:Pipeline:ContentReaderConcurrency=32`.

### Environment-variable mapping

```
MFILESEXPORTER_Exporter__Source__ConnectionString="Server=vault-db;Database=MFilesVault;Integrated Security=True;Encrypt=True;"
MFILESEXPORTER_Exporter__TrackingDatabase__ConnectionString="Server=tracking-db;Database=MFilesExportTracking;Integrated Security=True;"
MFILESEXPORTER_Exporter__Storage__RootPath="/data/export/documents"
MFILESEXPORTER_Exporter__Pipeline__ContentReaderConcurrency=16
MFILESEXPORTER_Exporter__BatchProcessing__BatchSize=2000
MFILESEXPORTER_Exporter__RetryHandling__SqlRead__MaxAttempts=8
```

### Secret handling

Connection strings and any other secret MUST be provided via one of:

- **User Secrets** in development (`dotnet user-secrets set ...`).
- **Environment variables** in staging / production containers.
- **Azure Key Vault**, **AWS Secrets Manager**, or **HashiCorp Vault** in
  production — bind through the appropriate `IConfigurationBuilder`
  provider before host build.

Never commit a real connection string, credential, OTLP token, or
certificate path into any `appsettings*.json`.

## 3. Section layout

```
Exporter
├── Source                  # SQL connection to the M-Files vault
├── TrackingDatabase        # SQL connection to the exporter's own tracking DB
├── StateStore              # SQLite/Postgres for local state
├── Storage                 # Output directory tree + manifests
├── FileExport              # File-sink strategy + shard layout
├── Metadata                # CSV / JSON / manifest emission
├── Pipeline                # Channel capacities + stage concurrency + timers
├── BatchProcessing         # Batch size, per-batch parallelism, failure gates
├── SqlStreaming            # SqlDataReader / GetBytes tuning + BLOB timeout
├── Validation              # Post-export validation pipeline (7 validators)
├── Checkpoint              # WAL directory, fsync, SQL reconciliation
├── RetryHandling           # Retry engine (per-operation profiles + circuit breakers)
└── Telemetry               # OpenTelemetry service name, Prometheus, OTLP
```

Logging is not owned by `ExporterOptions`; it lives under the top-level
`Serilog` section — see `docs/logging.md`.

## 4. Section reference

### 4.1 `Exporter:Source` — M-Files SQL connection

Type: `MFilesSourceOptions`. Read-only access to the vault.

| Field | Default | Meaning |
|-------|---------|---------|
| `ConnectionString`                  | *(required)* | ADO.NET connection string. |
| `CommandTimeoutSeconds`             | `120`        | Default `SqlCommand` timeout. |
| `EnumerationBatchSize`              | `1000`       | Rows per keyset-paginated round-trip. |
| `UseReadUncommittedForEnumeration`  | `true`       | Wraps enumeration in `SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED`. |
| `PartitionKey`                      | `"default"`  | Logical partition — enables horizontal sharding. |
| `Tables.DocumentFileVersion`        | `"DOCUMENTFILEVERSION"` | Source table name (override for renamed vaults). |
| `Tables.DataFileVersion`            | `"DATAFILEVERSION"`      | Source table name. |
| `Tables.DataFileVersionBytes`       | `"DATAFILEVERSION_BYTES"`| Source table name. |

### 4.2 `Exporter:TrackingDatabase` — exporter tracking DB

Type: `TrackingDatabaseOptions`. Read/write to the exporter's own operational database.

| Field | Default | Meaning |
|-------|---------|---------|
| `ConnectionString`         | *(required)* | ADO.NET to `MFilesExportTracking`. |
| `CommandTimeoutSeconds`    | `30`         | Per-command timeout. |
| `BatchSize`                | `500`        | Batch size for metric/progress flushes. |
| `MetricFlushInterval`      | `00:00:02`   | Max age before a partial batch flushes. |
| `ActorNameOverride`        | *(null)*     | Overrides `SUSER_SNAME()` for sproc `@ActorName`. |

### 4.3 `Exporter:StateStore` — local state store

Type: `StateStoreOptions`. SQLite (WAL mode) by default.

| Field | Default | Meaning |
|-------|---------|---------|
| `Provider`               | `"sqlite"` | Driver alias. `postgres` planned. |
| `ConnectionString`       | `"./export-output/state.db"` | For SQLite — filesystem path. |
| `EnableMemoryMappedIo`   | `true`     | SQLite `PRAGMA mmap_size`. |
| `CacheSizeKib`           | `65 536`   | SQLite `PRAGMA cache_size` (negative kibibytes). |
| `WalCheckpointInterval`  | `00:05:00` | Explicit `wal_checkpoint(TRUNCATE)` cadence. |

### 4.4 `Exporter:Storage` — output tree

Type: `StorageOptions`. Root layout for exported artifacts.

| Field | Default | Meaning |
|-------|---------|---------|
| `RootPath`                    | `./export-output/documents` | Documents root. |
| `ManifestPath`                | `./export-output/manifests` | Manifest root. |
| `ShardDepth`                  | `2`          | Levels of hash-shard nesting under `RootPath`. |
| `WriteBufferSize`             | `81 920`     | `FileStream` buffer bytes. |
| `ManifestRotationEntryCount`  | `100 000`    | Rotate manifest after N records. |
| `FsyncManifestOnRotate`       | `true`       | `File.Flush(true)` at each rotation. |
| `PreserveOriginalFilename`    | `true`       | Emit files as `{TITLE}.{EXTENSION}`. |
| `MinimumFreeSpaceGb`          | `50`         | Health check refuses to start below this. |

### 4.5 `Exporter:FileExport` — sink strategy

Type: `FileExportOptions`. Controls the file-export engine.

| Field | Default | Meaning |
|-------|---------|---------|
| `RootPath`             | `./export-output/documents` | Output root. |
| `FolderStrategy`       | `HashSharded` | Layout kind (`Flat`, `HashSharded`, `NumericShard`, `Date`, `Category`, `ShardedByDate`). |
| `ShardDepth`           | `2`  | Hex chars per shard level (1–4). |
| `NumericBucketCount`   | `512`| Buckets for `NumericShard`. |
| `DateFolderPattern`    | `yyyy/MM` | Path pattern for `Date` / `ShardedByDate`. |
| `DuplicateResolution`  | `IdempotencyKeySuffix` | Collision behaviour. |
| `MaxFilenameLength`    | `200` | Truncation ceiling. |
| `MaxFullPathLength`    | `240` | Path-length ceiling before hash-fallback. |
| `DefaultTitle`         | `untitled` | Fallback when TITLE is empty. |
| `DefaultExtension`     | `bin`      | Fallback when EXTENSION is empty. |
| `WriteBufferSize`      | `81 920`   | `FileStream` buffer. |
| `FsyncOnWrite`         | `true`     | `Flush(true)` per file. |
| `OverwriteOnCollision` | `false`    | If false, duplicate resolver picks a new name. |

### 4.6 `Exporter:Metadata` — CSV/JSON/manifest

Type: `MetadataOptions`.

| Field | Default | Meaning |
|-------|---------|---------|
| `OutputDirectory`     | `./export-output/metadata` | Target dir. |
| `WriteCsv`            | `true` | Emit `metadata.csv`. |
| `WriteJson`           | `true` | Emit `metadata.json`. |
| `WriteManifest`       | `true` | Emit run-level `manifest.json`. |
| `CsvFileName`         | `metadata.csv` | CSV filename. |
| `JsonFileName`        | `metadata.json` | JSON filename. |
| `ManifestFileName`    | `manifest.json` | Manifest filename. |
| `CsvDelimiter`        | `","` | Comma — set to `"\t"` for TSV. |
| `CsvIncludeUtf8Bom`   | `true` | UTF-8 BOM so Excel opens Unicode CSVs correctly. |
| `CsvIncludeHeader`    | `true` | Header row. |
| `JsonIndent`          | `false`| Compact JSON (recommended). |
| `IncludeExtensionFields` | `true` | Emits IdempotencyKey + DataFileVersionId. |
| `FlushEveryNRecords`  | `500`  | Flush cadence. |

### 4.7 `Exporter:Pipeline` — orchestrator

Type: `PipelineOptions`. Channels and stage concurrency for the export pipeline.

| Field | Default | Meaning |
|-------|---------|---------|
| `EnumerationChannelCapacity` | `5000` | Bounded channel between producer and content readers. |
| `ContentChannelCapacity`     | `128`  | Bounded channel between content readers and sink. |
| `ContentReaderConcurrency`   | `8`    | Content-reader worker count. |
| `SinkConcurrency`            | `8`    | Sink-stage worker count. |
| `ProgressReportInterval`     | `00:00:05` | Progress report cadence. |
| `CheckpointFlushInterval`    | `00:00:02` | Cadence for background checkpoint flush. |
| `OutcomeBatchSize`           | `200`  | Rows per outcome batch flush to tracking DB. |
| `OutcomeBatchFlushInterval`  | `00:00:02` | Max partial-batch age. |
| `MaxDocumentSizeMb`          | `0`    | 0 disables the size guard. Set to skip oversize docs. |

### 4.8 `Exporter:BatchProcessing` — batch engine

Type: `BatchProcessingOptions`.

| Field | Default | Meaning |
|-------|---------|---------|
| `BatchSize`               | `2000` | Documents per batch. Sweet spot 1 000–5 000. |
| `MaxParallelismPerBatch`  | `16`   | Concurrent item processors within a batch. |
| `BatchTimeout`            | `00:30:00` | Hard timeout — cancels the batch. |
| `PauseBetweenBatches`     | `00:00:00` | Delay inserted between batches. |
| `FailureRateThreshold`    | `0.5`  | Stops the run when per-batch failures exceed this ratio. `1.0` disables. |
| `StopOnFirstFailure`      | `false`| Rarely appropriate — use failure-rate threshold instead. |

### 4.9 `Exporter:SqlStreaming` — SQL streaming engine

Type: `SqlStreamingOptions`.

| Field | Default | Meaning |
|-------|---------|---------|
| `FetchSize`                         | `1 000` | Rows per keyset page. |
| `CommandTimeoutSeconds`             | `120`   | Metadata command timeout. |
| `BlobCommandTimeoutSeconds`         | `600`   | BLOB read timeout — larger than metadata. |
| `NetworkPacketSizeBytes`            | `8 192` | TDS packet size. Max 32 768. |
| `UseReadUncommittedForEnumeration`  | `true`  | Applied to metadata query only. |
| `ProgressReportInterval`            | `00:00:05` | Progress tick cadence. |
| `MaxRetryAttempts`                  | `5`     | Retries per SQL operation. |
| `RetryBaseDelay`                    | `250 ms`| Exponential backoff base. |
| `RetryMaxDelay`                     | `30 s`  | Exponential backoff ceiling. |

### 4.10 `Exporter:Validation` — post-export validators

Type: `ExportValidationOptions`. See `docs/export-validation-framework.md`.

| Field | Default | Meaning |
|-------|---------|---------|
| `Enabled`                     | `true`     | Master switch. |
| `Mode`                        | `FailFast` | `FailFast` or `RunAll`. |
| `EnabledValidators`           | *(empty)*  | Allowlist by name. Empty = every validator. |
| `PerValidatorTimeout`         | `00:02:00` | Timeout per validator → retryable failure. |
| `RerunChecksumFromFile`       | `true`     | Re-hashes from disk. |
| `AllowExtensionMismatch`      | `false`    | Downgrades mismatch to warning. |
| `ValidateMetadataConsistency` | `true`     | Include metadata check. |

### 4.12 `Exporter:Checkpoint` — checkpoint engine

Type: `CheckpointOptions`.

| Field | Default | Meaning |
|-------|---------|---------|
| `WalDirectory`           | `./export-output/checkpoints` | WAL file location. |
| `FsyncOnWrite`           | `true`  | Force fsync after every WAL write — do not disable in production. |
| `PersistToTrackingDb`    | `true`  | Also mirror to SQL tracking DB. |
| `SqlSaveTimeout`         | `00:00:15` | Per-save timeout. |
| `ReconcileSqlOnRecovery` | `true`  | Re-save WAL value when it exceeds SQL on recovery. |

### 4.13 `Exporter:RetryHandling` — enterprise retry engine

Type: `RetryHandlingOptions`. See `docs/retry-handling.md`.

| Field | Meaning |
|-------|---------|
| `Enabled`         | Master switch. |
| `Default`         | Fallback profile for unknown operations. |
| `SqlRead` / `SqlBlobRead` / `SqlWrite` / `DiskRead` / `DiskWrite` / `StateStore` / `Network` | Per-operation profiles. |
| `Categories`      | Per-category overrides for `SqlDeadlock`, `DiskFull`, `RateLimited`. |

Each profile:

| Field | Range | Meaning |
|-------|-------|---------|
| `MaxAttempts`             | 1–100  | Retry count cap. |
| `BaseDelayMilliseconds`   | 0–60 000 | Base of the exponential back-off. |
| `MaxDelaySeconds`         | 0–3 600  | Ceiling of the exponential back-off. |
| `PerAttemptTimeoutSeconds`| ≥0       | Cancels a single attempt and retries. |
| `JitterFactor`            | 0.0–1.0  | Full-jitter multiplier. |
| `CircuitBreaker`          | –        | Enabled by default. |

### 4.14 `Exporter:Telemetry` — monitoring

Type: `TelemetryOptions`. OpenTelemetry service identity + exporter wiring.

| Field | Default | Meaning |
|-------|---------|---------|
| `ServiceName`               | `"mfiles-exporter"` | OTel resource attribute. |
| `ServiceNamespace`          | `"seamfix"`         | OTel resource attribute. |
| `ServiceVersion`            | `"1.0.0"`           | OTel resource attribute. |
| `EnablePrometheusEndpoint`  | `true`              | Serves `/metrics`. |
| `PrometheusListenerUrl`     | `http://+:9464/`    | HTTP listener URL. |
| `EnableOtlpExporter`        | `false`             | OTLP push. |
| `OtlpEndpoint`              | *(null)*            | Required when OTLP is enabled — validated. |
| `TraceSamplingRatio`        | `0.05`              | 0.0–1.0 head-sampler. |

## 5. Validation

Every options class has a matching FluentValidation validator under
`MFilesExporter.Configuration.Validation`. The root
`ExporterOptionsValidator` composes them all and is registered against
`IValidateOptions<ExporterOptions>` via `FluentValidateOptions<T>`.

Validation fires at host build time — start-up fails fast rather than lazily:

```csharp
services.AddOptions<ExporterOptions>()
    .Bind(configuration.GetSection(ExporterOptions.SectionName))
    .ValidateOnStart();
```

Errors are surfaced as `OptionsValidationException` containing every failed
rule (path + message), so operators get a complete picture in one shot
rather than fixing one field per restart.

### Cross-field rules

Some invariants span more than one field:

- `Telemetry.OtlpEndpoint` is required when `Telemetry.EnableOtlpExporter`
  is true — validated via `When(...)`.
- `Telemetry.PrometheusListenerUrl` is required when
  `Telemetry.EnablePrometheusEndpoint` is true.

## 6. Dependency injection

Registered by `AddExporterConfiguration(IConfiguration)`:

```csharp
services.AddOptions<ExporterOptions>()
    .Bind(configuration.GetSection(ExporterOptions.SectionName))
    .ValidateOnStart();

services.AddSingleton<IValidator<ExporterOptions>, ExporterOptionsValidator>();
services.AddSingleton<IValidateOptions<ExporterOptions>>(sp =>
    new FluentValidateOptions<ExporterOptions>(null,
        sp.GetRequiredService<IValidator<ExporterOptions>>()));

// Sub-options exposed as singletons for direct injection.
services.AddSingleton(sp => sp.GetRequiredService<IOptions<ExporterOptions>>().Value.Source);
services.AddSingleton(sp => sp.GetRequiredService<IOptions<ExporterOptions>>().Value.Storage);
services.AddSingleton(sp => sp.GetRequiredService<IOptions<ExporterOptions>>().Value.Pipeline);
services.AddSingleton(sp => sp.GetRequiredService<IOptions<ExporterOptions>>().Value.StateStore);
services.AddSingleton(sp => sp.GetRequiredService<IOptions<ExporterOptions>>().Value.TrackingDatabase);
services.AddSingleton(sp => sp.GetRequiredService<IOptions<ExporterOptions>>().Value.BatchProcessing);
services.AddSingleton(sp => sp.GetRequiredService<IOptions<ExporterOptions>>().Value.SqlStreaming);
services.AddSingleton(sp => sp.GetRequiredService<IOptions<ExporterOptions>>().Value.FileExport);
services.AddSingleton(sp => sp.GetRequiredService<IOptions<ExporterOptions>>().Value.Metadata);
services.AddSingleton(sp => sp.GetRequiredService<IOptions<ExporterOptions>>().Value.Validation);
services.AddSingleton(sp => sp.GetRequiredService<IOptions<ExporterOptions>>().Value.Checkpoint);
services.AddSingleton(sp => sp.GetRequiredService<IOptions<ExporterOptions>>().Value.RetryHandling);
services.AddSingleton(sp => sp.GetRequiredService<IOptions<ExporterOptions>>().Value.Telemetry);
```

Consumer classes ask for the slice they need:

```csharp
public sealed class FileExportEngine
{
    public FileExportEngine(FileExportOptions options, IChecksumCalculatorFactory factory, ILogger<FileExportEngine> logger) { … }
}
```

Prefer this over injecting `IOptions<ExporterOptions>` — narrower dependencies
make refactoring safer and unit tests easier.

## 7. Sample `appsettings.json`

```jsonc
{
  "Exporter": {
    "Source": {
      "ConnectionString": "Server=vault-db;Database=MFilesVault;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Application Name=MFilesExporter",
      "CommandTimeoutSeconds": 120,
      "EnumerationBatchSize": 1000,
      "PartitionKey": "default"
    },

    "TrackingDatabase": {
      "ConnectionString": "Server=tracking-db;Database=MFilesExportTracking;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;",
      "CommandTimeoutSeconds": 30,
      "BatchSize": 500
    },

    "StateStore": {
      "Provider": "sqlite",
      "ConnectionString": "/data/export/state.db"
    },

    "Storage": {
      "RootPath": "/data/export/documents",
      "ManifestPath": "/data/export/manifests",
      "ShardDepth": 2,
      "MinimumFreeSpaceGb": 200
    },

    "FileExport": {
      "FolderStrategy": "ShardedByDate",
      "ShardDepth": 2,
      "DuplicateResolution": "IdempotencyKeySuffix",
      "FsyncOnWrite": true
    },

    "Metadata": {
      "OutputDirectory": "/data/export/metadata",
      "WriteCsv": true,
      "WriteJson": true,
      "WriteManifest": true
    },

    "Pipeline": {
      "EnumerationChannelCapacity": 5000,
      "ContentChannelCapacity": 128,
      "ContentReaderConcurrency": 16,
      "SinkConcurrency": 16,
      "OutcomeBatchSize": 500
    },

    "BatchProcessing": {
      "BatchSize": 2000,
      "MaxParallelismPerBatch": 16,
      "BatchTimeout": "00:30:00",
      "FailureRateThreshold": 0.25
    },

    "SqlStreaming": {
      "FetchSize": 1000,
      "CommandTimeoutSeconds": 120,
      "BlobCommandTimeoutSeconds": 600,
      "NetworkPacketSizeBytes": 32768
    },

    "Validation": {
      "Enabled": true,
      "Mode": "FailFast",
      "PerValidatorTimeout": "00:02:00"
    },

    "Checkpoint": {
      "WalDirectory": "/data/export/checkpoints",
      "FsyncOnWrite": true,
      "PersistToTrackingDb": true
    },

    "RetryHandling": {
      "Enabled": true,
      "SqlRead":     { "MaxAttempts": 5, "BaseDelayMilliseconds": 500, "MaxDelaySeconds": 30 },
      "SqlBlobRead": { "MaxAttempts": 5, "BaseDelayMilliseconds": 500, "MaxDelaySeconds": 30 },
      "DiskWrite":   { "MaxAttempts": 3, "BaseDelayMilliseconds": 250, "MaxDelaySeconds": 15 },
      "StateStore":  { "MaxAttempts": 5, "BaseDelayMilliseconds": 100, "MaxDelaySeconds":  5 }
    },

    "Telemetry": {
      "ServiceName": "mfiles-exporter",
      "ServiceNamespace": "seamfix",
      "ServiceVersion": "1.0.0",
      "EnablePrometheusEndpoint": true,
      "PrometheusListenerUrl": "http://+:9464/",
      "EnableOtlpExporter": false,
      "TraceSamplingRatio": 0.05
    }
  }
}
```

## 8. Testing

`ExporterOptionsValidatorTests` exercises the composed validator with:

- happy path (`Valid_Passes`),
- required-field missing (connection strings),
- range violations (`ShardDepth`, `BatchSize`, `MaxParallelismPerBatch`, `FetchSize`),
- ratio out of range (`FailureRateThreshold`),
- retry profile min/max attempts,
- cross-field: heartbeat > stalled,
- conditional: OTLP endpoint required when OTLP enabled,
- empty required paths (`Checkpoint.WalDirectory`, `Metadata.OutputDirectory`).

Add tests alongside future options fields — the validator is the single
place a mis-configuration is caught before code that expects the field
has a chance to break at 3 AM.

## 9. Anti-patterns

- **Don't inject `IOptionsMonitor<T>`** for options — nothing in the exporter
  supports live reload; monitor callbacks would fire but subsystems would
  still hold their initial snapshot.
- **Don't read `IConfiguration` directly** in domain / application code —
  bind an options class instead so validation catches drift.
- **Don't add a required field with a non-empty default** — if the default is
  a real value, callers won't notice the field is missing. Use `string.Empty`
  or a sentinel, and mark the field `NotEmpty` in the validator.
- **Don't build parallel option trees** — every new tuning knob belongs in
  the existing sub-options class, or a new one hung off `ExporterOptions`.
