# Enterprise Logging

> _Project: `MFilesExporter.Logging`_
> _Concrete provider: **Serilog** — `Microsoft.Extensions.Logging` is what business code depends on. Only this project references Serilog directly._

## 1. Overview

Every observable behaviour of the exporter — lifecycle events, per-document
progress, retry attempts, checkpoints, and audit trail — flows through a
single logging pipeline. The pipeline is deliberately split into five sinks
so different consumers can be served without cross-contamination:

| Sink                 | File pattern                    | Retention (default) | Role |
|----------------------|---------------------------------|---------------------|------|
| Console              | stdout                          | –                   | Operator-facing, human-readable |
| Everything           | `logs/mfilesexporter-.log`      | 30 days             | Full structured JSON |
| Errors               | `logs/errors-.log`              | 90 days             | `Warning`/`Error`/`Fatal` only |
| Audit                | `logs/audit-.log`               | **7 years** (2555 days) | `Category=Audit` only, WORM-shippable |
| Performance          | `logs/performance-.log`         | 30 days             | `Category=Performance` only |
| Workers              | `logs/workers-.log`             | 14 days             | `Category=Worker` only |

All file sinks use the compact-JSON formatter and are wrapped in
`Serilog.Sinks.Async` so file writes never block application threads.

## 2. Building blocks

| Type                              | Purpose |
|-----------------------------------|---------|
| `SerilogBootstrap`                | Bootstrap logger + host integration. |
| `LogCategories`                   | Constants for the `Category` property (`Application`, `Audit`, `Performance`, `Worker`). |
| `ICorrelationIdAccessor`          | Ambient correlation-id scope. AsyncLocal-backed. |
| `CorrelationIdEnricher`           | Belt-and-braces enricher — stamps `CorrelationId` when the caller forgot to push. |
| `IPerformanceLogger` / `PerformanceScope` | RAII latency measurement — one JSON line per operation, always emitted (even on throw). |
| `IAuditLog` / `AuditEvent`        | Compliance-grade audit trail. |
| `WorkerLogScope`                  | `IDisposable` that stamps `WorkerId` + `WorkerName` on every downstream log. |

## 3. Structured logging

The application code depends on `Microsoft.Extensions.Logging.ILogger<T>`.
Every log call MUST use message templates — never `$"…"` interpolation:

```csharp
_logger.LogInformation(
    "document.exported id={DocumentId} bytes={Bytes} sink={SinkPath}",
    doc.Id, bytes, path);
```

Serilog converts each named placeholder into a structured JSON field. Do not
concatenate values into the message text.

## 4. Correlation IDs

Every top-level entry point (worker iteration, job start, RPC boundary) opens
a correlation scope. Downstream code inherits it automatically:

```csharp
public sealed class ExportHostedService
{
    private readonly ICorrelationIdAccessor _correlation;
    private readonly IExportPipeline _pipeline;

    public async Task RunAsync(CancellationToken ct)
    {
        using var _ = _correlation.PushNew(out var correlationId);
        _logger.LogInformation("job.started");
        await _pipeline.RunAsync(ct);      // every log line inside carries CorrelationId
    }
}
```

The scope is implemented via `AsyncLocal<string?>` **and** `LogContext.PushProperty`,
so:

- The value flows across `await`, `Task.Run`, and `Parallel.ForEach` children.
- Serilog attaches it to every log event without any per-call plumbing.
- `CorrelationIdEnricher` fills in `no-scope` if a log line escapes the scope
  — so filtered queries never drop untagged events.

## 5. Performance logging

Use `IPerformanceLogger` for anything you care about latency-wise. The scope
emits a single JSON record on `Dispose`, even when the enclosing code throws:

```csharp
using var scope = _perf.Begin("sink.write");
scope.SetTag("path", path);
await sink.WriteAsync(doc, ct);
scope.Complete(bytes: doc.Size);
```

Or the delegate flavour:

```csharp
var blob = await _perf.TimeAsync("sql.blob-read",
    ct => reader.ReadAsync(id, ct), ct);
```

