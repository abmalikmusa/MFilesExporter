# MFilesExporter

Enterprise-grade document export platform for migrating a ~5-million-document
M-Files vault out of SQL Server and onto durable storage. Built for a single
long-running job: streaming, resumable, idempotent, fault-tolerant, fully
observable.

- **.NET 9** · Clean Architecture · CQRS · SOLID · DDD
- **10 projects**, 306 unit tests, 20+ design docs
- **Zero-downtime** to the source: `READ UNCOMMITTED` metadata scan, no locks held
- **Content-addressed** sink with SHA-256 idempotency keys — safe to rerun
- **Observable end-to-end**: Serilog structured logs, OpenTelemetry metrics,
  Prometheus + OTLP exporters, Spectre.Console real-time dashboard

---

## Quick start

**Deploying to a Windows Server?** Follow the full runbook:
**[docs/deployment-runbook.md](docs/deployment-runbook.md)** — one linear
walk-through from a fresh box to a running export with `--status` reports.

**Running on a dev box** for exploration:

```bash
# 1. Restore + build
dotnet restore
dotnet build -c Release

# 2. Configure — edit connection strings + output paths
$EDITOR src/MFilesExporter.Console/appsettings.json

# 3. Provision the tracking database
sqlcmd -S <tracking-db-host> -d master -i database/00-database.sql
for f in database/1*.sql database/2*.sql database/3*.sql database/4*.sql \
         database/5*.sql database/6*.sql database/7*.sql; do
  sqlcmd -S <tracking-db-host> -d MFilesExportTracking -i "$f"
done

# 4. Run
dotnet run --project src/MFilesExporter.Console -c Release

# 5. Live monitoring reports (any time, from any box with tracking-DB access):
./bin/Release/net9.0/MFilesExporter.Console --status
```

The console shows a live Spectre dashboard while the pipeline runs (dev boxes
only — under a Windows Service it auto-suppresses). Structured JSON logs
stream to `logs/`; `--status` prints a formatted summary from the tracking DB
in under a second.

---

## Solution layout

```
MFilesExporter.sln
├── src/
│   ├── MFilesExporter.Domain            Aggregates, value objects, enums (pure)
│   ├── MFilesExporter.Shared            Cross-cutting primitives (Guards, IO, Collections)
│   ├── MFilesExporter.Configuration     ExporterOptions tree + FluentValidation
│   ├── MFilesExporter.Logging           Serilog composition + correlation/audit/perf/worker logs
│   ├── MFilesExporter.Application       Use cases, CQRS dispatcher, port interfaces
│   ├── MFilesExporter.Persistence       Microsoft.Data.SqlClient adapters
│   ├── MFilesExporter.Export            Streaming pipeline, sink, checkpoint, validation
│   ├── MFilesExporter.Reporting         Progress reporting, Spectre.Console dashboard
│   ├── MFilesExporter.Infrastructure    Retry, monitoring, health checks, OpenTelemetry
│   └── MFilesExporter.Console           Composition root + Program.cs + appsettings
├── tests/
│   └── MFilesExporter.Tests             306 unit tests (xUnit + FluentAssertions + NSubstitute)
├── database/                            SQL Server tracking DB DDL + sprocs (00–71)
├── deploy/
│   ├── grafana/dashboard.json           18-panel Grafana dashboard
│   └── prometheus/                      Scrape config + recording rules + alerts
└── docs/                                20+ design docs, one per subsystem
```

---

## Architecture at a glance

```
                 ┌────────────────── M-Files vault (SQL Server) ─────────────────┐
                 │  DOCUMENTFILEVERSION · DATAFILEVERSION · DATAFILEVERSION_BYTES │
                 └───────────────────────────────┬───────────────────────────────┘
                                                 │  keyset pagination
                                                 │  READ UNCOMMITTED
                                                 ▼
        ┌─────────────────────────────────────────────────────────────────────────────┐
        │   Producer (SQL Streaming Engine)  ──▶  bounded Channel<Descriptor>          │
        │      ▼                                                                       │
        │   Content Readers (N workers)      ──▶  bounded Channel<Document>            │
        │      ▼                                                                       │
        │   Sink Stage (M workers) — atomic temp-write + rename                        │
        │      ▼                                                                       │
        │   Outcome Collector — batched writes to Tracking DB                          │
        │      ▼                                                                       │
        │   Checkpoint Engine (WAL + SQL reconciliation)                               │
        └─────────────────────────────────────────────────────────────────────────────┘
                                                 │
                                                 ▼
                        Local FS ── manifest.json · metadata.csv · metadata.json
                                                 ▲
                                                 │
                        Tracking DB — jobs · batches · workers · outcomes · errors
```

Cross-cutting: retry executor wraps every I/O boundary; monitoring publishes
counters/histograms/observable gauges to Prometheus & OTLP; Serilog fans out
to five sinks (all / errors / audit / performance / workers).

---

## Documentation

Every subsystem has a dedicated design doc.

**Getting oriented**
- [Architecture overview](docs/architecture.md) — projects, layering, dependency rules
- [Configuration reference](docs/configuration.md) — every option under `Exporter:*`
- [Dependency injection guide](docs/dependency-injection.md)
- [Coding conventions](docs/conventions.md)

