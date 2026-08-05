# Status Reports

> _No Prometheus. No Grafana. No Seq. Just the tracking database you already run._

The tracking DB (`MFilesExportTracking`) records every batch, outcome, error,
progress snapshot, and checkpoint. Everything an operator needs to answer
"what's been processed / what's outstanding / what's failing?" is already
there. This doc covers two ways to look at it.

## 1. `--status` — one-line report from the exporter binary

Runs on the host where the service is installed. Reads the tracking-DB
connection string from `appsettings.json`, opens a read-only connection,
prints a formatted summary, exits.

```powershell
D:\Services\MFilesExporter\MFilesExporter.Console.exe --status
```

Sample output:

```
────────────────────────────────
 Status summary
────────────────────────────────
JobName          Partition   Status    Expected     Processed    Remaining    Failed  Skipped  Docs/sec  MiB/sec  PctComplete  Workers  OpenErrors  Elapsed
---------------  ----------  --------  -----------  -----------  -----------  ------  -------  --------  -------  -----------  -------  ----------  ---------
prod-migration   default     Running    5,041,559    2,791,832    2,249,727     732       42    481.20     9.87        55.38        8          14  02:14:37

────────────────────────────────
 Outcomes
────────────────────────────────
Outcome    Count      PctOfTotal
---------  ---------  ----------
Succeeded  2,791,058       99.97
Failed           732        0.03
Skipped           42        0.00

────────────────────────────────
 Workers
────────────────────────────────
WorkerName   MachineName   Status  Health   HeartbeatAgeSecs  StartedAtUtc
-----------  ------------  ------  -------  ----------------  --------------------
worker-1     exporter-1    Active  Healthy  3                 2026-08-04T08:13:22Z
worker-2     exporter-1    Active  Healthy  4                 2026-08-04T08:13:22Z
worker-3     exporter-1    Active  Healthy  2                 2026-08-04T08:13:22Z

────────────────────────────────
 Failures by category (top 10)
────────────────────────────────
ErrorCategory        ErrorSeverity  Count  LastSeenUtc              Sample
-------------------  -------------  -----  -----------------------  ------------------------------------------------------------
SqlDeadlock          Warning          412  2026-08-04T10:27:11Z     Transaction (Process ID 61) was deadlocked on lock resour…
IoFailure            Error             198 2026-08-04T09:44:03Z     The process cannot access the file because it is being u…

────────────────────────────────
 Checkpoint
────────────────────────────────
PartitionKey  DocumentFilePart  VersionPart  DataFileVersion  SavedAtUtc              SavedByWorker
------------  ----------------  -----------  ---------------  ----------------------  --------------
default       829174            1            492817           2026-08-04T10:27:32Z    worker-3
```

Exit codes:

| Code | Meaning |
|------|---------|
| 0    | Report printed. |
| 1    | Unexpected error (bad connection, view missing). |
| 2    | Tracking-DB connection string not configured in appsettings. |

The command is **read-only** and takes < 1 s against a running exporter — safe
to invoke as often as you like (e.g. from an alerting cron).

## 2. SQL query pack — `deploy/reports/`

Eight `.sql` files runnable from SSMS / Azure Data Studio / sqlcmd. Same
data as `--status`, but you get one query per operational question and can
`SELECT INTO`, chart, or export freely.

| File                              | Answers |
|-----------------------------------|---------|
| `01-status-summary.sql`           | One-row summary — the same row `--status` prints. |
| `02-outcomes-breakdown.sql`       | Succeeded / failed / skipped with % of total. |
| `03-failures-by-category.sql`     | Errors grouped by category + severity (top 20). |
| `04-throughput-hourly.sql`        | Docs/sec + MiB/sec bucketed by hour. |
| `05-recent-errors.sql`            | Latest 100 error rows with full context. |
| `06-worker-health.sql`            | Per-worker heartbeat freshness. |
| `07-active-jobs.sql`              | Every Running job across partitions. |
| `08-checkpoint-current.sql`       | Latest checkpoint per partition (resume position). |

Every query defaults to "the latest running job" — set the `@JobId` variable
at the top if you want to look at a historical run.

## 3. PowerShell wrapper — `Get-ExporterStatus.ps1`

Runs the five most-used queries in sequence via `sqlcmd` and prints them
under captioned headings. Useful when the exporter binary isn't on the
box you're SSH'd into.

```powershell
.\deploy\reports\Get-ExporterStatus.ps1 -Server tracking-db
# scope to one job:
.\deploy\reports\Get-ExporterStatus.ps1 -Server tracking-db -JobId 42
```

## 4. Choosing between them

- **On the app host?** `--status` — no SQL client required.
- **On a DBA workstation?** Open a `.sql` in SSMS. You can chart the
  hourly-throughput result and pin the query.
- **In a scheduled task or a Teams channel?** Wrap `--status` or
  `Get-ExporterStatus.ps1` in a scheduled script and pipe the output.

## 5. What the tracking DB is *not* for

- **Streaming metrics dashboards** — the tracking DB is a system of record,
  not a time-series store. If you want live per-second charts, that's what
  `Exporter:Telemetry:EnablePrometheusEndpoint` is for.
- **Full-text log search** — logs land in `logs/*.log` (rolling, JSON) or
  in Seq if you enabled it. The tracking DB stores errors as structured
  rows, not free-text log lines.
- **Alerting** — no scheduler ships with it. Point your existing scheduler
  (Windows Task Scheduler, Nagios, PowerShell timer) at the same queries.