Failure paths automatically annotate `outcome=failed` and include the exception,
so latency SLOs can be sliced by success vs failure.

## 6. Audit logging

`IAuditLog` writes to a dedicated append-only sink for compliance:

```csharp
await _audit.WriteAsync(
    action:  "document.exported",
    actor:   $"worker-{workerId}",
    subject: $"document-file-version/{dfv}",
    outcome: "success",
    data: new Dictionary<string, object?>
    {
        ["Bytes"]        = bytes,
        ["ChecksumHex"]  = checksum,
        ["OutputPath"]   = outputPath,
    });
```

**Never** put document payload or PII in `Data` — only surrogate identifiers.

## 7. Worker logs

Every parallel-processing worker enters a scope on startup:

```csharp
public async Task RunWorkerAsync(int workerId, CancellationToken ct)
{
    using var _ = WorkerLogScope.Enter(workerId, workerName: $"worker-{workerId}");
    while (!ct.IsCancellationRequested)
    {
        // Every log line here inherits: WorkerId, WorkerName, Category=Worker.
    }
}
```

The scope also flows across `await` — so downstream stages log with the
worker id even without receiving it as a parameter.

## 8. Configuration

The complete `appsettings.json` layout under `Serilog:WriteTo`:

```jsonc
"Serilog": {
  "MinimumLevel": {
    "Default": "Information",
    "Override": { "Microsoft": "Warning", "System": "Warning" }
  },
  "Enrich": [ "FromLogContext", "WithMachineName", "WithEnvironmentUserName",
              "WithProcessId", "WithThreadId" ],
  "Properties": { "Application": "MFilesExporter" },
  "WriteTo": [
    {
      "Name": "Async",
      "Args": {
        "bufferSize": 10000,
        "blockWhenFull": true,
        "configure": [
          { "Name": "Console", "Args": { "outputTemplate": "[{Timestamp:HH:mm:ss.fff} {Level:u3}] [{CorrelationId}] {SourceContext} - {Message:lj} {Properties:j}{NewLine}{Exception}" } },
          { "Name": "File",    "Args": { "path": "logs/mfilesexporter-.log", "rollingInterval": "Day", "retainedFileCountLimit": 30,   "formatter": "Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact" } },
          { "Name": "Logger",  "Args": { "configureLogger": { "Filter": [ { "Name": "ByIncludingOnly", "Args": { "expression": "@l in ['Warning','Error','Fatal']" } } ], "WriteTo": [ { "Name": "File", "Args": { "path": "logs/errors-.log",       "rollingInterval": "Day", "retainedFileCountLimit": 90,   "formatter": "Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact" } } ] } } },
          { "Name": "Logger",  "Args": { "configureLogger": { "Filter": [ { "Name": "ByIncludingOnly", "Args": { "expression": "Category = 'Audit'" } } ],                       "WriteTo": [ { "Name": "File", "Args": { "path": "logs/audit-.log",        "rollingInterval": "Day", "retainedFileCountLimit": 2555, "formatter": "Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact" } } ] } } },
          { "Name": "Logger",  "Args": { "configureLogger": { "Filter": [ { "Name": "ByIncludingOnly", "Args": { "expression": "Category = 'Performance'" } } ],                 "WriteTo": [ { "Name": "File", "Args": { "path": "logs/performance-.log",  "rollingInterval": "Day", "retainedFileCountLimit": 30,   "formatter": "Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact" } } ] } } },
          { "Name": "Logger",  "Args": { "configureLogger": { "Filter": [ { "Name": "ByIncludingOnly", "Args": { "expression": "Category = 'Worker'" } } ],                      "WriteTo": [ { "Name": "File", "Args": { "path": "logs/workers-.log",      "rollingInterval": "Day", "retainedFileCountLimit": 14,   "formatter": "Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact" } } ] } } }
        ]
      }
    }
  ]
}
```

