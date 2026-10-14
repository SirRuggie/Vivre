using System.Text;

namespace Vivre.Core.Deploy;

/// <summary>Builds PowerShell 5.1 scripts for an ownership-scoped remote ZIP transfer.</summary>
internal static class StagingTransferScripts
{
    private const int MaxChunkBytes = 1024 * 1024;

    internal static string BuildInitialize(string directory, string id, string ownerToken) =>
        Header("initialize", directory, id, ownerToken) + """
        try {
            Assert-VivreTransferPath $directory $id $false
            if (Test-Path -LiteralPath $directory) { throw 'Transfer directory already exists; it was not modified' }
            $temporary = $directory + '.init-' + $ownerToken
            $tombstone = $directory + '.cleanup-' + $ownerToken
            if ((Test-Path -LiteralPath $temporary) -or (Test-Path -LiteralPath $tombstone)) {
                throw 'Transfer lifecycle path already exists; it was not modified'
            }
            $ownsTemporary = $false
            try {
                [IO.Directory]::CreateDirectory($temporary) | Out-Null
                $ownsTemporary = $true
                $ownerPath = [IO.Path]::Combine($temporary, 'owner.json')
                $ownerJson = [PSCustomObject]@{ id=$id; ownerToken=$ownerToken } | ConvertTo-Json -Compress
                $ownerBytes = [Text.Encoding]::UTF8.GetBytes($ownerJson)
                $ownerStream = New-Object IO.FileStream($ownerPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try { $ownerStream.Write($ownerBytes, 0, $ownerBytes.Length); $ownerStream.Flush($true) }
                finally { $ownerStream.Dispose() }
                $lockPath = [IO.Path]::Combine($temporary, 'transfer.lock')
                $lockStream = New-Object IO.FileStream($lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                $lockStream.Dispose()
                [IO.Directory]::Move($temporary, $directory)
                $ownsTemporary = $false
            } finally {
                if ($ownsTemporary) {
                    foreach ($name in @('owner.json','transfer.lock')) {
                        $owned = [IO.Path]::Combine($temporary, $name)
                        if (Test-Path -LiteralPath $owned -PathType Leaf) { [IO.File]::Delete($owned) }
                    }
                    if (Test-Path -LiteralPath $temporary -PathType Container) { [IO.Directory]::Delete($temporary, $false) }
                }
            }
            [PSCustomObject]@{ ok=$true; message='Transfer directory initialized' }
        } catch {
            [PSCustomObject]@{ ok=$false; message="Transfer initialization failed: $($_.Exception.Message)" }
        }
        """;

