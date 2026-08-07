# Monitoring

> _Project: `MFilesExporter.Infrastructure.Monitoring`_
> _Application surface: `MFilesExporter.Application.Abstractions.Monitoring`_
> _Deployment assets: `deploy/prometheus/`, `deploy/grafana/`_

## 1. What this covers

Every KPI required to run a 5-million-document export in production:

| Signal                | Instrument                              | Source |
|-----------------------|-----------------------------------------|--------|
| Export speed          | `rate(...documents.succeeded)`          | `IExporterMetrics.RecordOutcome` |
| Documents exported    | `...documents.succeeded/failed/skipped` | Same |
| Bytes written         | `...bytes.written`                      | Same |
| Queue depth           | `...queue.depth` (ObservableGauge)      | `IQueueDepthProvider` |
| Queue saturation      | `...queue.capacity_ratio`               | Same |
| Worker utilization    | `...workers.utilization` / `.busy`      | `IWorkerUtilizationProvider` |
| Stalled workers       | `...workers.stalled`                    | Same |
| Memory                | `process_memory_usage_bytes`            | `AddRuntimeInstrumentation` |
| CPU                   | `process_cpu_time_seconds_total`        | Same |
| GC / thread-pool      | `dotnet_*` counters                     | Same |
| Disk free             | `...disk.free_bytes`, `disk.free_ratio` | `ObservableGaugeRegistry` |
| SQL latency           | `...sql.latency` (histogram)            | `IExporterMetrics.RecordSqlLatency` |
| Sink latency          | `...sink.latency` (histogram)           | `IExporterMetrics.RecordSinkLatency` |
| Retry count           | `...retries.total`                      | `MetricsRetryObserver` (retry engine) |
| Circuit-breaker state | logged (INFO/WARN)                      | `OperationCircuitBreaker` |
| Failure ratio         | derived recording rule                  | Prometheus rule |
| ETA                   | `...eta.seconds`                        | `EtaCalculator` + `IProgressSnapshotProvider` |

All instruments are OpenTelemetry-native. Prometheus is one of two exporters
wired by `Exporter:Telemetry:EnablePrometheusEndpoint` — OTLP export is
supported via `Exporter:Telemetry:EnableOtlpExporter`.

## 2. Architecture

```
     Application code                                              OTel SDK
   ┌─────────────────┐                    ┌───────────────────┐   ┌──────────────────┐
   │ IExporterMetrics│──── Meter ────────▶│ ExporterMetrics   │──▶│ MeterProvider    │
   └─────────────────┘                    │  (owns Meter)     │   │  ├── Prometheus  │
                                          └───────────────────┘   │  └── OTLP        │
   ┌─────────────────┐  Observable ┌───────────────────────┐      │                  │
   │ IQueueDepth…    │──── pull ──▶│ ObservableGaugeRegistry│─────▶│                  │
   │ IWorkerUtil…    │             │  (queue, workers,     │      │                  │
   │ IProgressSnap…  │             │   disk, ETA gauges)   │      │                  │
   └─────────────────┘             └───────────────────────┘      │                  │
                                                                  └──────────────────┘
   Retry engine ── MetricsRetryObserver ── Meter "MFilesExporter.Retry" ───┘
   Runtime       ── AddRuntimeInstrumentation ── "System.Runtime" ─────────┘
```

Three OpenTelemetry meters are subscribed:

- `MFilesExporter.Monitoring` — this project.
- `MFilesExporter.Retry` — the retry engine's counters.
- `MFilesExporter.Pipeline` — the legacy pipeline meter (kept for continuity).

Plus `System.Runtime` from `AddRuntimeInstrumentation()` — memory, GC,
thread-pool, CPU.

## 3. Instrument catalogue

Prometheus names shown after the OTel translation
(`.` → `_`, unit suffix appended, counters get `_total`).

### 3.1 Counters