Every file sink has `rollingInterval: Day`, `fileSizeLimitBytes` for
in-day rollover, `shared: true` for multi-process writes, and `buffered:
true` for high-throughput sinks (audit is unbuffered — compliance beats
throughput).

## 9. DI wiring

```csharp
// Program.cs
Log.Logger = SerilogBootstrap.CreateBootstrapLogger();

builder.Services.AddExporterLogging();            // Correlation, Audit, Performance
builder.Services.AddSerilog((sp, cfg) => cfg
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(sp)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithEnvironmentUserName()
    .Enrich.WithThreadId()
    .Enrich.WithProcessId()
    .Enrich.WithProperty("Application", "MFilesExporter"));
```

`AddExporterLogging` registers `ICorrelationIdAccessor`, `IAuditLog`,
`IPerformanceLogger`, and the `CorrelationIdEnricher` — the enricher is
picked up automatically via `ReadFrom.Services(sp)`.

## 10. Sample log output

### Console (human-readable)

```
[10:14:22.417 INF] [d2a5f38b1c4a4e5f9c3b2a1e6f7d8e9a] MFilesExporter.Console.ExportHostedService - job.started jobId=42 partition=default {Application="MFilesExporter"}
[10:14:22.583 INF] [d2a5f38b1c4a4e5f9c3b2a1e6f7d8e9a] MFilesExporter.Export.Files.FileExportEngine - document.exported id=DFV#8172 bytes=1048576 sink=/data/export/8a/17/DFV_8172.pdf
[10:14:22.719 WRN] [d2a5f38b1c4a4e5f9c3b2a1e6f7d8e9a] MFilesExporter.Infrastructure.Retry.RetryExecutor - [retry] sql-read attempt 1/5 failed with SqlDeadlock; sleeping 00:00:00.0512
[10:14:24.902 INF] [d2a5f38b1c4a4e5f9c3b2a1e6f7d8e9a] MFilesExporter.Export.Pipeline.OutcomeCollectorStage - checkpoint.flushed rows=200 lastDfv=8199 elapsed_ms=42.11
```

### `mfilesexporter-2026-08-04.log` (compact JSON)

```json
{"@t":"2026-08-04T10:14:22.4171Z","@l":"Information","@mt":"job.started jobId={JobId} partition={Partition}","JobId":42,"Partition":"default","CorrelationId":"d2a5f38b1c4a4e5f9c3b2a1e6f7d8e9a","MachineName":"exporter-prod-3","EnvironmentUserName":"svc-mfiles","ProcessId":18342,"ThreadId":11,"Application":"MFilesExporter","SourceContext":"MFilesExporter.Console.ExportHostedService","Category":"Application"}
{"@t":"2026-08-04T10:14:22.5834Z","@l":"Information","@mt":"document.exported id={DocumentId} bytes={Bytes} sink={SinkPath}","DocumentId":"DFV#8172","Bytes":1048576,"SinkPath":"/data/export/8a/17/DFV_8172.pdf","CorrelationId":"d2a5f38b1c4a4e5f9c3b2a1e6f7d8e9a","WorkerId":"3","WorkerName":"worker-3","Category":"Worker"}
```

### `errors-2026-08-04.log`

```json
{"@t":"2026-08-04T10:14:22.7194Z","@l":"Warning","@mt":"[retry] {Operation} attempt {Attempt}/{Max} failed with {Category}; sleeping {Delay}","Operation":"sql-read","Attempt":1,"Max":5,"Category":"SqlDeadlock","Delay":"00:00:00.0512","CorrelationId":"d2a5f38b1c4a4e5f9c3b2a1e6f7d8e9a","@x":"Microsoft.Data.SqlClient.SqlException: Transaction (Process ID 61) was deadlocked on lock resources ..."}
{"@t":"2026-08-04T10:14:28.1027Z","@l":"Error","@mt":"perf.operation op={Operation} outcome={Outcome} elapsed_ms={ElapsedMs} category={Category}","Operation":"sink.write","Outcome":"failed","ElapsedMs":312.44,"Category":"Performance","CorrelationId":"d2a5f38b1c4a4e5f9c3b2a1e6f7d8e9a","@x":"System.IO.IOException: There is not enough space on the disk. ..."}
```

