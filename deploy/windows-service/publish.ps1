<#
.SYNOPSIS
Publishes MFilesExporter as a self-contained single-file Windows Service
build ready for `install.ps1`.

.DESCRIPTION
Runs `dotnet publish` with the right RID / trimming / packaging switches for
a Windows Server deployment target. Copies the output into the requested
InstallPath so the operator can point install.ps1 at it.

.PARAMETER InstallPath
Absolute path to publish into. Cleared and recreated.

.PARAMETER Runtime
RID passed to dotnet publish. win-x64 by default.

.PARAMETER Configuration
Release by default.

.EXAMPLE
.\publish.ps1 -InstallPath "D:\Services\MFilesExporter"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string] $InstallPath,

    [string] $Runtime       = "win-x64",
    [string] $Configuration = "Release",
    [string] $ProjectPath   = "src\MFilesExporter.Console\MFilesExporter.Console.csproj"
)

$ErrorActionPreference = "Stop"

if (Test-Path $InstallPath)
{
    Write-Host "Clearing existing $InstallPath…"
    Remove-Item -Recurse -Force $InstallPath
}
New-Item -ItemType Directory -Path $InstallPath | Out-Null

Write-Host "Publishing $ProjectPath → $InstallPath ($Runtime, $Configuration)…"

dotnet publish $ProjectPath `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    --output $InstallPath

if ($LASTEXITCODE -ne 0)
{
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Write-Host ""
Write-Host "Published. Next steps:"
Write-Host "  1. Edit $InstallPath\appsettings.json (connection strings, paths)."
Write-Host "  2. Run install.ps1 -InstallPath '$InstallPath'"
