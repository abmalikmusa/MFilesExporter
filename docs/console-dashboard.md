# Real-Time Console Dashboard

> _Project: `MFilesExporter.Reporting.Dashboard`_
> _Renderer: [Spectre.Console](https://spectreconsole.net) 0.49_
> _Configuration section: `Exporter:Dashboard`_

## 1. What it shows

A single-screen, always-current view of the entire export run. All values are
sourced from live state — no polling of the tracking DB from the UI thread,
no derived stats behind an off-screen buffer. One frame every
`RefreshInterval` (default 500 ms).

| Panel               | Displayed metrics                                                   |
|---------------------|---------------------------------------------------------------------|
| **Header**          | App title · elapsed · ETA                                           |
| **Progress**        | Bar (%) · processed / expected · remaining                          |
| **Throughput**      | Docs/sec · MiB/sec · bytes written · ETA                            |
| **Current Activity**| Current batch id · current document (from the busiest worker)       |
| **Counts**          | Succeeded · failed · skipped · retries · processed · remaining · bytes |
| **Resources**       | CPU % · memory · disk free · docs/sec · MiB/sec · workers busy · uptime |
| **Workers**         | Per-worker table: state · current document · batch · bytes · done · fail |
| **Footer**          | Start timestamp · current wall-clock · Ctrl+C hint                  |

## 2. Screenshot / mockup

```
╭──────────────────────────────────────────────────────────────────────────────────────────────────────────╮
│  MFilesExporter  — Enterprise Document Export Dashboard        elapsed 02:14:37         ETA 01:47:22     │
╰──────────────────────────────────────────────────────────────────────────────────────────────────────────╯
╭──────────────────────────── Progress ───────────────────────────╮╭──────── Counts ────────╮
│ ██████████████████████████████████████░░░░░░░░░░░░░░░░  55.4%   ││ succeeded    2,791,058 │
│ 2,791,832 / 5,041,559   (55.4%)                                 ││ failed             732 │
│ remaining  2,249,727                                            ││ skipped             42 │
╰─────────────────────────────────────────────────────────────────╯│ retries          1,204 │
╭─────────────────────────── Throughput ──────────────────────────╮│ processed    2,791,832 │
│  481.2 docs/s                                                   ││ remaining    2,249,727 │
│    9.87 MiB/s                                                   ││ bytes         3.74 TiB │
│ written 3.74 TiB                                                │╰────────────────────────╯
│ ETA     01:47:22                                                │╭─────── Resources ──────╮
╰─────────────────────────────────────────────────────────────────╯│ cpu             62.3 % │
╭─────────────────────── Current Activity ────────────────────────╮│ memory       1.42 GiB  │
│ current batch                                                   ││ disk free   842.11 GiB │
│ batch-42  1,847/2,000                                           ││ docs/sec         481.2 │
│ current document                                                ││ MiB/sec           9.87 │
│ worker-3  DFV#0000829174__2024_Contract_v3.pdf                  ││ workers busy         8 │
╰─────────────────────────────────────────────────────────────────╯│ uptime        02:14:37 │
                                                                    ╰────────────────────────╯
╭──────────────────────────────── Workers (8) ─────────────────────────────────────────────────────────────╮
│   # │ State     │ Current document                                     │ Batch          │      Bytes │  Done │ Fail │
│───────────────────────────────────────────────────────────────────────────────────────────────────────────│
│   0 │ ● busy    │ DFV#0000829171__Consent_Form_Signed.pdf              │ batch-42       │  742.1 KiB │  349k │    3 │
│   1 │ ● busy    │ DFV#0000829172__Invoice_2024_Q3.pdf                  │ batch-42       │    2.3 MiB │  348k │    9 │
│   2 │ ● busy    │ DFV#0000829173__ScannedID_reverse.tif                │ batch-42       │  481.7 KiB │  349k │    2 │
│   3 │ ● busy    │ DFV#0000829174__2024_Contract_v3.pdf                 │ batch-42       │    1.1 MiB │  349k │    0 │
│   4 │ ● busy    │ DFV#0000829175__Memo_Internal_Only.docx              │ batch-42       │   68.4 KiB │  349k │    1 │
│   5 │ ● busy    │ DFV#0000829176__CustomerAgreement_Signed_v2.pdf      │ batch-42       │    2.7 MiB │  348k │    5 │
│   6 │ ○ idle    │ —                                                    │ batch-42       │        0 B │  348k │   14 │
│   7 │ ● busy    │ DFV#0000829178__EmployeeOnboarding_MSA.pdf           │ batch-42       │  912.4 KiB │  349k │    2 │
╰──────────────────────────────────────────────────────────────────────────────────────────────────────────╯
╭──────────────────────────────────────────────────────────────────────────────────────────────────────────╮
│ started 2026-08-04 08:13:22 UTC                                    10:27:59 — press Ctrl+C to stop       │
╰──────────────────────────────────────────────────────────────────────────────────────────────────────────╯
```

Colours in the real render:

- Header title cyan; elapsed white; ETA green.
- Progress bar cyan → yellow → green as it crosses 50 % / 90 %.
- Counts: succeeded green, failed red, skipped yellow, retries magenta.
- Worker state markers: `●` green (busy), `○` yellow (idle), `◌` grey (done).
- Fail column red when > 0, dimmed grey when 0.

## 3. Architecture

```
                                        ┌────────────────────────────────┐
                                        │ IWorkerActivityFeed            │
   pipeline stages ─── RecordStart ────▶│ (ConcurrentDictionary)         │──┐
                       RecordFinish     └────────────────────────────────┘  │
                       RecordIdle                                           │
                                        ┌────────────────────────────────┐  │
                                        │ IExportStateStore              │──┤
                                        │ counters + checkpoint          │  │
                                        └────────────────────────────────┘  │
                                        ┌────────────────────────────────┐  │
                                        │ ITotalExpectedSource (opt.)    │──┤   ┌────────────────────┐
                                        │ IRetryCounterSource  (opt.)    │──┤──▶│ DashboardState-    │
                                        │                                │──┤   │ Source             │──┐
                                        └────────────────────────────────┘  │   └────────────────────┘  │
                                        ┌────────────────────────────────┐  │                            ▼
                                        │ SystemResourceSampler          │──┘             ┌───────────────────────────┐
                                        │  (Process + DriveInfo)         │                │ DashboardRenderer         │
                                        └────────────────────────────────┘                │  (Spectre Layout tree)    │
                                                                                          └───────────────────────────┘
                                                                                                        │
                                                                                                        ▼
                                                                                          ┌───────────────────────────┐
                                                                                          │ Console-                  │
                                                                                          │ DashboardHostedService    │
                                                                                          │  AnsiConsole.Live(...)    │
                                                                                          └───────────────────────────┘
```

The renderer is pure — a `DashboardSnapshot` in, a `Spectre.Console.Layout`
out. Everything time-varying lives in the state source.

## 4. Component reference

| Type / interface                     | Layer                       | Role |
|--------------------------------------|-----------------------------|------|
| `IWorkerActivityFeed`                | `Application.Abstractions`  | Pipeline stages push per-worker updates. |
| `IDashboardStateSource`              | `Application.Abstractions`  | Aggregated pull-based snapshot. |
| `ITotalExpectedSource`               | `Application.Abstractions`  | Optional — authoritative expected count. |
| `IRetryCounterSource`                | `Application.Abstractions`  | Optional — total retries. |
| `WorkerActivityFeed`                 | `Reporting.Dashboard`       | In-memory feed. |
| `SystemResourceSampler`              | `Reporting.Dashboard`       | CPU %, working set memory. |
| `DashboardStateSource`               | `Reporting.Dashboard`       | Aggregator. |
| `DashboardRenderer`                  | `Reporting.Dashboard`       | Pure Spectre layout builder. |
| `ConsoleDashboardHostedService`      | `Reporting.Dashboard`       | Owns the `AnsiConsole.Live` loop. |
| `DashboardOptions`                   | `Configuration.Options`     | Toggle + refresh interval + row cap. |
| `DashboardOptionsValidator`          | `Configuration.Validation`  | Guards `RefreshInterval >= 100 ms` etc. |

## 5. Configuration

```jsonc
"Exporter": {
  "Dashboard": {
    "Enabled": true,
    "RefreshInterval": "00:00:00.500",
    "MaxWorkerRows": 16,
    "MaxDocumentKeyLength": 48,
    "DisableWhenOutputRedirected": true
  }
}
```

| Field | Default | Meaning |
|-------|---------|---------|
| `Enabled`                     | `true`     | Master switch. Off in containers/CI where a TTY is absent. |
| `RefreshInterval`             | `500 ms`   | Frame cadence. Validator enforces ≥ 100 ms. |
| `MaxWorkerRows`               | `16`       | Excess workers collapse into a `+N more` caption. |
| `MaxDocumentKeyLength`        | `48`       | Truncation ceiling on the "Current document" cell. |
| `DisableWhenOutputRedirected` | `true`     | Skip when stdout is piped (structured logs still emit). |

The validator refuses `RefreshInterval < 100 ms` — anything below that
starves the terminal's frame budget and produces flicker on slow SSH links.

## 6. Wiring

`ReportingServiceCollectionExtensions.AddExporterReporting()` registers:

```csharp
services.AddSingleton<IWorkerActivityFeed, WorkerActivityFeed>();
services.AddSingleton<SystemResourceSampler>();
services.AddSingleton<IDashboardStateSource, DashboardStateSource>();
services.AddSingleton<DashboardRenderer>();
services.AddSingleton<IAnsiConsole>(_ => AnsiConsole.Console);
services.AddHostedService<ConsoleDashboardHostedService>();
```

`Program.cs` already calls `AddExporterReporting()` — no additional wiring needed.

## 7. Publishing activity from the pipeline

Every worker calls `RecordStart` when it picks up a document and
`RecordFinish` when it commits an outcome:

```csharp
public sealed class SinkWorker
{
    private readonly IWorkerActivityFeed _feed;
    private readonly int _workerId;
    private readonly string _batchId;

    public async ValueTask ProcessAsync(Document doc, CancellationToken ct)
    {
        _feed.RecordStart(_workerId, doc.Key.ToString(), doc.Size, _batchId);
        try
        {
            await Sink.WriteAsync(doc, ct);
            _feed.RecordFinish(_workerId, WorkerActivityOutcome.Succeeded, doc.Size);
        }
        catch
        {
            _feed.RecordFinish(_workerId, WorkerActivityOutcome.Failed, 0);
            throw;
        }
    }
}
```

The feed is thread-safe — every worker updates its own slot under a
lock; `Snapshot()` returns a copy sorted by worker id.

## 8. Environments where the dashboard is a no-op

- **Containerized runs without a TTY** — `Console.IsOutputRedirected == true`,
  the hosted service exits at startup and only structured logs emit.
- **CI pipelines** — same reasoning. Set `Exporter:Dashboard:Enabled=false`
  in `appsettings.Production.json` if you prefer to be explicit.
- **Log-forwarded runs (`| tee`, `> log.txt`)** — dashboard suppresses,
  logs still write.
- **`docker attach` without `-t`** — dashboard suppresses. Run
  `docker run -it` to see it live.

## 9. Testing

Because the renderer is a pure function of `DashboardSnapshot`, unit tests
build a fixed snapshot and assert on the panels' textual content. Live
rendering is exercised only manually against a real TTY.

```csharp
var snapshot = new DashboardSnapshot { …fixed values… };
var layout   = new DashboardRenderer(new DashboardOptions()).Build(snapshot);
// Snapshot- or golden-file compare via AnsiConsole rendering.
```

## 10. What the dashboard is NOT

- **Not a metrics store.** Everything shown is also emitted to Prometheus /
  OTLP — see `docs/monitoring.md`. The dashboard is for humans at the shell;
  Grafana is for the wall-mounted TV.
- **Not remote.** It renders in the same process that runs the export. Use
  `ssh -t` for interactive supervision or the Grafana dashboard for remote.
- **Not a substitute for logging.** Errors are logged with full context;
  the dashboard's `failed` counter is a summary tile.