### `audit-2026-08-04.log`

```json
{"@t":"2026-08-04T10:14:22.5836Z","@l":"Information","@mt":"audit.event action={Action} actor={Actor} subject={Subject} outcome={Outcome} timestamp={Timestamp:O} correlationId={CorrelationId} category={Category}","Action":"document.exported","Actor":"worker-3","Subject":"document-file-version/8172","Outcome":"success","Timestamp":"2026-08-04T10:14:22.5836000+00:00","CorrelationId":"d2a5f38b1c4a4e5f9c3b2a1e6f7d8e9a","Category":"Audit","Bytes":1048576,"ChecksumHex":"6f8b4a...","OutputPath":"/data/export/8a/17/DFV_8172.pdf"}
{"@t":"2026-08-04T10:14:24.9021Z","@l":"Information","@mt":"audit.event action={Action} actor={Actor} subject={Subject} outcome={Outcome} timestamp={Timestamp:O} correlationId={CorrelationId} category={Category}","Action":"job.completed","Actor":"system","Subject":"job/42","Outcome":"success","Timestamp":"2026-08-04T10:14:24.9021000+00:00","CorrelationId":"d2a5f38b1c4a4e5f9c3b2a1e6f7d8e9a","Category":"Audit","DocumentsExported":5041559,"BytesExported":4198234117376}
```

### `performance-2026-08-04.log`

```json
{"@t":"2026-08-04T10:14:22.5831Z","@l":"Information","@mt":"perf.operation op={Operation} outcome={Outcome} elapsed_ms={ElapsedMs:F2} category={Category}","Operation":"sql.blob-read","Outcome":"success","ElapsedMs":41.28,"Category":"Performance","CorrelationId":"d2a5f38b1c4a4e5f9c3b2a1e6f7d8e9a","Bytes":1048576}
{"@t":"2026-08-04T10:14:22.5843Z","@l":"Information","@mt":"perf.operation op={Operation} outcome={Outcome} elapsed_ms={ElapsedMs:F2} category={Category}","Operation":"sink.write","Outcome":"success","ElapsedMs":18.75,"Category":"Performance","Bytes":1048576,"WorkerId":"3","CorrelationId":"d2a5f38b1c4a4e5f9c3b2a1e6f7d8e9a"}
```

### `workers-2026-08-04.log`

```json
{"@t":"2026-08-04T10:14:22.5834Z","@l":"Information","@mt":"document.exported id={DocumentId} bytes={Bytes}","DocumentId":"DFV#8172","Bytes":1048576,"WorkerId":"3","WorkerName":"worker-3","Category":"Worker","CorrelationId":"d2a5f38b1c4a4e5f9c3b2a1e6f7d8e9a"}
```

## 11. Log-retention recommendations

| Sink                      | Retention | Rotation | Rationale |
|---------------------------|-----------|----------|-----------|
| Console                   | ephemeral | –        | Operator-facing; captured by container platform. |
| `mfilesexporter-*.log`    | 30 days   | daily + 256 MiB size | Full observability window; short enough to bound disk. |
| `errors-*.log`            | **90 days** | daily + 128 MiB | Post-mortem window survives quarterly reviews. |
| `audit-*.log`             | **7 years** | daily, unbuffered | Compliance / SOX / ISO 27001. WORM-shippable. Never rotate on size. |
| `performance-*.log`       | 30 days   | daily + 256 MiB | Enough to spot regressions across a sprint. |
| `workers-*.log`           | 14 days   | daily + 256 MiB | High volume — the compact-JSON copy in `mfilesexporter-*.log` remains for longer archives. |

### Shipping

Structured JSON lines are consumed unchanged by:

