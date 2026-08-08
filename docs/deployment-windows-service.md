# Deployment: Windows Service

> _Target: Windows Server 2019/2022, .NET 9 runtime OR self-contained publish_
> _Scripts: `deploy/windows-service/{publish,install,uninstall}.ps1`_

## 1. Why a Windows Service (not IIS)

The exporter is a **stateful, single-instance, multi-hour-to-multi-day batch
job**. IIS is built around HTTP request lifecycles (idle timeout, app-pool
recycling, overlapped recycles on config change, rapid-fail protection) —
every one of those actively conflicts with a long-running batch job:

- An overlapped recycle would start a **second** worker process against the
  same source vault mid-run.
- Idle timeout (20 min default) would kill the process the moment HTTP
  traffic dropped — but the exporter has no HTTP traffic to keep it alive.
- The tracking-DB checkpoint recovery works, but pointless restarts still
  cost time.

Windows Service is what this workload was designed for: no request
lifecycle, no recycling, native auto-restart on failure, dedicated service
account, EventLog + Serilog integration.

## 2. What the runtime detects

`Program.cs` calls `WindowsServiceHelpers.IsWindowsService()` — true iff the
process was started by the Service Control Manager. Under `sc start` the
host is upgraded with `AddWindowsService()`; under `dotnet run` it stays a
plain console host. **The same binary works both ways** — no separate
"service" project.

Additionally, when running as a service the ContentRoot is forced to the
executable directory (services default to `%WINDIR%\System32` as cwd,
which would break every relative path in `appsettings.json`).

## 3. Publish

The service must be published as a **self-contained single-file** artifact so
the target box does not need the .NET SDK installed. Only the .NET 9
runtime bundle would suffice if you prefer a framework-dependent deploy,
but self-contained is the more forgiving default.

```powershell
# From the repo root, in an elevated PowerShell:
.\deploy\windows-service\publish.ps1 -InstallPath "D:\Services\MFilesExporter"
```

This runs:

```
dotnet publish src\MFilesExporter.Console\MFilesExporter.Console.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o D:\Services\MFilesExporter
```

Output includes:

- `MFilesExporter.Console.exe` — the single-file bundle (~90 MiB with runtime).
- `appsettings.json` — the default committed config, **must be edited**.
- Native dependencies for Microsoft.Data.SqlClient (Kerberos/SNI).

## 4. Configure

Before installing the service, edit `D:\Services\MFilesExporter\appsettings.json`:

```jsonc
"Exporter": {
  "Source":           { "ConnectionString": "Server=vault-db;…" },
  "TrackingDatabase": { "ConnectionString": "Server=tracking-db;…" },
  "StateStore":       { "ConnectionString": "D:\\Services\\MFilesExporter\\state.db" },
  "Storage":          { "RootPath": "E:\\ExportOutput\\documents" },
  "FileExport":       { "RootPath": "E:\\ExportOutput\\documents" },
  "Metadata":         { "OutputDirectory": "E:\\ExportOutput\\metadata" },
  "Checkpoint":       { "WalDirectory": "D:\\Services\\MFilesExporter\\checkpoints" },
  "Dashboard":        { "Enabled": false }  // Suppressed under services anyway.
}
```

Use **absolute paths** — the service's `ContentRootPath` is set to the
install directory, so relative paths resolve there, but absolute paths are
easier to reason about when triaging under stress.

For secrets, prefer environment variables set on the service account
rather than committing to `appsettings.json`. The service picks up any
variable prefixed `MFILESEXPORTER_`:

```
MFILESEXPORTER_Exporter__Source__ConnectionString="…"
MFILESEXPORTER_Exporter__TrackingDatabase__ConnectionString="…"
```

## 5. Install

Elevated PowerShell:

```powershell
# Under NetworkService (cannot access domain shares):
.\deploy\windows-service\install.ps1 -InstallPath "D:\Services\MFilesExporter"

# Under a domain account (recommended when reading the M-Files vault
# over Kerberos or writing to a network share):
.\deploy\windows-service\install.ps1 `
    -InstallPath   "D:\Services\MFilesExporter" `
    -ServiceAccount "CONTOSO\svc-mfiles"
```

The installer:

1. Removes any existing `MFilesExporter` registration (idempotent).
2. Registers the service with `StartupType = AutomaticDelayedStart`
   (delayed so SQL Server has time to come up after a reboot).
3. Sets a failure-recovery policy: **restart 3× with 60 s delay** before
   the SCM gives up.
4. Reserves `http://+:9464/` for the service account via `netsh http
   add urlacl` (required by the OpenTelemetry Prometheus HTTP listener
   when running as a non-admin account).

## 6. Service account requirements

Regardless of whether you use `NetworkService` or a domain account, grant:

