#requires -Version 5.1

$script:VivrePublishOwnerFile = '.vivre-publish-owner.json'

function Get-VivreCanonicalPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($full)
    if ($full.Length -gt $root.Length) { $full = $full.TrimEnd('\', '/') }
    return $full
}

function Test-VivrePathEqual {
    param([string]$Left, [string]$Right)
    return [string]::Equals((Get-VivreCanonicalPath $Left), (Get-VivreCanonicalPath $Right), [StringComparison]::OrdinalIgnoreCase)
}

function Test-VivrePathInside {
    param([string]$Path, [string]$Parent)
    $candidate = (Get-VivreCanonicalPath $Path) + [IO.Path]::DirectorySeparatorChar
    $container = (Get-VivreCanonicalPath $Parent) + [IO.Path]::DirectorySeparatorChar
    return $candidate.StartsWith($container, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-VivreArtifactTree {
    param([Parameter(Mandatory = $true)][string]$Path)
    # Never recurse through a junction while deciding whether recursive cleanup is safe.
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push((Get-VivreCanonicalPath $Path))
    while ($pending.Count -gt 0) {
        $item = Get-Item -LiteralPath $pending.Pop() -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing artifact containing a reparse point: $($item.FullName)"
        }
        if ($item.PSIsContainer) {
            foreach ($child in @(Get-ChildItem -LiteralPath $item.FullName -Force -ErrorAction Stop)) {
                if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Refusing artifact containing a reparse point: $($child.FullName)"
                }
                if ($child.PSIsContainer) { $pending.Push($child.FullName) }
            }
        }
    }
}

function Assert-VivrePublishTarget {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$RepositoryRoot)

    $target = Get-VivreCanonicalPath $Path
    $repo = Get-VivreCanonicalPath $RepositoryRoot
    $root = [IO.Path]::GetPathRoot($target)
    if (Test-VivrePathEqual $target $root) { throw "Refusing drive/root publish target: $target" }
    if ((Test-VivrePathEqual $target $repo) -or (Test-VivrePathInside $repo $target)) {
        throw "Refusing publish target that is the repository or contains it: $target"
    }
    $source = Join-Path $repo 'source'
    $ownedRoot = Join-Path $repo 'publish'
    if ((Test-VivrePathEqual $target $source) -or (Test-VivrePathInside $target $source)) {
        throw "Refusing publish target in the source tree: $target"
    }
    if ((Test-VivrePathInside $target $repo) -and -not (Test-VivrePathEqual $target $ownedRoot) -and -not (Test-VivrePathInside $target $ownedRoot)) {
        throw "Repository-local publish targets must be inside the publish directory: $target"
    }

    $probe = $target
    while ($probe) {
        if (Test-Path -LiteralPath $probe) {
            $item = Get-Item -LiteralPath $probe -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing publish target through a reparse point: $($item.FullName)"
            }
        }
        $parentInfo = [IO.Directory]::GetParent($probe)
        $parent = if ($parentInfo) { $parentInfo.FullName } else { $null }
        if (-not $parent -or (Test-VivrePathEqual $parent $probe)) { break }
        $probe = $parent
    }

    if (Test-Path -LiteralPath $target) {
        $targetItem = Get-Item -LiteralPath $target -Force
        if (-not $targetItem.PSIsContainer) { throw "Publish target is not a directory: $target" }
        Assert-VivreArtifactTree -Path $target
        $children = @(Get-ChildItem -LiteralPath $target -Force)
        if ($children.Count -gt 0) {
            $ownerPath = Join-Path $target $script:VivrePublishOwnerFile
            if (-not (Test-Path -LiteralPath $ownerPath -PathType Leaf)) {
                throw "Refusing nonempty publish target without Vivre ownership marker: $target"
            }
            try { $owner = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json }
            catch { throw "Refusing publish target with unreadable ownership marker: $target ($($_.Exception.Message))" }
            if ($owner.kind -ne 'VivrePublishOutput' -or -not (Test-VivrePathEqual ([string]$owner.targetPath) $target)) {
                throw "Refusing publish target with mismatched ownership marker: $target"
            }
        }
    }
    return $target
}

