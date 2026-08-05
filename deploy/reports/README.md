# Operator Reports

Ad-hoc SQL against `MFilesExportTracking`. Answers the four questions ops
actually asks during a run:

- **What's been processed?**
- **What's outstanding?**
- **What's failing?**
- **Is a worker stuck?**

Every query targets the views already provisioned by `database/40-views.sql`
— no schema changes needed. Runs in SSMS, Azure Data Studio, `sqlcmd`, or
any tool that speaks TDS.

## Files

| File                              | Answers |
|-----------------------------------|---------|
| `01-status-summary.sql`           | Overall job status, processed/remaining, throughput, active workers, open errors |
| `02-outcomes-breakdown.sql`       | Terminal outcomes: succeeded / failed / skipped, with %-of-total |
| `03-failures-by-category.sql`     | Errors grouped by category + severity, top 20 |
| `04-throughput-hourly.sql`        | Docs/sec + MiB/sec bucketed by hour |
| `05-recent-errors.sql`            | Last 100 error rows with full context |
| `06-worker-health.sql`            | Per-worker heartbeat freshness + liveness |
| `07-active-jobs.sql`              | Currently running jobs across all partitions |
| `08-checkpoint-current.sql`       | Latest checkpoint per partition (resume position) |

## Quick invocation

**sqlcmd** (one-shot from PowerShell):

```powershell
sqlcmd -S tracking-db -d MFilesExportTracking -E -i .\01-status-summary.sql
```

**PowerShell wrapper** (formatted status, ready to `Invoke` regularly):

```powershell
.\Get-ExporterStatus.ps1 -Server tracking-db
# add -JobId 42 to scope to one job
```

**From the exporter binary itself** (no SQL client required on the host):

```powershell
D:\Services\MFilesExporter\MFilesExporter.Console.exe --status
```

Reads the same tracking-DB connection string from the deployed
`appsettings.json` and prints the summary. Handy when you can't SSH into
the DB but you can log onto the app host.

## Conventions

- Queries take `@JobId` (default `NULL` = "latest active job") and
  `@Since` (default `NULL` = "since job start"). Set them in the top
  `DECLARE` block if your tool prefers static SQL, or wrap in a stored
  proc if you find yourself running the same shape repeatedly.
- Every result set has stable column order so you can pipe the output
  through `Format-Table` or Excel without keeping a schema map.
- No query mutates state. All are `SELECT`-only, safe to run against
  live production.