- **Read** on the M-Files vault SQL Server (`db_datareader` on the vault DB).
- **Read/write** on the tracking DB (`db_datareader`, `db_datawriter`,
  and `EXECUTE` on the tracking DB's schemas).
- **Modify** on the storage volume (`E:\ExportOutput` in the sample above).
- **Log on as a service** local right (granted by Group Policy or manually
  via `secpol.msc` → Local Policies → User Rights Assignment).

Domain accounts additionally need:

- **`SeServiceLogonRight`** on the target host.
- Kerberos SPN on the SQL Server if you're using Integrated Security across
  domains (`setspn -A MSSQLSvc/…`).

## 7. Start, monitor, stop

```powershell
Start-Service MFilesExporter
Get-Service   MFilesExporter
Stop-Service  MFilesExporter
```

Health signals:

- **SCM status** — `Get-Service MFilesExporter` (running / stopped).
- **Prometheus** — `http://<host>:9464/metrics`. Scrape config in
  `deploy/prometheus/scrape-config.yml`. Grafana dashboard in
  `deploy/grafana/dashboard.json`.
- **Structured logs** — `D:\Services\MFilesExporter\logs\`:
  - `mfilesexporter-*.log` (all, 30-day retention),
  - `errors-*.log` (Warning+, 90-day retention),
  - `audit-*.log` (immutable, 2555-day retention — ship to WORM),
  - `performance-*.log`,
  - `workers-*.log`.
- **Windows Event Log** — the service publishes Start/Stop/Fatal events
  to Application under source `MFilesExporter`. Serilog is the primary
  log surface; Event Log is a coarse fallback for the SCM to inspect.
- **Seq** (optional) — if Prometheus/Grafana isn't your stack, drop in
  `deploy/windows-service/appsettings.Seq.example.json` renamed as
  `appsettings.Production.json` and you get structured-log search +
  dashboards in one pane. See [docs/logging.md § 13](logging.md).

## 8. Stopping gracefully

`Stop-Service MFilesExporter` sends SCM's `SERVICE_CONTROL_STOP`, which the
Generic Host converts to a `CancellationToken` cancel on the application
lifetime. Every background service (pipeline, checkpoint, progress)
honours the token: in-flight work drains, the checkpoint engine flushes
its WAL, tracking-DB writes are committed, and the process exits.

The **graceful-shutdown budget** is the generic host's
`HostOptions.ShutdownTimeout` (default 30 s) — tune this in `Program.cs`
if your BLOB reads regularly exceed it. Setting it larger than the SCM's
default stop timeout of 30 s requires bumping the SCM value too:

```powershell
sc.exe control MFilesExporter --timeout 120000   # 120 s
```

Or set the machine-wide default via the registry:

```
HKLM\SYSTEM\CurrentControlSet\Control\WaitToKillServiceTimeout
```

## 9. App pool concerns — none

There are none. This is a Windows Service, not IIS. There's no worker
process to recycle, no idle timeout, no rapid-fail protection, no
overlapped rotation. If the process dies, the SCM's failure-recovery
policy restarts it up to three times; then it stays stopped and pages
the on-call via your monitoring stack.

## 10. Uninstalling

```powershell
.\deploy\windows-service\uninstall.ps1
```

Stops the service (30 s grace), releases the URL reservation, deletes the
registration. The install directory, logs, tracking-DB, checkpoints, and
output are **not touched** — remove them manually if you're decomissioning
rather than reinstalling.

## 11. Troubleshooting

| Symptom                                                      | Cause                                                                                  | Fix |
|--------------------------------------------------------------|----------------------------------------------------------------------------------------|-----|
| Service starts then immediately stops                        | Invalid `appsettings.json` — validation runs at startup, throws, host exits            | Check `logs/mfilesexporter-*.log` for the FluentValidation errors |
| `Access is denied` on `http://+:9464/`                       | URL not reserved for the service account                                               | Re-run `install.ps1` or `netsh http add urlacl url=http://+:9464/ user=…` |
| Service takes > 30 s to stop                                 | In-flight BLOB reads exceed the host shutdown timeout                                  | Raise `HostOptions.ShutdownTimeout` in Program.cs AND the SCM timeout |
| Cannot access source vault under NetworkService              | NetworkService has no domain identity                                                  | Reinstall with `-ServiceAccount "DOMAIN\svc-mfiles"` |
| Relative paths (`./export-output`) resolved to `%WINDIR%\System32` | Old .NET service without ContentRoot fix                                          | Confirm you're on this build — `Program.cs` sets ContentRoot when `IsWindowsService()` is true |
| Prometheus scrape times out                                  | Firewall blocks 9464 inbound                                                           | `New-NetFirewallRule -DisplayName "MFilesExporter Metrics" -Direction Inbound -LocalPort 9464 -Protocol TCP -Action Allow` |

## 12. Reference

- [Docs · monitoring](monitoring.md) — Prometheus / Grafana / OTLP.
- [Docs · logging](logging.md) — sink layout + retention.
- [Docs · configuration](configuration.md) — every `Exporter:*` field.
- [Docs · checkpoint-engine](checkpoint-engine.md) — recovery guarantees.