**Data access**
- [Domain model](docs/domain-model.md)
- [Database schema](docs/database.md) — tracking DB tables, sprocs, TVPs
- [Data access layer](docs/data-access-layer.md) — `Microsoft.Data.SqlClient` usage
- [SQL streaming engine](docs/sql-streaming-engine.md)
- [Binary object reader](docs/binary-object-reader.md) — BLOB streaming
- [Query performance review](docs/query-performance-review.md)

**Application layer**
- [Application layer](docs/application-layer.md) — CQRS dispatcher, use cases
- [Work-claiming engine](docs/work-claiming-engine.md) — fencing tokens
- [Batch processing engine](docs/batch-processing-engine.md)
- [Parallel processing engine](docs/parallel-processing-engine.md) — worker pool + channels

**Export & durability**
- [File export engine](docs/file-export-engine.md) — folder strategies + duplicate resolution
- [Metadata generation framework](docs/metadata-generation-framework.md)
- [Export validation framework](docs/export-validation-framework.md)
- [Checkpoint engine](docs/checkpoint-engine.md) — WAL + SQL dual durability

**Reliability & observability**
- [Retry handling](docs/retry-handling.md) — classifier + backoff + circuit breakers
- [Logging](docs/logging.md) — correlation/audit/performance/worker sinks
- [Monitoring](docs/monitoring.md) — OpenTelemetry + Prometheus + Grafana
- [Console dashboard](docs/console-dashboard.md) — Spectre.Console live view
- [Status reports](docs/status-reports.md) — `--status` CLI + SQL query pack (no external stack)

**Deployment**
- [Deployment runbook](docs/deployment-runbook.md) — **start here** — zero-to-running walkthrough
- [Windows Service reference](docs/deployment-windows-service.md) — deeper detail: publish, install, service account, troubleshooting

---

## Configuration

Everything lives under the `Exporter` section of `appsettings.json` and is
strongly typed via `ExporterOptions`. FluentValidation runs at host build
time — the process refuses to start on invalid config.

```jsonc
"Exporter": {
  "Source":             { "ConnectionString": "…", "PartitionKey": "default" },
  "TrackingDatabase":   { "ConnectionString": "…" },
  "StateStore":         { "Provider": "sqlite", "ConnectionString": "./state.db" },
  "Storage":            { "RootPath": "/data/export/documents" },
  "FileExport":         { "FolderStrategy": "ShardedByDate", "ShardDepth": 2 },
  "Metadata":           { "WriteCsv": true, "WriteJson": true, "WriteManifest": true },
  "Pipeline":           { "ContentReaderConcurrency": 16, "SinkConcurrency": 16 },
  "BatchProcessing":    { "BatchSize": 2000, "MaxParallelismPerBatch": 16 },
  "ParallelProcessing": { "WorkerCount": 16, "ChannelCapacity": 256 },
  "SqlStreaming":       { "FetchSize": 1000, "BlobCommandTimeoutSeconds": 600 },
  "Validation":         { "Enabled": true, "Mode": "FailFast" },
  "Checkpoint":         { "WalDirectory": "/data/checkpoints", "FsyncOnWrite": true },
  "RetryHandling":      { "Enabled": true, /* per-operation profiles */ },
  "Telemetry":          { "EnablePrometheusEndpoint": true, "PrometheusListenerUrl": "http://+:9464/" },
  "Dashboard":          { "Enabled": true, "RefreshInterval": "00:00:00.500" }
}
```

Overrides layered on top (later wins): `appsettings.{Env}.json` → environment
variables (`MFILESEXPORTER_Exporter__…`) → command-line arguments.
Full reference in [docs/configuration.md](docs/configuration.md).

---

## Observability

- **Structured logs** — `logs/mfilesexporter-*.log` (all), `errors-*.log`,
  `audit-*.log` (2555-day retention for compliance), `performance-*.log`,
  `workers-*.log`. Compact-JSON, correlation-id enriched.
- **Metrics** — Prometheus scrape on `:9464/metrics`. Business metrics
  (`mfilesexporter.*`) plus `System.Runtime` (CPU, memory, GC, thread-pool).
- **Grafana dashboard** — `deploy/grafana/dashboard.json`, 18 panels
  (throughput, queue depth, workers, retries, SQL/sink latency percentiles,
  memory/CPU/disk).
- **Alerts** — `deploy/prometheus/recording-rules.yml` includes
  `HighFailureRate`, `DiskLow`, `WorkersStalled`, `SqlP95High`,
  `QueueSaturated`.
- **Live console dashboard** — Spectre.Console real-time view with per-worker
  activity, current batch, ETA, resource meters. See
  [docs/console-dashboard.md](docs/console-dashboard.md).

---

## Development

```bash
# Full solution
dotnet build MFilesExporter.sln

# Tests (306 tests)
dotnet test MFilesExporter.sln

# Single test class
dotnet test --filter "FullyQualifiedName~RetryExecutorTests"

# Run the exporter locally against a test vault
dotnet run --project src/MFilesExporter.Console
```

**Package management** is centralised in `Directory.Packages.props`.
Build-wide settings (target framework, nullable, implicit usings, analyzer
level) come from `Directory.Build.props`.

---

## Requirements

- .NET 9 SDK
- SQL Server 2019+ (source vault + tracking DB)
- Optional: Prometheus + Grafana for dashboards, OTLP endpoint for tracing
- Disk headroom for the export target — see the disk budget table in
  [docs/logging.md](docs/logging.md)

---

## License

Copyright © Seamfix. All rights reserved.