function Enter-VivrePublishLease {
    param([Parameter(Mandatory = $true)][string]$TargetPath)
    $target = Get-VivreCanonicalPath $TargetPath
    $parent = [IO.Directory]::GetParent($target).FullName
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    $leasePath = Join-Path $parent ('.' + [IO.Path]::GetFileName($target) + '.publish.lock')
    try {
        return [IO.File]::Open($leasePath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    }
    catch [IO.IOException] {
        throw "Another publish already owns this output target: $target"
    }
}

function Write-VivrePublishOwner {
    param([Parameter(Mandatory = $true)][string]$Directory, [Parameter(Mandatory = $true)][string]$TargetPath)
    $payload = [ordered]@{ kind = 'VivrePublishOutput'; targetPath = (Get-VivreCanonicalPath $TargetPath); createdUtc = [DateTime]::UtcNow.ToString('o') } | ConvertTo-Json
    Set-Content -LiteralPath (Join-Path $Directory $script:VivrePublishOwnerFile) -Value $payload -Encoding UTF8
}

function Invoke-VivreArtifactPromotion {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][object[]]$Artifacts)

    $transaction = [Guid]::NewGuid().ToString('N')
    $records = @()
    foreach ($artifact in $Artifacts) {
        $live = Get-VivreCanonicalPath ([string]$artifact.Live)
        $candidate = Get-VivreCanonicalPath ([string]$artifact.Candidate)
        if (-not (Test-Path -LiteralPath $candidate)) { throw "Candidate artifact is missing: $candidate" }
        Assert-VivreArtifactTree -Path $candidate
        if (Test-Path -LiteralPath $live) { Assert-VivreArtifactTree -Path $live }
        $records += [PSCustomObject]@{ Live = $live; Candidate = $candidate; Backup = "$live.last-good-$transaction"; HadLive = (Test-Path -LiteralPath $live); Promoted = $false }
    }

    try {
        foreach ($record in $records) { if ($record.HadLive) { Move-Item -LiteralPath $record.Live -Destination $record.Backup } }
        foreach ($record in $records) { Move-Item -LiteralPath $record.Candidate -Destination $record.Live; $record.Promoted = $true }
    }
    catch {
        $failure = $_
        $rollback = @($records)
        [array]::Reverse($rollback)
        $recoveryErrors = @()
        foreach ($record in $rollback) {
            try {
                if ($record.Promoted -and (Test-Path -LiteralPath $record.Live)) { Assert-VivreArtifactTree -Path $record.Live; Remove-Item -LiteralPath $record.Live -Recurse -Force }
                if ($record.HadLive -and (Test-Path -LiteralPath $record.Backup)) { Move-Item -LiteralPath $record.Backup -Destination $record.Live }
            }
            catch { $recoveryErrors += "Recovery incomplete for '$($record.Live)'; preserve '$($record.Backup)': $($_.Exception.Message)" }
        }
        if ($recoveryErrors.Count -gt 0) { throw ($failure.Exception.Message + ' ' + ($recoveryErrors -join '; ')) }
        throw $failure
    }
    foreach ($record in $records) {
        if ($record.HadLive -and (Test-Path -LiteralPath $record.Backup)) {
            try { Assert-VivreArtifactTree -Path $record.Backup; Remove-Item -LiteralPath $record.Backup -Recurse -Force }
            catch { Write-Warning "New artifact is published; prior artifact retained at '$($record.Backup)': $($_.Exception.Message)" }
        }
    }
}

function Test-VivrePublishArchive {
    param([Parameter(Mandatory = $true)][string]$ArchivePath, [Parameter(Mandatory = $true)][string]$SourceDirectory)
    Assert-VivreArtifactTree -Path $SourceDirectory
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    $hasher = [Security.Cryptography.SHA256]::Create()
    try {
        $entries = @{}
        foreach ($entry in $archive.Entries) {
            # Windows PowerShell 5.1 Compress-Archive emits backslashes, including directory entries.
            $entryPath = $entry.FullName.Replace('\', '/')
            if ($entryPath.EndsWith('/')) {
                if ($entry.Length -ne 0) { throw "Archive directory entry contains data: $($entry.FullName)" }
                continue
            }
            if ($entries.ContainsKey($entryPath)) { throw "Archive has duplicate entry: $entryPath" }
            $entries[$entryPath] = $entry
        }
        $sourceRoot = (Get-VivreCanonicalPath $SourceDirectory) + [IO.Path]::DirectorySeparatorChar
        $sourceFiles = @(Get-ChildItem -LiteralPath $SourceDirectory -Recurse -File -Force)
        if ($entries.Count -ne $sourceFiles.Count) { throw "Archive file count differs from the verified publish folder (archive: $($entries.Count), folder: $($sourceFiles.Count))." }
        foreach ($file in $sourceFiles) {
            $relative = $file.FullName.Substring($sourceRoot.Length).Replace('\', '/')
            if (-not $entries.ContainsKey($relative)) { throw "Archive is missing published file: $relative" }
            $entry = $entries[$relative]
            if ($entry.Length -ne $file.Length) { throw "Archive length mismatch: $relative" }
            $disk = [IO.File]::OpenRead($file.FullName)
            try { $expected = [Convert]::ToBase64String($hasher.ComputeHash($disk)) }
            finally { $disk.Dispose() }
            $compressed = $entry.Open()
            try { $actual = [Convert]::ToBase64String($hasher.ComputeHash($compressed)) }
            finally { $compressed.Dispose() }
            if ($actual -ne $expected) { throw "Archive content mismatch: $relative" }
        }
    }
    finally { $hasher.Dispose(); $archive.Dispose() }
}
