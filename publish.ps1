#requires -Version 5.1
<#
.SYNOPSIS
    Publishes Vivre (the WPF Desktop app) ready to copy to another machine.

.DESCRIPTION
    Wraps `dotnet publish` of source\Vivre.Desktop. By default it produces a
    self-contained win-x64 build: no .NET install is needed on the target -- just copy
    the output folder over and run Vivre.exe. The curated Scripts\ folder is
    included, so the Run-script menu seeds correctly on the target.

    Use -FrameworkDependent for a small (~5 MB) build that instead requires the
    .NET 10 Desktop Runtime (x64) to be installed on the target.

    Output goes to .\publish\Vivre-<runtime>[ -fxdep ] (the publish\ folder is git-ignored).

.PARAMETER Runtime
    Target runtime identifier. Default win-x64 (use win-arm64 for ARM).

.PARAMETER Configuration
    Build configuration. Default Release.

.PARAMETER FrameworkDependent
    Build framework-dependent (small; needs the .NET 10 Desktop Runtime on the target)
    instead of self-contained.

.PARAMETER Zip
    Also produce a .zip of the output next to the folder (easy to copy / share).

.PARAMETER OutputDir
    Override the output folder.

.EXAMPLE
    .\publish.ps1
    Self-contained win-x64 into .\publish\Vivre-win-x64.

.EXAMPLE
    .\publish.ps1 -Zip
    Self-contained win-x64, plus .\publish\Vivre-win-x64.zip.

.EXAMPLE
    .\publish.ps1 -FrameworkDependent
    Small build; target needs the .NET 10 Desktop Runtime.
#>
[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    [switch]$FrameworkDependent,
    [switch]$Zip,
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'source\Vivre.Desktop\Vivre.Desktop.csproj'
if (-not (Test-Path $project)) { throw "Project not found: $project" }

$selfContained = -not $FrameworkDependent
$selfContainedArg = if ($selfContained) { 'true' } else { 'false' }
$suffix = if ($FrameworkDependent) { "$Runtime-fxdep" } else { $Runtime }
if (-not $OutputDir) { $OutputDir = Join-Path $PSScriptRoot "publish\Vivre-$suffix" }

Write-Host "Publishing Vivre" -ForegroundColor Cyan
Write-Host "  configuration  : $Configuration"
Write-Host "  runtime        : $Runtime"
Write-Host "  self-contained : $selfContained"
Write-Host "  output         : $OutputDir"
Write-Host ""

# Clean previous output so stale files never linger in the deployable folder.
if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }

dotnet publish $project -c $Configuration -r $Runtime --self-contained $selfContainedArg -o $OutputDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }

$exe = Join-Path $OutputDir 'Vivre.exe'
if (-not (Test-Path $exe)) { throw "Publish finished but $exe is missing." }

$files = Get-ChildItem -LiteralPath $OutputDir -Recurse -File
$sizeMb = [math]::Round((($files | Measure-Object Length -Sum).Sum) / 1MB, 1)
Write-Host ""
Write-Host "Published $($files.Count) files, $sizeMb MB" -ForegroundColor Green
Write-Host "  run: $exe"

if ($Zip) {
    $zipPath = "$OutputDir.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $OutputDir '*') -DestinationPath $zipPath
    $zipMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
    Write-Host "  zip: $zipPath ($zipMb MB)" -ForegroundColor Green
}

if ($FrameworkDependent) {
    Write-Host ""
    Write-Host "Framework-dependent build: the target needs the .NET 10 Desktop Runtime (x64):" -ForegroundColor Yellow
    Write-Host "  https://dotnet.microsoft.com/download/dotnet/10.0"
}
