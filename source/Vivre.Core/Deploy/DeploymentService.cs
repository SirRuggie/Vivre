using System.IO;
using System.IO.Compression;
using System.Management.Automation;
using System.Security.Cryptography;
using Vivre.Core.PowerShell;

namespace Vivre.Core.Deploy;

/// <inheritdoc cref="IDeploymentService"/>
/// <remarks>
/// <para><b>SMB first.</b> The fast path copies the source straight to the target's admin share
/// (<c>\\host\C$\…</c>) via a short local orchestration script (so it runs from this machine, reaching
/// out over SMB). The credential is passed as a bound parameter — never interpolated into the script
/// text. This is how Windows deployment tools move files: LAN speed, no encoding, one copy, no zip.</para>
///
/// <para><b>WinRM fallback.</b> If SMB fails (admin$ disabled, port 445 blocked), the payload is zipped
/// and shipped over WinRM in chunks — a multi-MB installer base64'd inline is one oversized command that
/// WinRM rejects ("the remote session ended"), so the bytes go as a sequence of small commands that
/// append to a temp zip. A finalize command then SHA-256-verifies the assembled zip against what we
/// shipped (so a dropped/garbled chunk can't pass silently) and expands it. Nothing is executed.</para>
///
/// <para><b>Local.</b> A target that resolves local is a direct file-system copy — no SMB, no WinRM.</para>
/// </remarks>
public sealed class DeploymentService : IDeploymentService
{
    /// <summary>Base64 characters per chunk command (~2 MiB of script text) on the WinRM fallback path.
    /// Comfortably under WinRM's per-command limits with ~10× margin below the size that fails, while
    /// keeping the round-trip count low. Multiple of 4 so each chunk decodes to a whole number of bytes.</summary>
    private const int Base64ChunkLength = 2_097_152;

    private readonly IPowerShellHost _powerShell;

    /// <param name="powerShell">The remoting host all transport goes through.</param>
    public DeploymentService(IPowerShellHost powerShell) => _powerShell = powerShell;

    public async Task<StageResult> StageAsync(
        string host,
        string sourcePath,
        bool sourceIsFolder,
        string targetPath,
        PSCredential? credential,
        CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        token.ThrowIfCancellationRequested();

        // Local target: just copy on disk — no remoting at all.
        if (HostName.IsLocal(host))
        {
            return CopyLocal(sourcePath, sourceIsFolder, targetPath);
        }

        // 1) SMB admin share — the fast path. A throw here (e.g. a host that doesn't support the
        //    parameterized local run) is treated like any other SMB failure: remember it, fall back.
        string? smbError;
        try
        {
            StageResult smb = await TrySmbCopyAsync(host, sourcePath, sourceIsFolder, targetPath, credential, token).ConfigureAwait(false);
            if (smb.Ok)
            {
                return smb;
            }

            smbError = smb.Message;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            smbError = ex.Message;
        }

        // 2) WinRM fallback — works wherever WinRM works, for boxes where SMB/admin$ is blocked.
        return await WinRmStageAsync(host, sourcePath, sourceIsFolder, targetPath, credential, smbError, token).ConfigureAwait(false);
    }

    // --- local -------------------------------------------------------------

