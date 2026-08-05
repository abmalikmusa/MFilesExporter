<#
.SYNOPSIS
Removes the MFilesExporter Windows Service. Idempotent.

.DESCRIPTION
Stops the service (if running), releases the Prometheus URL reservation,
and deletes the service registration. The install directory, logs, and
appsettings are NOT touched — remove them manually if needed.

Run in an elevated PowerShell session.

.EXAMPLE
.\uninstall.ps1
#>
[CmdletBinding()]
param(
    [string] $ServiceName    = "MFilesExporter",
    [string] $PrometheusUrl  = "http://+:9464/"
)

$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal] `
        [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator))
{
    throw "This script must be run in an elevated PowerShell session."
}

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc)
{
    if ($svc.Status -ne "Stopped")
    {
        Write-Host "Stopping $ServiceName…"
        Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
        # Wait up to 30 s for a clean stop before deleting.
        (Get-Service $ServiceName).WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }

    Write-Host "Deleting service registration…"
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}
else
{
    Write-Host "Service '$ServiceName' is not installed — nothing to remove."
}

Write-Host "Releasing URL reservation $PrometheusUrl (ignore errors if not set)…"
netsh http delete urlacl url=$PrometheusUrl 2>$null | Out-Null

Write-Host "Done."
