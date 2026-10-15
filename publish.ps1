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
. (Join-Path $PSScriptRoot 'tools\publish-safety.ps1')

$project = Join-Path $PSScriptRoot 'source\Vivre.Desktop\Vivre.Desktop.csproj'
if (-not (Test-Path $project)) { throw "Project not found: $project" }

# Validate signing prerequisites before removing any previous publish output.
[xml]$agentProject = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'source\Vivre.UpdateAgent\Vivre.UpdateAgent.csproj')
$thumbprint = $agentProject.SelectSingleNode('//AgentSignThumbprint').InnerText.Trim()
$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint" -ErrorAction Stop
if (-not $certificate.HasPrivateKey) { throw 'Agent signing certificate has no private key.' }

$selfContained = -not $FrameworkDependent
$selfContainedArg = if ($selfContained) { 'true' } else { 'false' }
$suffix = if ($FrameworkDependent) { "$Runtime-fxdep" } else { $Runtime }
if (-not $OutputDir) { $OutputDir = Join-Path $PSScriptRoot "publish\Vivre-$suffix" }
$OutputDir = Assert-VivrePublishTarget -Path $OutputDir -RepositoryRoot $PSScriptRoot
$outputParent = [IO.Directory]::GetParent($OutputDir).FullName
if (-not (Test-Path -LiteralPath $outputParent)) { New-Item -ItemType Directory -Path $outputParent -Force | Out-Null }
$candidateId = [Guid]::NewGuid().ToString('N')
$candidateDir = Join-Path $outputParent ('.' + [IO.Path]::GetFileName($OutputDir) + '.candidate-' + $candidateId)
$zipPath = "$OutputDir.zip"
$checksumPath = "$zipPath.sha256"
$candidateZip = Join-Path $outputParent ('.' + [IO.Path]::GetFileNameWithoutExtension($zipPath) + '.candidate-' + $candidateId + '.zip')
$candidateChecksum = Join-Path $outputParent ('.' + [IO.Path]::GetFileName($checksumPath) + '.candidate-' + $candidateId)
if ($Zip -and ((Test-Path -LiteralPath $zipPath) -or (Test-Path -LiteralPath $checksumPath))) {
    $ownerMarker = Join-Path $OutputDir '.vivre-publish-owner.json'
    if (-not (Test-Path -LiteralPath $ownerMarker -PathType Leaf)) {
        throw "Refusing to replace adjacent archive artifacts without an owned publish folder: $zipPath"
    }
}
$publishLease = Enter-VivrePublishLease -TargetPath $OutputDir

Write-Host "Publishing Vivre" -ForegroundColor Cyan
Write-Host "  configuration  : $Configuration"
Write-Host "  runtime        : $Runtime"
Write-Host "  self-contained : $selfContained"
Write-Host "  output         : $OutputDir"
Write-Host ""

try {
dotnet publish $project -p:RestoreLockedMode=true -c $Configuration -r $Runtime --self-contained $selfContainedArg -o $candidateDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE); the last-good publish was preserved." }

$exe = Join-Path $candidateDir 'Vivre.exe'
if (-not (Test-Path $exe)) { throw "Publish finished but $exe is missing." }

# A deploy package must contain the agent and carry the configured publisher signature.
# Run in the publishing host so signing does not depend on MSBuild's child-process store view.
$agent = Join-Path $candidateDir 'Vivre.UpdateAgent.exe'
if (-not (Test-Path -LiteralPath $agent)) { throw "Published update agent is missing: $agent" }
$signature = Set-AuthenticodeSignature -LiteralPath $agent -Certificate $certificate -HashAlgorithm SHA256
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $thumbprint) {
    throw "Agent signing validation failed: $($signature.Status) - $($signature.StatusMessage)"
}
Write-Host "Verified agent signature: $thumbprint" -ForegroundColor Green
foreach ($required in @('Modules', 'Scripts', 'coreclr.dll', 'Vivre.runtimeconfig.json')) {
    # Framework-dependent packages intentionally omit the runtime.
    if ($FrameworkDependent -and $required -eq 'coreclr.dll') { continue }
    if (-not (Test-Path -LiteralPath (Join-Path $candidateDir $required))) { throw "Published component missing: $required" }
}

$version = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
Write-VivrePublishOwner -Directory $candidateDir -TargetPath $OutputDir
$manifest = [ordered]@{
    kind = 'VivrePublishManifest'
    version = $version
    runtime = $Runtime
    selfContained = $selfContained
    createdUtc = [DateTime]::UtcNow.ToString('o')
} | ConvertTo-Json
Set-Content -LiteralPath (Join-Path $candidateDir 'publish-manifest.json') -Value $manifest -Encoding UTF8

Assert-VivreArtifactTree -Path $candidateDir
$files = Get-ChildItem -LiteralPath $candidateDir -Recurse -File
$sizeMb = [math]::Round((($files | Measure-Object Length -Sum).Sum) / 1MB, 1)

$artifacts = @([PSCustomObject]@{ Live = $OutputDir; Candidate = $candidateDir })
if ($Zip) {
    Compress-Archive -Path (Join-Path $candidateDir '*') -DestinationPath $candidateZip
    if (-not (Test-Path -LiteralPath $candidateZip) -or (Get-Item -LiteralPath $candidateZip).Length -eq 0) {
        throw 'Compression did not produce a nonempty archive; the last-good publish was preserved.'
    }
    Test-VivrePublishArchive -ArchivePath $candidateZip -SourceDirectory $candidateDir
    $digest = (Get-FileHash -LiteralPath $candidateZip -Algorithm SHA256).Hash
    "$digest  $([IO.Path]::GetFileName($zipPath))" | Set-Content -LiteralPath $candidateChecksum -Encoding ASCII
    $artifacts += [PSCustomObject]@{ Live = $zipPath; Candidate = $candidateZip }
    $artifacts += [PSCustomObject]@{ Live = $checksumPath; Candidate = $candidateChecksum }
}

Invoke-VivreArtifactPromotion -Artifacts $artifacts
$exe = Join-Path $OutputDir 'Vivre.exe'
Write-Host ""
Write-Host "Published Vivre $version ($($files.Count) files, $sizeMb MB)" -ForegroundColor Green
Write-Host "  run: $exe"
if (-not $Zip -and (Test-Path -LiteralPath $zipPath)) { Write-Warning "Existing ZIP was NOT updated by this folder-only publish: $zipPath" }
if ($Zip) {
    $zipMb = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)
    Write-Host "  zip: $zipPath ($zipMb MB)" -ForegroundColor Green
    Write-Host "  SHA256: $digest"
}
}
finally {
    try {
        foreach ($temporaryPath in @($candidateDir, $candidateZip, $candidateChecksum)) {
            if (Test-Path -LiteralPath $temporaryPath) {
                try { Assert-VivreArtifactTree -Path $temporaryPath; Remove-Item -LiteralPath $temporaryPath -Recurse -Force }
                catch { Write-Warning "Temporary publish files retained at '$temporaryPath': $($_.Exception.Message)" }
            }
        }
    }
    finally { if ($publishLease) { $publishLease.Dispose() } }
}

if ($FrameworkDependent) {
    Write-Host ""
    Write-Host "Framework-dependent build: the target needs the .NET 10 Desktop Runtime (x64):" -ForegroundColor Yellow
    Write-Host "  https://dotnet.microsoft.com/download/dotnet/10.0"
}
