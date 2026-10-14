using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vivre.Core.Deploy;

internal sealed record PackageManifest(string[] Directories, ManifestFile[] Files);
internal sealed record ManifestFile(string Path, long Length, string Sha256);
internal sealed record PackagePublicationPlan(
    string Source, string Target, string Stage, string Backup, bool IsFolder, PackageManifest Manifest);

/// <summary>Builds and publishes a verified package without mutating its destination until verification succeeds.</summary>
internal static class PackagePublication
{
    internal static PackagePublicationPlan Prepare(string sourcePath, bool isFolder, string targetPath, bool localTarget, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(sourcePath) || !Path.IsPathFullyQualified(targetPath))
            throw new ArgumentException("Source and destination must be fully qualified paths.");
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath));
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath));
        if (isFolder ? !Directory.Exists(source) : !File.Exists(source))
            throw new IOException($"The selected {(isFolder ? "folder" : "file")} does not exist: {source}");
        if (isFolder && File.Exists(source) || !isFolder && Directory.Exists(source))
            throw new IOException("The selected source type does not match the package type.");
        if (IsRoot(target)) throw new IOException("The destination cannot be a drive or share root.");
        RejectReparseSource(source, isFolder, token);
        if (localTarget && isFolder && (IsSameOrChild(target, source) || IsSameOrChild(source, target)))
            throw new IOException("Source and destination folders cannot contain or replace one another.");
        if (localTarget && !isFolder && string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Source and destination file must be different.");
        string id = Guid.NewGuid().ToString("N");
        return new(source, target, target + $".vivre-staging-{id}", target + $".vivre-backup-{id}", isFolder, BuildManifest(source, isFolder, token));
    }

    internal static StageResult PublishLocal(PackagePublicationPlan plan, CancellationToken token, Action<string>? fault = null)
    {
        bool backupCreated = false;
        bool candidateOwned = false;
        StageResult Failed(string message)
        {
            try { if (candidateOwned) DeleteOwned(plan.Stage, plan.IsFolder); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            { message += $" Candidate cleanup failed at {plan.Stage}: {cleanupError.Message}."; }
            return new(false, null, message);
        }
        try
        {
            RejectReparseDestination(plan.Target, plan.IsFolder);
            if (plan.IsFolder ? File.Exists(plan.Target) : Directory.Exists(plan.Target))
                throw new IOException("The destination type does not match the selected package.");
            if (new[] { plan.Stage, plan.Backup, plan.Stage + ".extract" }.Any(p => File.Exists(p) || Directory.Exists(p))) throw new IOException("Unique candidate path unexpectedly already exists.");
            candidateOwned = true;
            Copy(plan.Source, plan.Stage, plan.IsFolder, token);
            Verify(plan.Stage, plan.IsFolder, plan.Manifest, token);
            fault?.Invoke("after-verify");
            token.ThrowIfCancellationRequested();
            if (Exists(plan.Target, plan.IsFolder))
            {
                Move(plan.Target, plan.Backup, plan.IsFolder);
                backupCreated = true;
            }
            bool hadPrevious = backupCreated;
            try
            {
                fault?.Invoke("before-publish");
                Move(plan.Stage, plan.Target, plan.IsFolder);
                candidateOwned = false;
            }
            catch (Exception publishError)
            {
                if (backupCreated)
                {
                    try
                    {
                        if (Exists(plan.Target, plan.IsFolder)) throw new IOException("another item appeared at the destination during rollback");
                        fault?.Invoke("before-rollback"); Move(plan.Backup, plan.Target, plan.IsFolder);
                        backupCreated = false;
                    }
                    catch (Exception rollbackError)
                    {
                        return Failed($"Replacement failed: {publishError.Message}. Rollback failed: {rollbackError.Message}. Previous package retained at {plan.Backup}");
                    }
                }
                return Failed($"Replacement failed: {publishError.Message}. " + (hadPrevious ? "The previous package was restored." : "The new package was not published."));
            }
            if (backupCreated)
            {
                try { DeleteOwned(plan.Backup, plan.IsFolder); }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
                {
                    return new(true, plan.Target, $"Staged and verified at {plan.Target}. Old-package cleanup is incomplete at {plan.Backup}: {cleanupError.Message}");
                }
            }
            return new(true, plan.Target, $"Staged and verified at {plan.Target}") { TemporaryPathsCleaned = true };
        }
        catch (OperationCanceledException)
        {
            try { if (candidateOwned) DeleteOwned(plan.Stage, plan.IsFolder); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            { throw new OperationCanceledException($"Staging was cancelled; candidate cleanup failed at {plan.Stage}: {cleanupError.Message}", cleanupError, token); }
            throw;
        }
        catch (Exception ex)
        {
            string cleanup = string.Empty;
            try { if (candidateOwned) DeleteOwned(plan.Stage, plan.IsFolder); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            { cleanup = $" Candidate cleanup also failed at {plan.Stage}: {cleanupError.Message}."; }
            string recovery = backupCreated && Exists(plan.Backup, plan.IsFolder) ? $" Previous package retained at {plan.Backup}." : string.Empty;
            return new(false, null, $"Couldn't stage locally: {ex.Message}.{cleanup}{recovery}");
        }
    }

    internal static string ManifestBase64(PackageManifest manifest) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest)));

    internal static string BuildPublishScript(
        string sourceExpression, bool sourceIsZip, string target, string stage, string backup, bool isFolder,
        string manifestBase64, string? expectedZipHash = null, bool cleanupSourceZip = true, string? manifestExpression = null)
    {
        string manifestRead = manifestExpression ?? $"[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{manifestBase64}'))";
        string zipCheck = expectedZipHash is null ? string.Empty :
            $"if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne '{expectedZipHash}') {{ throw 'transferred ZIP SHA-256 mismatch' }}";
        return Functions + $$"""

        $ErrorActionPreference = 'Stop'
        $source = {{sourceExpression}}
        $target = {{Quote(target)}}
        $stage = {{Quote(stage)}}
        $backup = {{Quote(backup)}}
        $isFolder = ${{isFolder.ToString().ToLowerInvariant()}}
        $sourceIsZip = ${{sourceIsZip.ToString().ToLowerInvariant()}}
        $manifest = {{manifestRead}} | ConvertFrom-Json
        try {
            {{zipCheck}}
            $result = Invoke-VivrePublish $source $sourceIsZip $target $stage $backup $isFolder $manifest
        } catch {
            $result = [PSCustomObject]@{ ok=$false; safeToFallback=$false; message="Stage failed: $($_.Exception.Message)"; recoveryPath=$null }
        }
        if ($sourceIsZip -and ${{cleanupSourceZip.ToString().ToLowerInvariant()}}) {
            try { Remove-Item -LiteralPath $source -Force -ErrorAction Stop }
            catch { $result.message += " Transfer ZIP cleanup failed at $source`: $($_.Exception.Message)" }
        }
        $result
        """;
    }

    internal static string BuildSmbScript(PackagePublicationPlan plan)
    {
        string body = BuildPublishScript("$Source", false, plan.Target, plan.Stage, plan.Backup, plan.IsFolder, string.Empty, manifestExpression: "$ManifestJson");
        return "param([string]$Source,[Management.Automation.PSCredential]$Cred,[string]$ShareRoot,[string]$ManifestJson)\n" +
            "$mapped=$false; try { if($Cred -and $Cred -ne [Management.Automation.PSCredential]::Empty){New-PSDrive -Name VivreStage -PSProvider FileSystem -Root $ShareRoot -Credential $Cred -ErrorAction Stop|Out-Null;$mapped=$true}\n" +
            body + "\n} catch {[PSCustomObject]@{ok=$false;safeToFallback=$true;message=\"SMB failed before publication: $($_.Exception.Message)\"}} finally {if($mapped){Remove-PSDrive VivreStage -Force -ErrorAction SilentlyContinue}}";
    }

    private static PackageManifest BuildManifest(string path, bool folder, CancellationToken token)
    {
        if (!folder)
        {
            var info = new FileInfo(path);
            return new([], [new(string.Empty, info.Length, Hash(path, token))]);
        }
        string[] dirs = Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
            .Select(p => { token.ThrowIfCancellationRequested(); return Relative(path, p); }).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        ManifestFile[] files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Select(p => { token.ThrowIfCancellationRequested(); var i = new FileInfo(p); return new ManifestFile(Relative(path, p), i.Length, Hash(p, token)); })
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(dirs, files);
    }

    private static void Verify(string path, bool folder, PackageManifest expected, CancellationToken token)
    {
        PackageManifest actual = BuildManifest(path, folder, token);
        token.ThrowIfCancellationRequested();
        bool equal = expected.Directories.SequenceEqual(actual.Directories, StringComparer.OrdinalIgnoreCase) &&
            expected.Files.Length == actual.Files.Length && expected.Files.Zip(actual.Files).All(x =>
                string.Equals(x.First.Path, x.Second.Path, StringComparison.OrdinalIgnoreCase) &&
                x.First.Length == x.Second.Length && string.Equals(x.First.Sha256, x.Second.Sha256, StringComparison.OrdinalIgnoreCase));
        if (!equal) throw new IOException("candidate does not match the source manifest (paths, lengths, hashes, or empty folders differ)");
    }

    private static string Hash(string path, CancellationToken token)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return HashStream(stream, token);
    }

    internal static string HashStream(Stream stream, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int count = stream.Read(buffer, 0, buffer.Length);
                if (count == 0) break;
                hash.AppendData(buffer, 0, count);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void Copy(string source, string stage, bool folder, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(stage)!);
        if (!folder) { token.ThrowIfCancellationRequested(); File.Copy(source, stage, false); return; }
        Directory.CreateDirectory(stage);
        foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        { token.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.Combine(stage, Path.GetRelativePath(source, dir))); }
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            string destination = Path.Combine(stage, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination, false);
        }
    }

    private static void RejectReparseSource(string source, bool folder, CancellationToken token)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse-point sources are not supported.");
        if (folder && Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories).Any(p => { token.ThrowIfCancellationRequested(); return (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0; }))
            throw new IOException("The source contains a reparse point.");
    }

    private static void RejectReparseDestination(string target, bool folder)
    {
        for (string? current = target; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"The destination traverses a reparse point: {current}");
            string? parent = Path.GetDirectoryName(current); if (parent == current) break;
        }
        if (folder && Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target, "*", SearchOption.AllDirectories).Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0))
            throw new IOException("The existing destination contains a reparse point.");
    }

    private static bool IsSameOrChild(string candidate, string parent)
    { string r = Path.GetRelativePath(parent, candidate); return r == "." || (!Path.IsPathRooted(r) && r != ".." && !r.StartsWith(".." + Path.DirectorySeparatorChar)); }
    private static bool IsRoot(string path) => string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path)!), StringComparison.OrdinalIgnoreCase);
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static bool Exists(string path, bool folder) => folder ? Directory.Exists(path) : File.Exists(path);
    private static void Move(string from, string to, bool folder) { if (folder) Directory.Move(from, to); else File.Move(from, to, false); }
    private static void DeleteOwned(string path, bool folder) { RejectReparseDestination(path, folder); if (folder) { if (Directory.Exists(path)) Directory.Delete(path, true); } else if (File.Exists(path)) File.Delete(path); }
    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    private const string Functions = """
        function Assert-VivreNoReparse($target, $folder) {
            $p = $target
            while ($p) {
                if (Test-Path -LiteralPath $p) {
                    $item = Get-Item -LiteralPath $p -Force
                    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                        throw "Path traverses a reparse point: $p"
                    }
                }
                $next = [IO.Path]::GetDirectoryName($p)
                if ($next -eq $p) { break }
                $p = $next
            }
            if ($folder -and (Test-Path -LiteralPath $target -PathType Container)) {
                $pending = New-Object 'System.Collections.Generic.Stack[string]'
                $pending.Push($target)
                while ($pending.Count -gt 0) {
                    foreach ($entry in @(Get-ChildItem -LiteralPath $pending.Pop() -Force)) {
                        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                            throw "Package contains a reparse point: $($entry.FullName)"
                        }
                        if ($entry.PSIsContainer) { $pending.Push($entry.FullName) }
                    }
                }
            }
        }

        function Remove-VivreOwned($path, $folder) {
            if (Test-Path -LiteralPath $path) {
                Assert-VivreNoReparse $path $folder
                if ($folder) { Remove-Item -LiteralPath $path -Recurse -Force }
                else { Remove-Item -LiteralPath $path -Force }
            }
        }

        function Move-VivreExact($from, $to, $folder) {
            if ($folder) { [IO.Directory]::Move($from, $to) }
            else { [IO.File]::Move($from, $to) }
        }

        function Assert-VivreManifest($path, $folder, $manifest) {
            Assert-VivreNoReparse $path $folder
            if ($folder) {
                $directories = @(Get-ChildItem -LiteralPath $path -Directory -Recurse -Force |
                    ForEach-Object { $_.FullName.Substring($path.Length).TrimStart('\').Replace('\','/') } | Sort-Object)
                $files = @(Get-ChildItem -LiteralPath $path -File -Recurse -Force | ForEach-Object {
                    [PSCustomObject]@{
                        Path = $_.FullName.Substring($path.Length).TrimStart('\').Replace('\','/')
                        Length = $_.Length
                        Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                    }
                } | Sort-Object Path)
            } else {
                $item = Get-Item -LiteralPath $path -Force
                $directories = @()
                $files = @([PSCustomObject]@{ Path=''; Length=$item.Length; Sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash })
            }
            $expectedDirectories = @($manifest.Directories | Sort-Object)
            $expectedFiles = @($manifest.Files | Sort-Object Path)
            if ($directories.Count -ne $expectedDirectories.Count -or $files.Count -ne $expectedFiles.Count) {
                throw 'Manifest entry count mismatch'
            }
            for ($i = 0; $i -lt $directories.Count; $i++) {
                if ($directories[$i] -ine $expectedDirectories[$i]) { throw 'Manifest directory mismatch' }
            }
            for ($i = 0; $i -lt $files.Count; $i++) {
                if ($files[$i].Path -ine $expectedFiles[$i].Path -or
                    $files[$i].Length -ne $expectedFiles[$i].Length -or
                    $files[$i].Sha256 -ine $expectedFiles[$i].Sha256) {
                    throw "Manifest mismatch: $($expectedFiles[$i].Path)"
                }
            }
        }

        function Invoke-VivrePublish($source, $zip, $target, $stage, $backup, $folder, $manifest) {
            $phase = 'copy'
            $madeBackup = $false
            $published = $false
            $ownsStage = $false
            $ownsExtract = $false
            $extract = $stage + '.extract'
            $result = $null
            try {
                Assert-VivreNoReparse $target $folder
                if (Test-Path -LiteralPath $target) {
                    $targetIsFolder = (Get-Item -LiteralPath $target -Force).PSIsContainer
                    if ($targetIsFolder -ne $folder) { throw 'Destination type does not match the package' }
                }
                if ((Test-Path -LiteralPath $stage) -or (Test-Path -LiteralPath $backup) -or (Test-Path -LiteralPath $extract)) {
                    throw 'A unique staging/recovery path already exists; it was not modified'
                }
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($stage)) | Out-Null
                $ownsStage = $true
                if ($zip) {
                    if ($folder) {
                        [IO.Directory]::CreateDirectory($stage) | Out-Null
                        Expand-Archive -LiteralPath $source -DestinationPath $stage -Force
                    } else {
                        $ownsExtract = $true
                        [IO.Directory]::CreateDirectory($extract) | Out-Null
                        Expand-Archive -LiteralPath $source -DestinationPath $extract -Force
                        Assert-VivreNoReparse $extract $true
                        $entries = @(Get-ChildItem -LiteralPath $extract -Recurse -Force)
                        if ($entries.Count -ne 1 -or $entries[0].PSIsContainer) {
                            throw 'Single-file archive must contain exactly one root file'
                        }
                        [IO.File]::Move($entries[0].FullName, $stage)
                    }
                } else {
                    Assert-VivreNoReparse $source $folder
                    if ($folder) { Copy-Item -LiteralPath $source -Destination $stage -Recurse -Force }
                    else { Copy-Item -LiteralPath $source -Destination $stage -Force }
                }
                Assert-VivreManifest $stage $folder $manifest
                Assert-VivreNoReparse $target $folder
                $phase = 'publish'
                if (Test-Path -LiteralPath $target) {
                    Move-VivreExact $target $backup $folder
                    $madeBackup = $true
                }
                Move-VivreExact $stage $target $folder
                $published = $true
                $ownsStage = $false
                $result = [PSCustomObject]@{ ok=$true; safeToFallback=$false; path=$target; message="Staged and verified at $target"; recoveryPath=$null }
                if ($madeBackup) {
                    try { Remove-VivreOwned $backup $folder; $madeBackup = $false }
                    catch {
                        $result.message += " Old-package cleanup incomplete at $backup`: $($_.Exception.Message)"
                        $result.recoveryPath = $backup
                    }
                }
            } catch {
                $detail = "Stage $phase failed: $($_.Exception.Message)"
                if ($madeBackup -and -not $published) {
                    try {
                        if (Test-Path -LiteralPath $target) { throw 'Another item appeared at the destination; it was not modified' }
                        Move-VivreExact $backup $target $folder
                        $madeBackup = $false
                        $detail += '. Previous package restored.'
                    } catch {
                        $detail += ". Rollback failed: $($_.Exception.Message). Previous package retained at $backup."
                    }
                }
                $result = [PSCustomObject]@{
                    ok=$false; safeToFallback=($phase -eq 'copy'); message=$detail
                    recoveryPath=$(if ($madeBackup) { $backup } else { $null })
                }
            } finally {
                if ($ownsStage) {
                    try { Remove-VivreOwned $stage $folder }
                    catch {
                        $result.safeToFallback = $false
                        $result.message += " Candidate cleanup failed at $stage`: $($_.Exception.Message)"
                    }
                }
                if ($ownsExtract) {
                    try { Remove-VivreOwned $extract $true; $ownsExtract = $false }
                    catch {
                        $result.safeToFallback = $false
                        $result.message += " Extraction cleanup failed at $extract`: $($_.Exception.Message)"
                    }
                }
            }
            $result | Add-Member -NotePropertyName temporaryPathsCleaned -NotePropertyValue ($result.ok -and -not $ownsStage -and -not $ownsExtract -and -not $madeBackup) -Force
            $result
        }
        """;
}
