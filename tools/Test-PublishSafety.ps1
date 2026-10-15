#requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'publish-safety.ps1')

$root = Join-Path ([IO.Path]::GetTempPath()) ('.vivre-publish-test-' + [Guid]::NewGuid().ToString('N'))
$repo = Join-Path $root 'repo'
$publishRoot = Join-Path $repo 'publish'
New-Item -ItemType Directory -Path (Join-Path $repo 'source') -Force | Out-Null
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

function Assert-ThrowsPreservingSentinel {
    param([string]$Target)
    $sentinel = Join-Path $Target 'sentinel.txt'
    New-Item -ItemType Directory -Path $Target -Force | Out-Null
    Set-Content -LiteralPath $sentinel -Value 'keep'
    $threw = $false
    try { Assert-VivrePublishTarget -Path $Target -RepositoryRoot $repo | Out-Null } catch { $threw = $true }
    if (-not $threw) { throw "Expected unsafe target rejection: $Target" }
    if ((Get-Content -LiteralPath $sentinel -Raw).Trim() -ne 'keep') { throw "Sentinel changed: $sentinel" }
}

try {
    Assert-ThrowsPreservingSentinel -Target (Join-Path $repo 'source')
    Assert-ThrowsPreservingSentinel -Target (Join-Path $repo 'unowned-output')

    $owned = Join-Path $publishRoot 'Vivre-test'
    New-Item -ItemType Directory -Path $owned | Out-Null
    Write-VivrePublishOwner -Directory $owned -TargetPath $owned
    Set-Content -LiteralPath (Join-Path $owned 'last-good.txt') -Value 'old'
    $resolved = Assert-VivrePublishTarget -Path ([string]$owned) -RepositoryRoot ([string]$repo)
    if (-not (Test-VivrePathEqual $resolved $owned)) { throw 'Owned target resolved incorrectly.' }

    $outside = Join-Path $root 'outside-publish'
    New-Item -ItemType Directory -Path $outside | Out-Null
    $outsideSentinel = Join-Path $outside 'keep.txt'
    Set-Content -LiteralPath $outsideSentinel -Value 'untouched'
    $junction = Join-Path $owned 'linked-content'
    New-Item -ItemType Junction -Path $junction -Target $outside | Out-Null
    try {
        $threw = $false
        try { Assert-VivrePublishTarget -Path $owned -RepositoryRoot $repo | Out-Null } catch { $threw = $true }
        if (-not $threw) { throw 'Owned output descendant junction was accepted.' }
        $threw = $false
        try { Invoke-VivreArtifactPromotion -Artifacts @([PSCustomObject]@{ Live = (Join-Path $publishRoot 'new-output'); Candidate = $owned }) } catch { $threw = $true }
        if (-not $threw) { throw 'Candidate descendant junction was accepted.' }
        if ((Get-Content -LiteralPath $outsideSentinel -Raw).Trim() -ne 'untouched') { throw 'Outside junction sentinel changed.' }
        if (-not (Test-Path -LiteralPath (Join-Path $owned 'last-good.txt'))) { throw 'Rejected candidate was moved.' }
    }
    finally { [IO.Directory]::Delete($junction) }

    $missingCandidate = Join-Path $publishRoot '.missing-candidate'
    $threw = $false
    try { Invoke-VivreArtifactPromotion -Artifacts @([PSCustomObject]@{ Live = $owned; Candidate = $missingCandidate }) } catch { $threw = $true }
    if (-not $threw) { throw 'Missing candidate should fail promotion.' }
    if ((Get-Content -LiteralPath (Join-Path $owned 'last-good.txt') -Raw).Trim() -ne 'old') {
        throw 'Failed publication did not preserve the last-good artifact.'
    }

    $archiveSource = Join-Path $root 'archive-source'
    $archivePath = Join-Path $root 'candidate.zip'
    New-Item -ItemType Directory -Path $archiveSource | Out-Null
    Set-Content -LiteralPath (Join-Path $archiveSource 'Vivre.exe') -Value 'dummy'
    New-Item -ItemType Directory -Path (Join-Path $archiveSource 'Modules\Nested') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $archiveSource 'Scripts\Empty') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $archiveSource 'Modules\Nested\module.psm1') -Value 'dummy module'
    Write-VivrePublishOwner -Directory $archiveSource -TargetPath $owned
    Compress-Archive -Path (Join-Path $archiveSource '*') -DestinationPath $archivePath
    if (-not (Test-Path -LiteralPath $archivePath) -or (Get-Item -LiteralPath $archivePath).Length -eq 0) {
        throw 'Candidate ZIP naming is incompatible with Compress-Archive.'
    }

    Test-VivrePublishArchive -ArchivePath $archivePath -SourceDirectory $archiveSource
    Set-Content -LiteralPath (Join-Path $archiveSource 'Vivre.exe') -Value 'other'
    $threw = $false
    try { Test-VivrePublishArchive -ArchivePath $archivePath -SourceDirectory $archiveSource } catch { $threw = $true }
    if (-not $threw) { throw 'Same-length archive content mismatch was not detected.' }

    # Separators must normalize before duplicate detection, without bypassing content checks.
    Set-Content -LiteralPath (Join-Path $archiveSource 'Vivre.exe') -Value 'dummy'
    $archive = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $existing = @($archive.Entries | Where-Object { $_.FullName.Replace('\', '/') -eq 'Modules/Nested/module.psm1' })[0]
        $alias = if ($existing.FullName.Contains('\')) { 'Modules/Nested/module.psm1' } else { 'Modules\Nested\module.psm1' }
        $archive.CreateEntry($alias) | Out-Null
    }
    finally { $archive.Dispose() }
    $threw = $false
    try { Test-VivrePublishArchive -ArchivePath $archivePath -SourceDirectory $archiveSource }
    catch { if ($_.Exception.Message -notlike 'Archive has duplicate entry:*') { throw }; $threw = $true }
    if (-not $threw) { throw 'Separator-normalized duplicate was not detected.' }

    $archive = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $archive.GetEntry($alias).Delete()
        $badDirectory = $archive.CreateEntry('Scripts/Bad/')
        $stream = $badDirectory.Open()
        try { $stream.WriteByte(1) } finally { $stream.Dispose() }
    }
    finally { $archive.Dispose() }
    $threw = $false
    try { Test-VivrePublishArchive -ArchivePath $archivePath -SourceDirectory $archiveSource }
    catch { if ($_.Exception.Message -notlike 'Archive directory entry contains data:*') { throw }; $threw = $true }
    if (-not $threw) { throw 'Directory entry containing data was not rejected.' }

    $lease = Enter-VivrePublishLease -TargetPath $owned
    try {
        $threw = $false
        try { $secondLease = Enter-VivrePublishLease -TargetPath $owned } catch { $threw = $true }
        if (-not $threw) { $secondLease.Dispose(); throw 'Concurrent publication was not rejected.' }
    }
    finally { $lease.Dispose() }

    $secondLive = Join-Path (Join-Path $publishRoot 'missing-parent') 'artifact.txt'
    $secondCandidate = Join-Path $publishRoot '.second-candidate'
    Set-Content -LiteralPath $secondCandidate -Value 'candidate'
    $firstCandidate = Join-Path $publishRoot '.rollback-candidate'
    New-Item -ItemType Directory -Path $firstCandidate | Out-Null
    Set-Content -LiteralPath (Join-Path $firstCandidate 'new.txt') -Value 'new'
    $threw = $false
    try {
        Invoke-VivreArtifactPromotion -Artifacts @(
            [PSCustomObject]@{ Live = $owned; Candidate = $firstCandidate },
            [PSCustomObject]@{ Live = $secondLive; Candidate = $secondCandidate })
    }
    catch { $threw = $true }
    if (-not $threw) { throw 'Expected second-artifact promotion failure.' }
    if ((Get-Content -LiteralPath (Join-Path $owned 'last-good.txt') -Raw).Trim() -ne 'old') {
        throw 'Rollback did not restore the last-good artifact.'
    }

    $candidate = Join-Path $publishRoot '.candidate'
    New-Item -ItemType Directory -Path $candidate | Out-Null
    Write-VivrePublishOwner -Directory $candidate -TargetPath $owned
    Set-Content -LiteralPath (Join-Path $candidate 'current.txt') -Value 'new'
    Invoke-VivreArtifactPromotion -Artifacts @([PSCustomObject]@{ Live = $owned; Candidate = $candidate })
    if ((Get-Content -LiteralPath (Join-Path $owned 'current.txt') -Raw).Trim() -ne 'new') { throw 'Verified candidate was not promoted.' }
    if (Test-Path -LiteralPath (Join-Path $owned 'last-good.txt')) { throw 'Old artifact remained after promotion.' }

    Write-Host 'Publish safety tests passed.' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