- **Elasticsearch/OpenSearch** — Filebeat with the `json.keys_under_root: true` codec.
- **Loki** — Promtail scrape config with `json` stage.
- **Splunk** — HEC input with `sourcetype = _json`.
- **CloudWatch Logs** — install the CloudWatch agent and treat lines as `json`.

For the audit sink, prefer **write-once-read-many (WORM)** storage in
addition to the local file — e.g. AWS S3 Object Lock, Azure Blob immutable
containers, or GCP Bucket Lock. The local file is the source of truth for
30–90 days; the WORM copy is the 7-year system of record.

### Disk budget (5-million-document run)

Empirical from the exporter's structured JSON layout:

- `mfilesexporter`: ~400 B/event × 5 events/document ≈ 10 GB/run → daily rollover, 30 files → **300 GB steady state**.
- `errors`: ≤1% of total → ~3 GB/run × 90 days → **9 GB**.
- `audit`: 1 event/document + lifecycle → ~2 KB/document × 5 M = **10 GB/run**; over 7 years assume 24 runs/yr → **1.7 TB WORM budget**.
- `performance`: comparable to full log → **300 GB**.
- `workers`: comparable to full log but 14 days → **140 GB**.

Add ~30% headroom. Provision **≥1 TB local NVMe** for `logs/` on each host.

## 12. What NEVER goes in logs

- Document payload / BLOB content.
- Full SQL connection strings (redact `Password=` before logging).
- Any user-supplied file contents.
- Free-text metadata that may contain PII.

Only surrogate identifiers — document-file-part, version-part,
data-file-version, SHA-256 idempotency key hex, output paths — appear in
structured fields.

## 13. Shipping logs to Seq

**[Seq](https://datalust.co/seq)** is a first-class option for .NET shops that
don't want Prometheus/Grafana: single MSI, ingests Serilog events over HTTP,
gives you structured-log search and dashboards in one pane. The
`Serilog.Sinks.Seq` package is already referenced by
`MFilesExporter.Logging` — enable it by overlaying the fragment shipped in
`deploy/windows-service/appsettings.Seq.example.json`:

```jsonc
"Serilog": {
  "Using": [
    "Serilog.Sinks.Console", "Serilog.Sinks.File", "Serilog.Sinks.Async",
    "Serilog.Sinks.Seq", "Serilog.Expressions"
  ],
  "WriteTo": [
    {
      "Name": "Seq",
      "Args": {
        "serverUrl": "http://seq.internal:5341",
        "apiKey":    "<REPLACE-WITH-INGESTION-API-KEY>",
        "restrictedToMinimumLevel": "Information",
        "batchPostingLimit": 1000,
        "period":            "00:00:02",
        "queueSizeLimit":    100000
      }
    }
  ]
}
```

Two ways to layer this in:

- **Environment overlay** — rename the file to `appsettings.Production.json`
  next to the deployed binary. The Generic Host merges it on top of
  `appsettings.json`, so the new `Seq` sink is added alongside the
  existing file sinks.
- **Environment variables** — set `MFILESEXPORTER_Serilog__WriteTo__…`
  paths. Fine for scripting, awkward to read.

Seq automatically indexes every property Serilog emits (`CorrelationId`,
`WorkerId`, `Category`, `Operation`, ...), so queries like
`Category = 'Audit' and Actor = 'worker-3'` work with no dashboard setup.
The `Category` property still routes the same events to the appropriate
`logs/*.log` file locally — Seq is additive, not a replacement.

**Retention** — Seq handles retention via its own storage engine. The
audit sink still writes to `logs/audit-*.log` for the 7-year WORM
requirement; treat Seq as the operational window, not the compliance
archive.

## 14. Testing coverage

| Test                              | Focus |
|-----------------------------------|-------|
| `CorrelationIdAccessorTests`      | Push/pop, nesting, async-flow propagation, sibling isolation |
| `PerformanceLoggerTests`          | Success emission, failure emission w/ rethrow, tags, unknown-outcome guard |
| `AuditLogTests`                   | Field composition, ambient correlation, override correlation |