| OTel name                                | Prometheus                                    | Tags |
|------------------------------------------|-----------------------------------------------|------|
| `mfilesexporter.documents.enumerated`    | `mfilesexporter_documents_enumerated_total`   | – |
| `mfilesexporter.documents.succeeded`     | `mfilesexporter_documents_succeeded_total`    | – |
| `mfilesexporter.documents.failed`        | `mfilesexporter_documents_failed_total`       | – |
| `mfilesexporter.documents.skipped`       | `mfilesexporter_documents_skipped_total`      | – |
| `mfilesexporter.bytes.written`           | `mfilesexporter_bytes_written_total`          | – |
| `mfilesexporter.retries.total`           | `mfilesexporter_retries_total`                | `operation`, `category` |
| `mfilesexporter.checkpoints.flushed`     | `mfilesexporter_checkpoints_flushed_total`    | – |

### 3.2 Histograms

| OTel name                             | Prometheus (bucket)                       | Tags |
|---------------------------------------|-------------------------------------------|------|
| `mfilesexporter.document.duration`    | `mfilesexporter_document_duration_bucket` | `outcome` |
| `mfilesexporter.sql.latency`          | `mfilesexporter_sql_latency_bucket`       | `operation`, `status` |
| `mfilesexporter.sink.latency`         | `mfilesexporter_sink_latency_bucket`      | `status` |
| `mfilesexporter.checkpoint.latency`   | `mfilesexporter_checkpoint_latency_bucket`| `records` |

### 3.3 Observable gauges

| OTel name                                | Prometheus                             | Tags |
|------------------------------------------|----------------------------------------|------|
| `mfilesexporter.queue.depth`             | `mfilesexporter_queue_depth`           | `queue` |
| `mfilesexporter.queue.capacity_ratio`    | `mfilesexporter_queue_capacity_ratio`  | `queue` |
| `mfilesexporter.workers.busy`            | `mfilesexporter_workers_busy`          | – |
| `mfilesexporter.workers.utilization`     | `mfilesexporter_workers_utilization`   | – |
| `mfilesexporter.workers.stalled`         | `mfilesexporter_workers_stalled`       | – |
| `mfilesexporter.disk.free_bytes`         | `mfilesexporter_disk_free_bytes`       | `volume` |
| `mfilesexporter.disk.free_ratio`         | `mfilesexporter_disk_free_ratio`       | `volume` |
| `mfilesexporter.eta.seconds`             | `mfilesexporter_eta_seconds`           | – |

## 4. Emitting metrics from application code

```csharp
public sealed class SinkStage
{
    private readonly IExporterMetrics _metrics;
    private readonly IDocumentSink    _sink;

    public async ValueTask WriteAsync(Document doc, CancellationToken ct)
    {
        var sw = ValueStopwatch.StartNew();
        var ok = true;
        try
        {
            await _sink.WriteAsync(doc, ct);
        }
        catch
        {
            ok = false;
            throw;
        }
        finally
        {
            _metrics.RecordSinkLatency(sw.Elapsed, ok);
        }

        _metrics.RecordOutcome(DocumentOutcome.Succeeded, doc.Size, sw.Elapsed);
    }
}
```

SQL layers time their calls with `Stopwatch` and pass the elapsed time
straight to `IExporterMetrics.RecordSqlLatency` with a status tag —
one call, per-operation dimension. See `ContentReaderStage.WorkerLoopAsync`
for the canonical pattern.

## 5. Publishing queue / worker / progress signals

Register the providers as DI singletons. The `ObservableGaugeRegistry`
picks them up automatically:

```csharp
services.AddSingleton<IQueueDepthProvider>(sp =>
    new ChannelQueueDepthProvider<Document>("enumeration", enumerationChannel, capacity: 5000));

services.AddSingleton<IWorkerUtilizationProvider, WorkerUtilizationAdapter>();
services.AddSingleton<IProgressSnapshotProvider,  ProgressSnapshotAdapter>();
```

`WorkerUtilizationAdapter` typically wraps the parallel processing engine's
`WorkerHealthMonitor`; `ProgressSnapshotAdapter` wraps the tracking-DB
progress projection.

## 6. Grafana dashboard