    internal static string BuildChunk(string directory, string id, string ownerToken, string base64, long offset, string fileName = "payload.zip")
    {
        if (fileName is not ("payload.zip" or "manifest.json")) throw new ArgumentException("Unsupported transfer file.", nameof(fileName));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException ex) { throw new ArgumentException("Chunk must be valid base64.", nameof(base64), ex); }
        if (bytes.Length > MaxChunkBytes) throw new ArgumentException($"Chunk exceeds {MaxChunkBytes} bytes.", nameof(base64));
        return Header("chunk", directory, id, ownerToken) + $$"""
        try {
            Assert-VivreTransferPath $directory $id $true
            $lock = Open-VivreTransferLock $directory
            try {
                Assert-VivreTransferOwner $directory $id $ownerToken
                $bytes = [Convert]::FromBase64String('{{base64}}')
                if ($bytes.Length -gt {{MaxChunkBytes}}) { throw 'Transfer chunk exceeds the size limit' }
                $payload = [IO.Path]::Combine($directory, '{{fileName}}')
                $expectedOffset = [Int64]{{offset}}
                if ($expectedOffset -eq 0) {
                    $stream = New-Object IO.FileStream($payload, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                } else {
                    Assert-VivreRegularChild $directory '{{fileName}}' $true
                    $stream = New-Object IO.FileStream($payload, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::None)
                }
                try {
                    if ($stream.Length -ne $expectedOffset) { throw "Transfer offset mismatch: expected $expectedOffset, actual $($stream.Length)" }
                    $stream.Position = $stream.Length
                    $stream.Write($bytes, 0, $bytes.Length)
                    $stream.Flush($true)
                } finally { $stream.Dispose() }
                [PSCustomObject]@{ ok=$true; message="Transfer chunk accepted at offset $expectedOffset" }
            } finally { $lock.Dispose() }
        } catch {
            [PSCustomObject]@{ ok=$false; message="Transfer chunk failed: $($_.Exception.Message)" }
        }
        """;
    }

    internal static string BuildCleanup(string directory, string id, string ownerToken) =>
        Header("cleanup", directory, id, ownerToken) + """
        $lock = $null
        try {
            Assert-VivreTransferPath $directory $id $false
            $initial = $directory + '.init-' + $ownerToken
            $tombstone = $directory + '.cleanup-' + $ownerToken
            $present = @(@($directory, $initial, $tombstone) | Where-Object { Test-Path -LiteralPath $_ })
            if ($present.Count -eq 0) {
                [PSCustomObject]@{ ok=$true; cleanupPending=$false; absent=$true; message='Transfer directory, initializer, and cleanup tombstone are already absent' }
                return
            }
            if ($present.Count -ne 1) { throw "Multiple transfer lifecycle paths exist; automatic cleanup refused: $($present -join ', ')" }
            $known = @('owner.json','payload.zip','manifest.json','transfer.lock')
            if ($present[0] -ne $tombstone) {
                $ownedPath = $present[0]
                if ($ownedPath -eq $directory) { Assert-VivreTransferPath $directory $id $true }
                else { Assert-VivreLifecycleSibling $directory $ownedPath ('.init-' + $ownerToken) }
                foreach ($entry in @(Get-ChildItem -LiteralPath $ownedPath -Force)) {
                    if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $entry.PSIsContainer -or $known -notcontains $entry.Name) {
                        throw "Transfer lifecycle directory contains an unknown or unsafe entry: $($entry.Name)"
                    }
                }
                $lock = Open-VivreTransferLock $ownedPath
                Assert-VivreTransferOwner $ownedPath $id $ownerToken
                $lock.Dispose(); $lock = $null
                [IO.Directory]::Move($ownedPath, $tombstone)
            }
            Assert-VivreLifecycleSibling $directory $tombstone ('.cleanup-' + $ownerToken)
            foreach ($entry in @(Get-ChildItem -LiteralPath $tombstone -Force)) {
                if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $entry.PSIsContainer -or $known -notcontains $entry.Name) {
                    throw "Cleanup tombstone contains an unknown or unsafe entry: $($entry.Name)"
                }
            }
            foreach ($name in @('payload.zip','manifest.json','owner.json','transfer.lock')) {
                $owned = [IO.Path]::Combine($tombstone, $name)
                if (Test-Path -LiteralPath $owned) {
                    Assert-VivreRegularChild $tombstone $name $true
                    [IO.File]::Delete($owned)
                }
            }
            [IO.Directory]::Delete($tombstone, $false)
            [PSCustomObject]@{ ok=$true; cleanupPending=$false; absent=$false; message='Transfer directory cleaned' }
        } catch {
            [PSCustomObject]@{ ok=$false; cleanupPending=$true; absent=$false; message="Transfer cleanup is pending: $($_.Exception.Message)" }
        } finally {
            if ($null -ne $lock) { $lock.Dispose() }
        }
        """;

    internal static string WrapPublication(string directory, string id, string ownerToken, string publicationScript, string? manifestSha256 = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicationScript);
        if (manifestSha256 is not null && (manifestSha256.Length != 64 || !manifestSha256.All(Uri.IsHexDigit)))
            throw new ArgumentException("Invalid manifest SHA256.", nameof(manifestSha256));
        string manifestCheck = manifestSha256 is null ? string.Empty :
            "Assert-VivreRegularChild $directory 'manifest.json' $true\n" +
            "$manifestPath = [IO.Path]::Combine($directory, 'manifest.json')\n" +
            $"if ((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -ne '{manifestSha256}') {{ throw 'transferred manifest SHA-256 mismatch' }}\n";
        return Header("publication", directory, id, ownerToken) + """
        try {
            Assert-VivreTransferPath $directory $id $true
            $lock = Open-VivreTransferLock $directory
            try {
                Assert-VivreTransferOwner $directory $id $ownerToken
                $payload = [IO.Path]::Combine($directory, 'payload.zip')
                Assert-VivreRegularChild $directory 'payload.zip' $true
        """ + "\n" + manifestCheck + """
                $publicationResult = & {
        """ + publicationScript + """
                }
                if ($null -eq $publicationResult) { throw 'Publication returned no verdict' }
                @($publicationResult)[-1]
            } finally { $lock.Dispose() }
        } catch {
            [PSCustomObject]@{ ok=$false; safeToFallback=$false; message="Transfer publication failed: $($_.Exception.Message)"; recoveryPath=$null }
        }
        """;
    }

    private static string Header(string operation, string directory, string id, string ownerToken)
    {
        id = ValidateGuid(id, nameof(id));
        ownerToken = ValidateGuid(ownerToken, nameof(ownerToken));
        string full = ValidateDirectory(directory, id);
        return $$"""
        # Vivre transfer {{operation}}
        $ErrorActionPreference = 'Stop'
        $directory = {{Quote(full)}}
        $id = '{{id}}'
        $ownerToken = '{{ownerToken}}'

        function Assert-VivreTransferPath($path, $expectedId, $mustExist) {
            if ([string]::IsNullOrWhiteSpace($path) -or [IO.Path]::GetFullPath($path) -ne $path) { throw 'Transfer path must be fully qualified and normalized' }
            if ([IO.Path]::GetFileName($path) -ine ('.vivre-transfer-' + $expectedId)) { throw 'Transfer path does not match its id' }
            $root = [IO.Path]::GetPathRoot($path).TrimEnd('\')
            if ($path.TrimEnd('\') -ieq $root) { throw 'Transfer path cannot be a root' }
            $current = $path
            while ($current) {
                if (Test-Path -LiteralPath $current) {
                    $item = Get-Item -LiteralPath $current -Force
                    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Transfer path traverses a reparse point: $current" }
                }
                $next = [IO.Path]::GetDirectoryName($current)
                if ($next -eq $current) { break }
                $current = $next
            }
            if ($mustExist -and -not (Test-Path -LiteralPath $path -PathType Container)) { throw 'Transfer directory is missing' }
        }

        function Open-VivreTransferLock($path) {
            Assert-VivreRegularChild $path 'transfer.lock' $true
            $lockPath = [IO.Path]::Combine($path, 'transfer.lock')
            New-Object IO.FileStream($lockPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        }

        function Assert-VivreTransferOwner($path, $expectedId, $expectedToken) {
            $ownerPath = [IO.Path]::Combine($path, 'owner.json')
            Assert-VivreRegularChild $path 'owner.json' $true
            $owner = [IO.File]::ReadAllText($ownerPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
            if ($owner.id -cne $expectedId -or $owner.ownerToken -cne $expectedToken) { throw 'Transfer ownership marker does not match' }
        }

        function Assert-VivreRegularChild($path, $name, $required) {
            $child = [IO.Path]::Combine($path, $name)
            if (-not (Test-Path -LiteralPath $child)) {
                if ($required) { throw "Transfer file is missing: $name" }
                return
            }
            $item = Get-Item -LiteralPath $child -Force
            if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                [IO.Path]::GetDirectoryName($item.FullName) -ine $path -or $item.Name -cne $name) {
                throw "Transfer file is not an owned regular file: $name"
            }
        }

        function Assert-VivreLifecycleSibling($livePath, $candidate, $suffix) {
            $expected = $livePath + $suffix
            if ($candidate -cne $expected -or [IO.Path]::GetDirectoryName($candidate) -ine [IO.Path]::GetDirectoryName($livePath)) {
                throw 'Transfer lifecycle path does not match its ownership token'
            }
            if (-not (Test-Path -LiteralPath $candidate -PathType Container)) { throw 'Transfer lifecycle directory is missing' }
            $item = Get-Item -LiteralPath $candidate -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Transfer lifecycle directory is a reparse point' }
        }

        """;
    }

    private static string ValidateDirectory(string directory, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (directory.IndexOfAny(['*', '?']) >= 0) throw new ArgumentException("Transfer path cannot contain wildcards.", nameof(directory));
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Transfer path must be fully qualified.", nameof(directory));
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Transfer path cannot be a root.", nameof(directory));
        if (!string.Equals(Path.GetFileName(full), $".vivre-transfer-{id}", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Transfer path leaf must match the transfer id.", nameof(directory));
        return full;
    }

    private static string ValidateGuid(string value, string parameterName)
    {
        if (value is null || value.Length != 32 || !Guid.TryParseExact(value, "N", out Guid parsed))
            throw new ArgumentException("Transfer ids must be 32-character GUIDs in N format.", parameterName);
        return parsed.ToString("N");
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
}