    private static StageResult CopyLocal(string sourcePath, bool sourceIsFolder, string targetPath)
    {
        try
        {
            if (sourceIsFolder)
            {
                CopyDirectory(sourcePath, targetPath);
            }
            else
            {
                string? parent = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                File.Copy(sourcePath, targetPath, overwrite: true);
            }

            return new StageResult(true, targetPath, $"Staged to {targetPath}");
        }
        catch (Exception ex)
        {
            return new StageResult(false, null, $"Couldn't stage locally: {ex.Message}");
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(dest, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    // --- SMB admin share ---------------------------------------------------

    private async Task<StageResult> TrySmbCopyAsync(
        string host, string sourcePath, bool sourceIsFolder, string targetPath, PSCredential? credential, CancellationToken token)
    {
        if (!TryToAdminUnc(host, targetPath, out string unc, out string shareRoot))
        {
            return new StageResult(false, null, "the destination isn't a local drive path, so there's no admin share to map.");
        }

        var args = new Dictionary<string, object?>
        {
            ["Source"] = sourcePath,
            ["IsFolder"] = sourceIsFolder,
            ["Unc"] = unc,
            ["UncParent"] = Path.GetDirectoryName(unc) ?? unc,
            ["ShareRoot"] = shareRoot,
            ["Cred"] = credential,
        };

        PSExecutionResult result = await _powerShell.RunLocalAsync(SmbCopyScript, args, token).ConfigureAwait(false);
        PSObject? row = result.Output.Count > 0 ? result.Output[0] : null;
        if (row is not null && GetBool(row, "ok"))
        {
            return new StageResult(true, targetPath, $"Staged to {targetPath}");
        }

        string detail = row is not null
            ? GetString(row, "message") ?? "SMB copy failed"
            : result.Errors.Count > 0 ? result.Errors[0] : "SMB copy returned no result";
        return new StageResult(false, null, detail);
    }

    /// <summary>Turns a drive-rooted local path (<c>C:\Windows\Temp\X</c>) into its admin-share UNC on
    /// <paramref name="host"/> (<c>\\host\C$\Windows\Temp\X</c>) + the share root. False when the path
    /// isn't drive-rooted (nothing to map — caller falls back to WinRM).</summary>
    private static bool TryToAdminUnc(string host, string localPath, out string unc, out string shareRoot)
    {
        unc = string.Empty;
        shareRoot = string.Empty;
        if (localPath.Length < 3 || localPath[1] != ':' || (localPath[2] != '\\' && localPath[2] != '/'))
        {
            return false;
        }

        char drive = localPath[0];
        string rest = localPath[3..].Replace('/', '\\').TrimStart('\\');
        shareRoot = $@"\\{host}\{drive}$";
        unc = $@"{shareRoot}\{rest}";
        return true;
    }

    /// <summary>
    /// The SMB copy, run locally on the Vivre host (reaching the target over the file-sharing channel).
    /// When a credential is supplied it maps the admin share with it first (so an alternate account can
    /// be used); otherwise it copies as the current identity. A folder is copied into its parent (so it
    /// lands as <c>&lt;parent&gt;\&lt;name&gt;</c>); a single file is copied to its exact path.
    /// </summary>
    private const string SmbCopyScript = """
        param(
            [string]$Source,
            [bool]$IsFolder,
            [string]$Unc,
            [string]$UncParent,
            [string]$ShareRoot,
            [System.Management.Automation.PSCredential]$Cred
        )
        $ErrorActionPreference = 'Stop'
        $mapped = $false
        try {
            if ($Cred -and $Cred -ne [System.Management.Automation.PSCredential]::Empty) {
                New-PSDrive -Name VivreStage -PSProvider FileSystem -Root $ShareRoot -Credential $Cred -ErrorAction Stop | Out-Null
                $mapped = $true
            }
            if (-not (Test-Path -LiteralPath $UncParent)) {
                New-Item -ItemType Directory -Path $UncParent -Force | Out-Null
            }
            if ($IsFolder) {
                if (Test-Path -LiteralPath $Unc) { Remove-Item -LiteralPath $Unc -Recurse -Force -ErrorAction SilentlyContinue }
                Copy-Item -LiteralPath $Source -Destination $UncParent -Recurse -Force
                # Verify the copy actually landed: every source file must be present on the target. This
                # catches a partial copy or a target-side agent (Defender/EDR) blocking a written file.
                $srcCount = (Get-ChildItem -LiteralPath $Source -Recurse -File).Count
                $dstCount = (Get-ChildItem -LiteralPath $Unc -Recurse -File -ErrorAction SilentlyContinue).Count
                if ($dstCount -lt $srcCount) { throw "the copy was incomplete - $dstCount of $srcCount files reached the target (a security agent may have blocked one)." }
            } else {
                Copy-Item -LiteralPath $Source -Destination $Unc -Force
                # Verify the bytes landed (a 0-byte/short file means the write was blocked or interrupted).
                $srcLen = (Get-Item -LiteralPath $Source).Length
                $dstLen = (Get-Item -LiteralPath $Unc -ErrorAction SilentlyContinue).Length
                if ($dstLen -ne $srcLen) { throw "the copy was incomplete - $dstLen of $srcLen bytes reached the target (a security agent may have blocked the write)." }
            }
            [PSCustomObject]@{ ok = $true }
        } catch {
            [PSCustomObject]@{ ok = $false; message = $_.Exception.Message }
        } finally {
            if ($mapped) { Remove-PSDrive -Name VivreStage -Force -ErrorAction SilentlyContinue }
        }
        """;

    // --- WinRM fallback (zip + chunk + expand) -----------------------------

    private async Task<StageResult> WinRmStageAsync(
        string host, string sourcePath, bool sourceIsFolder, string targetPath, PSCredential? credential, string? smbError, CancellationToken token)
    {
        byte[] payloadZip;
        try
        {
            payloadZip = ZipSource(sourcePath, sourceIsFolder);
        }
        catch (Exception ex)
        {
            return Fail(host, smbError, $"couldn't package the source: {ex.Message}");
        }

        string base64Zip = Convert.ToBase64String(payloadZip);
        string expectedSha256 = Convert.ToHexString(SHA256.HashData(payloadZip));
        string zipName = $"VivreStage_{Guid.NewGuid():N}.zip";
        // A single file's zip holds it at the root, so expand into the file's parent; a folder's zip
        // holds its contents at the root, so expand into the target folder itself.
        string expandInto = sourceIsFolder ? targetPath : Path.GetDirectoryName(targetPath) ?? targetPath;

        try
        {
            for (int offset = 0; offset < base64Zip.Length; offset += Base64ChunkLength)
            {
                token.ThrowIfCancellationRequested();
                int length = Math.Min(Base64ChunkLength, base64Zip.Length - offset);
                string piece = base64Zip.Substring(offset, length);
                PSExecutionResult chunk = await _powerShell
                    .RunRemoteAsync(host, BuildChunkScript(zipName, piece, first: offset == 0), credential, cancellationToken: token)
                    .ConfigureAwait(false);
                if (TryReadFailure(chunk, out string? failure))
                {
                    return Fail(host, smbError, failure!);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(host, smbError, ex.Message);
        }

        PSExecutionResult result;
        try
        {
            result = await _powerShell
                .RunRemoteAsync(host, BuildFinalizeScript(PsSingleQuote(expandInto), zipName, expectedSha256), credential, cancellationToken: token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(host, smbError, ex.Message);
        }

        PSObject? row = result.Output.Count > 0 ? result.Output[0] : null;
        if (row is null)
        {
            return Fail(host, smbError, result.Errors.Count > 0 ? result.Errors[0] : "no result returned from the target");
        }

        return GetBool(row, "ok")
            ? new StageResult(true, targetPath, $"Staged to {targetPath}")
            : Fail(host, smbError, GetString(row, "message") ?? "stage failed on the target");
    }

    /// <summary>Builds the final failure message, noting that SMB was tried first when it was.</summary>
    private static StageResult Fail(string host, string? smbError, string winrmError)
    {
        string message = smbError is null
            ? $"Couldn't stage to {host}: {winrmError}"
            : $"Couldn't stage to {host}: {winrmError} (SMB was tried first: {smbError})";
        return new StageResult(false, null, message);
    }

    /// <summary>Zips the source for the WinRM fallback: a folder's CONTENTS at the zip root, or a single
    /// file at the zip root. Returns the bytes (the temp zip is deleted before returning).</summary>
    private static byte[] ZipSource(string sourcePath, bool sourceIsFolder)
    {
        string tempZip = Path.Combine(Path.GetTempPath(), $"Vivre_Stage_{Guid.NewGuid():N}.zip");
        try
        {
            if (sourceIsFolder)
            {
                ZipFile.CreateFromDirectory(sourcePath, tempZip, CompressionLevel.Optimal, includeBaseDirectory: false);
            }
            else
            {
                using var archive = ZipFile.Open(tempZip, ZipArchiveMode.Create);
                archive.CreateEntryFromFile(sourcePath, Path.GetFileName(sourcePath), CompressionLevel.Optimal);
            }

            return File.ReadAllBytes(tempZip);
        }
        finally
        {
            try
            {
                if (File.Exists(tempZip))
                {
                    File.Delete(tempZip);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup; a stray temp zip is harmless and the OS reaps %TEMP%.
            }
        }
    }

    /// <summary>A chunk reported <c>ok = $false</c> (a write failure on the target): pull its message.
    /// No output / no explicit failure ⇒ keep going (the finalize hash check is the real backstop).</summary>
    private static bool TryReadFailure(PSExecutionResult result, out string? message)
    {
        PSObject? row = result.Output.Count > 0 ? result.Output[0] : null;
        if (row is not null && row.Properties["ok"] is not null && !GetBool(row, "ok"))
        {
            message = GetString(row, "message") ?? "the target reported a write failure.";
            return true;
        }

        message = null;
        return false;
    }

    /// <summary>
    /// One chunk command: decode this slice of base64 and append the bytes to the temp zip (the first
    /// chunk removes any stale file first). Returns <c>{ ok = $true }</c> or <c>{ ok = $false; message }</c>.
    /// Internal so tests can assert on it.
    /// </summary>
    internal static string BuildChunkScript(string zipName, string base64Chunk, bool first)
    {
        string resetLine = first ? "Remove-Item $p -Force -ErrorAction SilentlyContinue" : "# append to the file the first chunk created";
        return $$"""
        $p = "$env:WINDIR\Temp\{{zipName}}"
        try {
            {{resetLine}}
            $bytes = [Convert]::FromBase64String('{{base64Chunk}}')
            $fs = [System.IO.File]::Open($p, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write)
            try { $fs.Write($bytes, 0, $bytes.Length) } finally { $fs.Dispose() }
            [PSCustomObject]@{ ok = $true }
        } catch {
            [PSCustomObject]@{ ok = $false; message = $_.Exception.Message }
        }
        """;
    }

    /// <summary>
    /// The finalize command: SHA-256-verify the assembled zip against what we shipped, create the
    /// destination, expand into it (overwriting), delete the temp zip, and emit <c>{ ok = $true; path }</c>
    /// — or <c>{ ok = $false; message }</c> on any failure (incl. a hash mismatch from a bad chunk).
    /// Internal so tests can assert on it. The destination is a pre-quoted literal so spaces are safe.
    /// </summary>
    internal static string BuildFinalizeScript(string destDirLiteral, string zipName, string expectedSha256) =>
        $$"""
        $ErrorActionPreference = 'Stop'
        try {
            $dest = {{destDirLiteral}}
            $zipPath = "$env:WINDIR\Temp\{{zipName}}"

            $expectedHash = '{{expectedSha256}}'
            $actualHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
            if ($actualHash -ne $expectedHash) {
                Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
                throw "Vivre stage integrity check failed on the target - the copied package did not match the expected SHA-256."
            }

            New-Item -ItemType Directory -Path $dest -Force | Out-Null
            Expand-Archive -LiteralPath $zipPath -DestinationPath $dest -Force
            Remove-Item $zipPath -Force -ErrorAction SilentlyContinue

            [PSCustomObject]@{ ok = $true; path = $dest }
        } catch {
            [PSCustomObject]@{ ok = $false; message = "Stage failed on the target: $($_.Exception.Message)" }
        }
        """;

    /// <summary>Wraps a string as a single-quoted PowerShell literal, escaping embedded single quotes
    /// by doubling them — so a destination path can't break out of the literal.</summary>
    private static string PsSingleQuote(string value) => "'" + value.Replace("'", "''") + "'";

    // --- PSObject scalar readers (same shape as VitalsProbe / RemediationService) ---

    private static object? Value(PSObject row, string name) => row.Properties[name]?.Value;

    private static bool GetBool(PSObject row, string name) => Value(row, name) is bool b && b;

    private static string? GetString(PSObject row, string name)
    {
        object? value = Value(row, name);
        return string.IsNullOrWhiteSpace(value?.ToString()) ? null : value!.ToString();
    }
}
