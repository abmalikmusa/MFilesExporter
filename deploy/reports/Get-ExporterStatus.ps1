<#
.SYNOPSIS
One-line status report against the MFilesExportTracking database.

.DESCRIPTION
Prints a formatted summary of the target job — processed, remaining,
throughput, worker health, top error categories — using nothing more
than sqlcmd and PowerShell's built-in Format-Table.

Works against any SQL Server login the caller has read access to;
default is Integrated Security.

.PARAMETER Server
Tracking DB host. Required.

.PARAMETER Database
Tracking DB name. Defaults to MFilesExportTracking.

.PARAMETER JobId
Optional job id. Defaults to the latest Running job.

.PARAMETER User / Password
Optional SQL login. Omit for Integrated Security.

.EXAMPLE
.\Get-ExporterStatus.ps1 -Server tracking-db

.EXAMPLE
.\Get-ExporterStatus.ps1 -Server tracking-db -JobId 42
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string] $Server,

    [string] $Database = "MFilesExportTracking",

    [int64]  $JobId,

    [string] $User,
    [SecureString] $Password
)

$ErrorActionPreference = "Stop"

$here = Split-Path -Parent $MyInvocation.MyCommand.Path

# ---------------------------------------------------------------------------
# Compose sqlcmd auth args
# ---------------------------------------------------------------------------
$authArgs = if ($User) {
    if (-not $Password) { $Password = Read-Host "Password for $User" -AsSecureString }
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringUni(
        [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($Password))
    @("-U", $User, "-P", $plain)
} else {
    @("-E")
}

function Invoke-Report {
    param([string] $ScriptPath, [string] $Title)

    $sql = Get-Content $ScriptPath -Raw
    if ($PSBoundParameters.ContainsKey('JobId')) {
        $sql = $sql -replace 'DECLARE @JobId BIGINT\s*=\s*NULL;', "DECLARE @JobId BIGINT = $JobId;"
    }

    $tmp = New-TemporaryFile
    try {
        $sql | Set-Content -Path $tmp.FullName -Encoding UTF8

        Write-Host ""
        Write-Host "─── $Title ───" -ForegroundColor Cyan

        & sqlcmd -S $Server -d $Database @authArgs -i $tmp.FullName -W -s "|" `
                 -h -1 -Y 40 -y 200
        if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed for $ScriptPath ($LASTEXITCODE)" }
    }
    finally {
        Remove-Item $tmp.FullName -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------------------
Invoke-Report -ScriptPath (Join-Path $here "01-status-summary.sql")    -Title "Status summary"
Invoke-Report -ScriptPath (Join-Path $here "02-outcomes-breakdown.sql") -Title "Outcomes"
Invoke-Report -ScriptPath (Join-Path $here "06-worker-health.sql")     -Title "Workers"
Invoke-Report -ScriptPath (Join-Path $here "03-failures-by-category.sql") -Title "Failures by category"
Invoke-Report -ScriptPath (Join-Path $here "08-checkpoint-current.sql") -Title "Checkpoint"

Write-Host ""
Write-Host "Done." -ForegroundColor Green