`deploy/grafana/dashboard.json` — an 18-panel dashboard covering:

- **Row 1** — Stat tiles: docs/s, total exported, failed, skipped, ETA.
- **Row 2** — Time series: docs/s stacked by outcome + bytes/s.
- **Row 3** — Queue depth per channel + saturation ratio.
- **Row 4** — Worker utilization / busy vs stalled / retries by category.
- **Row 5** — SQL latency (p50/p95/p99) + sink latency p95.
- **Row 6** — Process memory (working set + GC heap) + CPU + disk free.

Import via Grafana → Dashboards → Import → paste the JSON, pick the
Prometheus data source. Uses a data-source variable `${DS_PROM}` so the
same JSON works across environments.

## 7. Prometheus setup

Scrape config: `deploy/prometheus/scrape-config.yml` — targets the
exporter's `/metrics` on the port from
`Exporter:Telemetry:PrometheusListenerUrl` (default `9464`).

Recording rules and alerts: `deploy/prometheus/recording-rules.yml`.
Highlights:

- `mfilesexporter:export_speed:docs_per_second_5m` — dashboard-friendly rate.
- `mfilesexporter:failure_ratio_5m` — powering the `HighFailureRate` alert.
- `mfilesexporter:sql_latency_ms:{p50,p95,p99}` — one series per operation.
- Alerts: high failure rate, disk < 10 %, stalled workers, SQL p95 > 5 s,
  queue saturation > 90 %.

## 8. Configuration

Set once in `appsettings.json`:

```jsonc
"Exporter": {
  "Telemetry": {
    "ServiceName": "mfiles-exporter",
    "ServiceNamespace": "seamfix",
    "ServiceVersion": "1.0.0",
    "EnablePrometheusEndpoint": true,
    "PrometheusListenerUrl": "http://+:9464/",
    "EnableOtlpExporter": false,
    "OtlpEndpoint": null,
    "TraceSamplingRatio": 0.05
  }
}
```

Enabling OTLP simultaneously with Prometheus is supported — the metric
pipeline fans out to both readers.

## 9. Dependency injection

Registered by `AddExporterInfrastructure(TelemetryOptions)`:

```csharp
services.AddSingleton<ExporterMetrics>();
services.AddSingleton<IExporterMetrics>(sp => sp.GetRequiredService<ExporterMetrics>());
services.AddSingleton<ObservableGaugeRegistry>();
services.AddHostedService<MonitoringActivator>();     // eager instantiation
services.AddExporterOpenTelemetry(telemetry);          // subscribes the meters
```

`MonitoringActivator` ensures the `ObservableGaugeRegistry` is constructed
at host start — otherwise DI defers it and the first scrape misses the
observable gauges.

## 10. Testing

| Test file                        | Coverage |
|----------------------------------|----------|
| `ExporterMetricsTests`           | Outcome counters, retry tags, SQL histogram capture using `MeterListener`. |
| `EtaCalculatorTests`             | Null/empty/complete/high-rate paths for the ETA math. |

Integration testing the OpenTelemetry pipeline requires a live meter
reader — recommended path is to expose `/metrics` from a smoke-test host
and assert Prometheus-shape output with an HTTP client.

## 11. Operational notes

- **Scrape cadence**: 15 s. Histograms use OTel default buckets (0.005 s → 10 s);
  adjust `Views` in the meter provider if BLOB latency exceeds 10 s often.
- **Cardinality budget**: `operation` on `sql.latency` is bounded to a
  small enumerated set (`sql.enumerate`, `sql.blob-read`, `sql.claim-work`,
  `sql.record-outcome`). Do **not** stuff document IDs into tags.
- **Restart resets counters**: Prometheus handles counter resets — no
  action needed. The **ETA gauge** legitimately drops to null on restart
  until the progress provider recomputes.
- **Disk gauges** rely on `DriveInfo` — verify the `Exporter:Storage:RootPath`
  is on the volume you expect to monitor. Network-mounted paths may not
  report free space reliably; add a filesystem-level exporter (node_exporter)
  as a belt-and-braces measure.
