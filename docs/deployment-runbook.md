# End-to-End Deployment Runbook

> _Zero to a running export with monitoring reports — one linear guide._
> _Target: Windows Server 2019/2022, SQL Server 2019+, on-prem._

Follow the sections in order. Every command is copy-pasteable; anything you
must substitute is in `<ANGLE-BRACKETS>`. Cross-references point to the
deeper design docs when you want to understand *why* a step exists.

---

## Table of contents

1. [Prerequisites](#1-prerequisites)
2. [Provision the tracking database](#2-provision-the-tracking-database)
3. [Prepare storage volumes](#3-prepare-storage-volumes)
4. [Create the service account](#4-create-the-service-account)
5. [Publish the exporter binary](#5-publish-the-exporter-binary)
6. [Configure `appsettings.json`](#6-configure-appsettingsjson)
7. [Install the Windows Service](#7-install-the-windows-service)
8. [Preflight — start and verify](#8-preflight--start-and-verify)
9. [Run monitoring reports](#9-run-monitoring-reports)
10. [Common ops tasks](#10-common-ops-tasks)
11. [Upgrading in place](#11-upgrading-in-place)
12. [Uninstall](#12-uninstall)
13. [Troubleshooting matrix](#13-troubleshooting-matrix)

---

## 1. Prerequisites

**On the app host** (the Windows Server running the exporter):

| Requirement                                    | Why                                                          |
|------------------------------------------------|--------------------------------------------------------------|
| Windows Server 2019 or 2022                    | Supported .NET 9 runtime + Service Control Manager           |
| **PowerShell 5.1+** (built in) or PowerShell 7 | Install / uninstall scripts                                  |
| **.NET 9 Hosting Bundle** (only if not self-contained publish) | Runtime + ANCM. Skip if you follow §5 with self-contained publish. |
| **SQL Server client tooling** — `sqlcmd` or SSMS | For provisioning + reports                                   |
| Local admin account (for install)              | Registers the Windows Service, sets URL ACLs                 |
| Outbound network to source vault + tracking DB | Duh                                                          |
| ~1 TB free on the output volume                | 5 M documents × ~1 MiB avg blob                              |

**On the SQL Server** (source vault + tracking DB — can be the same box or two):

- SQL Server 2019+ (2022 preferred for TEMPDB improvements).
- Read access to the M-Files vault DB (`DOCUMENTFILEVERSION`, `DATAFILEVERSION`, `DATAFILEVERSION_BYTES`).
- A dedicated **tracking database** — you'll create it in §2. It must be **separate** from the vault; the exporter role has no write permission on the vault.

---

## 2. Provision the tracking database

Run the DDL scripts under `database/` in order. Everything is idempotent — safe to re-run.

```powershell
# Substitute <TRACKING-DB-HOST>. Uses Integrated Security; add -U / -P for SQL auth.
$server = "<TRACKING-DB-HOST>"

# Step 1: create the database itself. Runs against master.
sqlcmd -S $server -d master -i database/00-database.sql

# Step 2: all other objects — tables, TVPs, indexes, procs, views, security,
# maintenance, work-claiming. In order.
foreach ($f in Get-ChildItem database/*.sql | Where-Object Name -notlike '00-*' | Sort-Object Name)
{
    Write-Host "Applying $($f.Name) …"
    sqlcmd -S $server -d MFilesExportTracking -i $f.FullName
    if ($LASTEXITCODE -ne 0) { throw "Failed on $($f.Name)" }
}
```

Verify:

```sql
-- Should return 8+ tables and 7 views.
SELECT COUNT(*) AS Tables FROM sys.tables WHERE schema_id = SCHEMA_ID('dbo');
SELECT COUNT(*) AS Views  FROM sys.views  WHERE schema_id = SCHEMA_ID('dbo');
```

Full schema reference: [docs/database.md](database.md).

---

## 3. Prepare storage volumes

The exporter writes a lot. Pre-create the target directories on the volume(s)
you've sized for it:

```powershell
$root = "E:\ExportOutput"

New-Item -ItemType Directory -Path "$root\documents"    -Force | Out-Null
New-Item -ItemType Directory -Path "$root\metadata"     -Force | Out-Null
New-Item -ItemType Directory -Path "$root\manifests"    -Force | Out-Null
New-Item -ItemType Directory -Path "$root\checkpoints"  -Force | Out-Null
New-Item -ItemType Directory -Path "$root\logs"         -Force | Out-Null
```

The service account (§4) will need **Modify** on all of them. We'll grant that after creating the account.

---

## 4. Create the service account

Pick one:

### 4.a — Local system account (simplest, no domain shares)

`NT AUTHORITY\NetworkService` — built-in, no password to manage. Fine when
the SQL Server accepts NetworkService (via `<hostname>$` in Windows Auth)
or when you use SQL Auth in the connection strings.

**Nothing to create**; jump to §5.

### 4.b — Domain service account (recommended for production)

If either the source vault or the storage volume needs a domain identity
(SMB share, Kerberos to SQL Server), create a dedicated account:

```powershell
# On a domain-joined workstation or a Domain Controller with RSAT:
New-ADUser `
    -Name                  "svc-mfiles" `
    -SamAccountName        "svc-mfiles" `
    -UserPrincipalName     "svc-mfiles@<DOMAIN>" `
    -AccountPassword       (Read-Host -AsSecureString "Password") `
    -PasswordNeverExpires  $true `
    -CannotChangePassword  $true `
    -Enabled               $true `
    -Description           "MFilesExporter Windows Service"
```

Grant permissions:

```sql
-- On the M-Files vault DB (read-only):
USE MFilesVault;
CREATE LOGIN [<DOMAIN>\svc-mfiles] FROM WINDOWS;
CREATE USER  [<DOMAIN>\svc-mfiles] FOR LOGIN [<DOMAIN>\svc-mfiles];
ALTER ROLE   db_datareader ADD MEMBER [<DOMAIN>\svc-mfiles];

-- On the tracking DB (read/write):
USE MFilesExportTracking;
CREATE LOGIN [<DOMAIN>\svc-mfiles] FROM WINDOWS;
CREATE USER  [<DOMAIN>\svc-mfiles] FOR LOGIN [<DOMAIN>\svc-mfiles];
ALTER ROLE   db_datareader ADD MEMBER [<DOMAIN>\svc-mfiles];
ALTER ROLE   db_datawriter ADD MEMBER [<DOMAIN>\svc-mfiles];
GRANT EXECUTE ON SCHEMA::dbo TO [<DOMAIN>\svc-mfiles];
```

On the **app host**, grant the account:

- **Modify** on `E:\ExportOutput\*` (Explorer → Security → Edit → Add).
- **Log on as a service** local right — `secpol.msc` → Local Policies → User
  Rights Assignment → *Log on as a service* → add `<DOMAIN>\svc-mfiles`.

---

## 5. Publish the exporter binary

Publish from a build machine (or the app host itself if the SDK is
installed). This produces a self-contained single-file executable that
carries the .NET runtime inside it — no runtime install required on
the target.

```powershell
# From the repo root, in an elevated PowerShell:
.\deploy\windows-service\publish.ps1 -InstallPath "D:\Services\MFilesExporter"
```

Under the hood this runs:

```
dotnet publish src\MFilesExporter.Console\MFilesExporter.Console.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o D:\Services\MFilesExporter
```

You should see:

```
D:\Services\MFilesExporter\
    MFilesExporter.Console.exe        ~90 MiB — single-file bundle
    appsettings.json                  default committed config
    appsettings.Production.json       (optional — copy from step 6)
```

Full deployment options in [docs/deployment-windows-service.md § 3](deployment-windows-service.md#3-publish).

---

## 6. Configure `appsettings.json`

Edit `D:\Services\MFilesExporter\appsettings.json`. Use **absolute paths**.

```jsonc
"Exporter": {
  "Source": {
    "ConnectionString": "Server=<VAULT-DB>;Database=MFilesVault;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Application Name=MFilesExporter",
    "PartitionKey": "default"
  },
  "TrackingDatabase": {
    "ConnectionString": "Server=<TRACKING-DB>;Database=MFilesExportTracking;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;"
  },
  "StateStore": {
    "Provider": "sqlite",
    "ConnectionString": "D:\\Services\\MFilesExporter\\state.db"
  },
  "Storage": {
    "RootPath": "E:\\ExportOutput\\documents",
    "ManifestPath": "E:\\ExportOutput\\manifests",
    "MinimumFreeSpaceGb": 200
  },
  "FileExport": {
    "RootPath": "E:\\ExportOutput\\documents",
    "FolderStrategy": "ShardedByDate",
    "ShardDepth": 2,
    "FsyncOnWrite": true
  },
  "Metadata": {
    "OutputDirectory": "E:\\ExportOutput\\metadata"
  },
  "Checkpoint": {
    "WalDirectory": "E:\\ExportOutput\\checkpoints",
    "FsyncOnWrite": true
  },
  "Pipeline": {
    "ContentReaderConcurrency": 16,
    "SinkConcurrency": 16
  },
  "ParallelProcessing": {
    "WorkerCount": 16,
    "ChannelCapacity": 256
  },
  "Dashboard": {
    "Enabled": false     // no TTY under a Windows Service
  },
  "Telemetry": {
    "EnablePrometheusEndpoint": false,   // set true if you'll scrape /metrics
    "EnableOtlpExporter":       false
  }
}
```

**Secrets** (connection strings, ingestion tokens) belong in environment
variables set on the service account, not in this file. Prefix them
`MFILESEXPORTER_` and use `__` as section separator:

```
MFILESEXPORTER_Exporter__Source__ConnectionString=…
MFILESEXPORTER_Exporter__TrackingDatabase__ConnectionString=…
```

Full field reference: [docs/configuration.md](configuration.md).

**Tune `WorkerCount` and `Concurrency`** to your box: rule of thumb is
`min(physical cores, 16)` for the parallel-processing engine and the same
for `ContentReaderConcurrency` / `SinkConcurrency`. Higher numbers help
only when the source DB and disk are keeping up.

---

## 7. Install the Windows Service

```powershell
# Elevated PowerShell, from the repo root.

# Local NetworkService (no domain shares):
.\deploy\windows-service\install.ps1 `
    -InstallPath "D:\Services\MFilesExporter"

# Domain account (recommended):
.\deploy\windows-service\install.ps1 `
    -InstallPath   "D:\Services\MFilesExporter" `
    -ServiceAccount "<DOMAIN>\svc-mfiles"
```

The installer:

- Registers `MFilesExporter` with `StartupType = AutomaticDelayedStart`
  (delayed so SQL Server has time to come up after a reboot).
- Sets failure-recovery: **restart 3× with 60 s delay** before the SCM
  gives up.
- Reserves the Prometheus URL (`http://+:9464/`) for the service account
  via `netsh http add urlacl` — harmless if you left Prometheus off in §6.

Details: [docs/deployment-windows-service.md § 5](deployment-windows-service.md#5-install).

---

## 8. Preflight — start and verify

```powershell
Start-Service MFilesExporter
Start-Sleep -Seconds 5
Get-Service   MFilesExporter          # → Status: Running
```

Watch the log for a clean start-up:

```powershell
Get-Content D:\Services\MFilesExporter\logs\mfilesexporter-*.log -Tail 30 -Wait
```

Expected lines within the first ~10 s:

```
"@l":"Information","@mt":"MFilesExporter starting in Production (mode=WindowsService)"
"@l":"Information","@mt":"Starting parallel processing engine hosted service for Document"
"@l":"Information","@mt":"Batch coordinator ready | partition={Partition}"
"@l":"Information","@mt":"Job started jobId=…"
```

If the process exits within a few seconds and the log shows a
`FluentValidation` failure, your `appsettings.json` has an invalid field.
Fix and restart.

Quick sanity check from the tracking DB:

```powershell
D:\Services\MFilesExporter\MFilesExporter.Console.exe --status
```

You should see one row under **Status summary** with `Status = Running` and
non-zero `Processed` after the first minute.

---

## 9. Run monitoring reports

Three ways to see what's happening. All three read the same tracking DB —
they never touch the running process, so use them freely.

### 9.a — Live status (fastest)

On the app host:

```powershell
D:\Services\MFilesExporter\MFilesExporter.Console.exe --status
```

One-page summary: processed / remaining, docs/sec, MiB/sec, %-complete,
worker health, top failure categories, current checkpoint. Sub-second.
Exit code 0 on success, 2 if the tracking-DB connection string is
missing.

Docs: [docs/status-reports.md § 1](status-reports.md#1---status--one-line-report-from-the-exporter-binary).

### 9.b — SQL query pack (deepest)

Open any of the eight `.sql` files under `deploy/reports/` in SSMS or
Azure Data Studio. Every query defaults to the latest Running job; set
`@JobId` at the top to look at a historical run.

Most-used:

```powershell
sqlcmd -S <TRACKING-DB> -d MFilesExportTracking -E -i deploy\reports\01-status-summary.sql
sqlcmd -S <TRACKING-DB> -d MFilesExportTracking -E -i deploy\reports\03-failures-by-category.sql
sqlcmd -S <TRACKING-DB> -d MFilesExportTracking -E -i deploy\reports\04-throughput-hourly.sql
```

Full catalogue: [docs/status-reports.md § 2](status-reports.md#2-sql-query-pack--deployreports).

### 9.c — PowerShell wrapper (five reports at once)

For a full snapshot in one command:

```powershell
.\deploy\reports\Get-ExporterStatus.ps1 -Server <TRACKING-DB>
```

Prints status summary → outcomes → workers → failures by category →
current checkpoint, each with a caption. Use `-JobId 42` to scope to a
historical run.

### 9.d — Alerting on failure spikes

Wrap `--status` in a Windows Scheduled Task and send its output to Teams
or email when the failure count grows. Simple example:

```powershell
$out = & 'D:\Services\MFilesExporter\MFilesExporter.Console.exe' --status
$failed = ($out | Select-String -Pattern '^\s*prod-migration' | ForEach-Object {
    ($_ -split '\s{2,}')[6]   # Failed column index
}) -as [int]

if ($failed -gt 100) {
    # Invoke your Teams webhook / Send-MailMessage etc.
    Invoke-RestMethod -Uri $env:TEAMS_WEBHOOK -Method Post -Body @{
        text = "MFilesExporter: $failed failures — investigate"
    } -ContentType "application/json"
}
```

Schedule it every 5–10 minutes via Task Scheduler.

---

## 10. Common ops tasks

| Task                          | Command                                              |
|-------------------------------|------------------------------------------------------|
| Start                         | `Start-Service MFilesExporter`                       |
| Stop (graceful)               | `Stop-Service  MFilesExporter`                       |
| Restart                       | `Restart-Service MFilesExporter`                     |
| Current status (SCM)          | `Get-Service MFilesExporter`                         |
| Current status (data)         | `MFilesExporter.Console.exe --status`                |
| Tail live log                 | `Get-Content …\logs\mfilesexporter-*.log -Tail 50 -Wait` |
| Tail errors only              | `Get-Content …\logs\errors-*.log -Tail 20 -Wait`     |
| Change log level              | Edit `Serilog:MinimumLevel:Default` → restart        |
| Change worker count           | Edit `Exporter:ParallelProcessing:WorkerCount` → restart |
| Bump graceful-stop timeout    | `sc.exe control MFilesExporter --timeout 120000`     |

Stopping the service is **safe at any time**: the checkpoint engine
flushes the WAL, in-flight work drains within
`Exporter:ParallelProcessing:GracefulShutdownTimeout` (default 30 s),
and the next start resumes from exactly where it left off.

---

## 11. Upgrading in place

```powershell
# 1. Stop the service. Wait for graceful drain.
Stop-Service MFilesExporter

# 2. Publish the new build over the top.
.\deploy\windows-service\publish.ps1 -InstallPath "D:\Services\MFilesExporter"

# 3. Re-apply any tracking-DB migrations. (Every schema file is idempotent.)
foreach ($f in Get-ChildItem database/*.sql | Where-Object Name -notlike '00-*' | Sort-Object Name) {
    sqlcmd -S <TRACKING-DB> -d MFilesExportTracking -i $f.FullName
}

# 4. Restart.
Start-Service MFilesExporter
```

The publish step **overwrites** `appsettings.json`. Keep environment-
specific overrides in `appsettings.Production.json` (which is preserved
by `PreserveNewest`) or in environment variables so upgrades don't
clobber your config.

---

## 12. Uninstall

```powershell
.\deploy\windows-service\uninstall.ps1
```

Stops the service, releases the URL ACL, deletes the SCM registration.
Install directory, logs, checkpoints, and tracking-DB rows are **not
touched** — remove them manually only if you're decommissioning.

---

## 13. Troubleshooting matrix

| Symptom                                                     | Likely cause                                                                            | Fix |
|-------------------------------------------------------------|-----------------------------------------------------------------------------------------|-----|
| Service starts, then stops within seconds                   | Invalid `appsettings.json` — FluentValidation fails at startup                          | Check `logs\mfilesexporter-*.log`; fix the highlighted field |
| `Access is denied` on `http://+:9464/`                      | URL ACL not registered for the service account                                          | Re-run `install.ps1` or `netsh http add urlacl url=http://+:9464/ user=<ACCOUNT>` |
| `--status` prints "connection string not configured"        | `Exporter:TrackingDatabase:ConnectionString` missing                                     | Set in `appsettings.json` or `MFILESEXPORTER_Exporter__TrackingDatabase__ConnectionString` |
| `--status` returns no rows                                  | No job in `Running` state; the exporter isn't started, or the last run has completed    | `Get-Service MFilesExporter` + inspect `dbo.ExportJobs`; use `07-active-jobs.sql` |
| Service takes > 30 s to stop                                | In-flight BLOB reads exceed `GracefulShutdownTimeout`                                    | Increase `Exporter:ParallelProcessing:GracefulShutdownTimeout` AND the SCM timeout (`sc.exe control … --timeout`) |
| `SqlDeadlock` failures dominate `03-failures-by-category`   | Vault DB contention with another workload                                                | Reduce `Pipeline:ContentReaderConcurrency`; verify `UseReadUncommittedForEnumeration: true` |
| Disk-free gauge dropping fast                               | Output volume too small for the export estimate                                          | Move `Storage:RootPath` to a larger volume, or turn on compression at the FS layer |
| Multiple stalled workers                                    | Vault DB unreachable, network partition, disk write starvation                          | Check `06-worker-health.sql`; correlate with error log |
| `NetworkService cannot access domain share`                 | Using `NetworkService` for cross-machine writes                                          | Reinstall with `-ServiceAccount <DOMAIN>\svc-mfiles` (§4.b) |
| Prometheus scrape times out (only if you enabled it)        | Firewall blocks 9464 inbound                                                             | `New-NetFirewallRule -DisplayName "MFilesExporter Metrics" -Direction Inbound -LocalPort 9464 -Protocol TCP -Action Allow` |
| After reboot the service starts before SQL Server is ready  | Delayed-start is set, but not delayed *enough*                                          | `sc.exe config MFilesExporter start= delayed-auto` (default), or add a service dependency |

---

## 14. What to look at when things go wrong

1. **`--status`** — one row tells you the shape of the problem.
2. **`logs\errors-*.log`** — every `Warning`/`Error`/`Fatal` in structured JSON.
3. **`dbo.ExportErrors`** — persisted error rows with full context; query via
   `deploy\reports\05-recent-errors.sql`.
4. **`Get-EventLog Application -Source MFilesExporter -Newest 20`** — the coarse
   lifecycle events the Windows Service surface publishes.
5. **`06-worker-health.sql`** — is any worker stalled?
6. **`08-checkpoint-current.sql`** — did the WAL advance during the failure
   window? If not, the pipeline was stuck upstream of the sink.

Every layer of the exporter attaches the `CorrelationId` property to its
logs and to `ExportErrors.CorrelationId`, so once you find a suspect
event you can pivot to every other event from the same document in one
query.

---

## 15. Reference

- [docs/deployment-windows-service.md](deployment-windows-service.md) — service-specific detail
- [docs/configuration.md](configuration.md) — every `Exporter:*` option
- [docs/status-reports.md](status-reports.md) — the reports catalogue
- [docs/logging.md](logging.md) — log sink layout and retention
- [docs/database.md](database.md) — tracking-DB schema
- [docs/checkpoint-engine.md](checkpoint-engine.md) — resume/recovery guarantees
- [docs/retry-handling.md](retry-handling.md) — what the exporter retries and why
