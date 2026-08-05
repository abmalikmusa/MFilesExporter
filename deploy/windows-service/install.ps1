<#
.SYNOPSIS
Registers MFilesExporter as a Windows Service.

.DESCRIPTION
Installs the service so it starts under the given account, sets it to
Automatic start with delayed start (so SQL Server has time to come up
after a reboot), configures failure recovery, and pins the working
directory to the install path.

Run in an elevated PowerShell session.

.PARAMETER InstallPath
Absolute path to the folder containing MFilesExporter.Console.exe.

.PARAMETER ServiceAccount
Optional account (DOMAIN\User). If omitted, the service runs under
NT AUTHORITY\NetworkService. NetworkService cannot access domain shares —
use a domain account for anything shared over the network.

.PARAMETER ServiceAccountPassword
SecureString password for the service account. Required when
-ServiceAccount is specified. Prompted interactively if omitted.

.EXAMPLE
.\install.ps1 -InstallPath "D:\Services\MFilesExporter"

.EXAMPLE
.\install.ps1 -InstallPath "D:\Services\MFilesExporter" `
              -ServiceAccount "CONTOSO\svc-mfiles"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [ValidateScript({ Test-Path $_ -PathType Container })]
    [string] $InstallPath,

    [string] $ServiceAccount,

    [SecureString] $ServiceAccountPassword,

    [string] $ServiceName = "MFilesExporter",

    [string] $DisplayName = "MFiles Exporter",

    [string] $Description = "Streams documents from an M-Files SQL Server vault to durable storage. See docs/deployment-windows-service.md."
)

$ErrorActionPreference = "Stop"

# ----------------------------------------------------------------------
# Preflight
# ----------------------------------------------------------------------
if (-not ([Security.Principal.WindowsPrincipal] `
        [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator))
{
    throw "This script must be run in an elevated PowerShell session."
}

$exePath = Join-Path $InstallPath "MFilesExporter.Console.exe"
if (-not (Test-Path $exePath))
{
    throw "MFilesExporter.Console.exe not found at $exePath. Publish the app there first (see docs)."
}

if ($ServiceAccount -and -not $ServiceAccountPassword)
{
    $ServiceAccountPassword = Read-Host "Password for $ServiceAccount" -AsSecureString
}

# ----------------------------------------------------------------------
# Remove existing service (idempotent reinstall)
# ----------------------------------------------------------------------
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing)
{
    Write-Host "Existing service '$ServiceName' found — stopping and removing…"
    if ($existing.Status -ne "Stopped") { Stop-Service $ServiceName -Force }
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# ----------------------------------------------------------------------
# Create the service
# ----------------------------------------------------------------------
Write-Host "Creating service '$ServiceName' → $exePath"

$args = @{
    Name           = $ServiceName
    DisplayName    = $DisplayName
    Description    = $Description
    BinaryPathName = "`"$exePath`""
    StartupType    = "AutomaticDelayedStart"
}

if ($ServiceAccount)
{
    $args["Credential"] = [System.Management.Automation.PSCredential]::new(
        $ServiceAccount, $ServiceAccountPassword)
}

New-Service @args | Out-Null

# ----------------------------------------------------------------------
# Failure recovery: restart 3× before giving up. Each restart is 60 s later.
# ----------------------------------------------------------------------
Write-Host "Configuring failure recovery…"
sc.exe failure   $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
sc.exe failureflag $ServiceName 1 | Out-Null

# ----------------------------------------------------------------------
# Grant the service account the required URL ACL for the Prometheus
# HTTP listener (http://+:9464/). Skip this if you set
# Exporter:Telemetry:EnablePrometheusEndpoint to false.
# ----------------------------------------------------------------------
$prometheusUrl = "http://+:9464/"
$account       = if ($ServiceAccount) { $ServiceAccount } else { "NT AUTHORITY\NetworkService" }
Write-Host "Reserving $prometheusUrl for $account…"
netsh http add urlacl url=$prometheusUrl user="$account" | Out-Null

Write-Host ""
Write-Host "Service '$ServiceName' installed."
Write-Host "  Start:  Start-Service $ServiceName"
Write-Host "  Status: Get-Service   $ServiceName"
Write-Host "  Stop:   Stop-Service  $ServiceName"
Write-Host "  Logs:   $InstallPath\logs\"
