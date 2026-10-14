using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Management.Automation;
using System.Security.Cryptography;
using Vivre.Core.Operations;
using Vivre.Core.PowerShell;

namespace Vivre.Core.Deploy;

/// <inheritdoc cref="IDeploymentService"/>
/// <remarks>
/// <para><b>SMB first.</b> The fast path copies a verified candidate on the target's admin share
/// (<c>\\host\C$\…</c>) via a short local orchestration script (so it runs from this machine, reaching
/// out over SMB). The credential is passed as a bound parameter — never interpolated into the script
/// text. This is how Windows deployment tools move files: LAN speed, no encoding, one copy, no zip.</para>
///
/// <para><b>WinRM fallback.</b> If SMB fails (admin$ disabled, port 445 blocked), the payload is zipped
/// and shipped over WinRM in chunks — a multi-MB installer base64'd inline is one oversized command that
/// WinRM rejects ("the remote session ended"), so the bytes go as a sequence of small commands that
/// append to a temp zip. A finalize command then SHA-256-verifies the assembled zip against what we
/// shipped (so a dropped/garbled chunk can't pass silently) and expands a candidate for manifest verification and recoverable replacement. Nothing is executed.</para>
///
/// <para><b>Local.</b> A target that resolves local is a direct file-system copy — no SMB, no WinRM.</para>
/// </remarks>
public sealed class DeploymentService : IDeploymentService
{
    /// <summary>Base64 characters per chunk command (~2 MiB of script text) on the WinRM fallback path.
    /// Comfortably under WinRM's per-command limits with ~10× margin below the size that fails, while
    /// keeping the round-trip count low. Multiple of 4 so each chunk decodes to a whole number of bytes.</summary>
    private const int Base64ChunkLength = 2_097_152;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PublicationLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly IPowerShellHost _powerShell;
    private readonly HostOperationCoordinator _operations;
    private readonly ProtectedStagingPathRegistry _protectedPaths;

    /// <param name="powerShell">The remoting host all transport goes through.</param>
    public DeploymentService(
        IPowerShellHost powerShell,
        HostOperationCoordinator? operations = null,
        ProtectedStagingPathRegistry? protectedPaths = null)
    {
        _powerShell = powerShell;
        _operations = operations ?? new HostOperationCoordinator();
        _protectedPaths = protectedPaths ?? new ProtectedStagingPathRegistry();
    }

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

        using HostOperationCoordinator.HostOperationClaim claim = _operations.TryBeginOperation(host, "Software staging");
        if (!claim.Acquired) return new StageResult(false, null, claim.RefusalMessage!);
        using HostOperationCoordinator.HostOperationClaim localClaim = _operations.TryBeginOperation("localhost", "Software staging source read");
        if (!localClaim.Acquired) return new StageResult(false, null, localClaim.RefusalMessage!);
        try { _protectedPaths.Register(host, targetPath); _protectedPaths.Register("localhost", sourcePath); }
        catch (Exception ex) { return new StageResult(false, null, $"Staging blocked — protected-path registry could not be saved: {ex.Message}"); }

        PackagePublicationPlan plan;
        try
        {
            plan = await Task.Run(() => PackagePublication.Prepare(sourcePath, sourceIsFolder, targetPath, HostName.IsLocal(host)), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new StageResult(false, null, $"Couldn't stage: source/destination validation failed: {ex.Message}");
        }

        string publicationKey = $"{HostOperationCoordinator.NormalizeHost(host)}|{plan.Target.ToUpperInvariant()}";
        SemaphoreSlim gate = PublicationLocks.GetOrAdd(publicationKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await StagePreparedAsync(host, plan, credential, token, claim).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<StageResult> StagePreparedAsync(
        string host, PackagePublicationPlan plan, PSCredential? credential, CancellationToken token,
        HostOperationCoordinator.HostOperationClaim claim)
    {
        string sourcePath = plan.Source;
        bool sourceIsFolder = plan.IsFolder;
        string targetPath = plan.Target;

        try
        {
            // Persist before the first copy. A registry that cannot be read or saved blocks staging,
            // so cleanup can never fall back to an unsafe empty protection set.
            _protectedPaths.Register(host, targetPath);
            _protectedPaths.Register(host, plan.Stage);
            _protectedPaths.Register(host, plan.Backup);
            _protectedPaths.Register(host, plan.Stage + ".extract");
            _protectedPaths.Register("localhost", sourcePath);
        }
        catch (Exception ex)
        {
            return new StageResult(false, null, $"Staging blocked — protected-path registry could not be saved: {ex.Message}");
        }

        // Local target: just copy on disk — no remoting at all.
        if (HostName.IsLocal(host))
        {
            StageResult local = PackagePublication.PublishLocal(plan, token);
            if (!local.Ok)
            {
                claim.MarkRemoteStateUncertain();
            }
            return local;
        }

        // Retry through WinRM only after an explicit safe pre-publication failure.
        string? smbError;
        try
        {
            SmbAttempt smb = await TrySmbCopyAsync(host, plan, credential, token).ConfigureAwait(false);
            if (smb.Result.Ok)
            {
                return smb.Result;
            }
            if (!smb.SafeToFallback)
            {
                claim.MarkRemoteStateUncertain();
                return smb.Result;
            }
            smbError = smb.Result.Message;
        }
        catch (OperationCanceledException)
        {
            claim.MarkRemoteStateUncertain();
            throw;
        }
        catch (Exception ex)
        {
            claim.MarkRemoteStateUncertain();
            return new StageResult(false, null, $"Couldn't stage to {host}: SMB ended without a trustworthy publication result, so WinRM was not retried: {ex.Message}. {RecoveryContext(plan)}");
        }

        // 2) WinRM fallback — works wherever WinRM works, for boxes where SMB/admin$ is blocked.
        try
        {
            StageResult result = await WinRmStageAsync(host, plan, credential, smbError, token).ConfigureAwait(false);
            if (!result.Ok)
            {
                claim.MarkRemoteStateUncertain();
            }
            return result;
        }
        catch
        {
            claim.MarkRemoteStateUncertain();
            throw;
        }
    }

    // --- SMB admin share ---------------------------------------------------

    private async Task<SmbAttempt> TrySmbCopyAsync(
        string host, PackagePublicationPlan plan, PSCredential? credential, CancellationToken token)
    {
        if (!TryToAdminUnc(host, plan.Target, out string unc, out string shareRoot) ||
            !TryToAdminUnc(host, plan.Stage, out string stageUnc, out _) ||
            !TryToAdminUnc(host, plan.Backup, out string backupUnc, out _))
        {
            return new(new StageResult(false, null, "The destination isn't a local drive path, so there's no admin share to map."), true);
        }

        var remotePlan = plan with { Target = unc, Stage = stageUnc, Backup = backupUnc };

        var args = new Dictionary<string, object?>
        {
            ["Source"] = plan.Source,
            ["ShareRoot"] = shareRoot,
            ["Cred"] = credential,
        };

        token.ThrowIfCancellationRequested();
        PSExecutionResult result = await _powerShell.RunLocalAsync(PackagePublication.BuildSmbScript(remotePlan), args, CancellationToken.None).ConfigureAwait(false);
        PSObject? row = result.Output.Count > 0 ? result.Output[0] : null;
        if (row is not null && GetBool(row, "ok"))
        {
            string message = GetString(row, "message") ?? $"Staged and verified at {plan.Target}";
            string? retainedPath = GetString(row, "recoveryPath");
            if (!string.IsNullOrWhiteSpace(retainedPath) && !message.Contains(retainedPath, StringComparison.OrdinalIgnoreCase)) message += $" Recovery path: {retainedPath}.";
            return new(new StageResult(true, plan.Target, message), false);
        }

        if (row is null)
        {
            string unknown = result.Errors.Count > 0 ? result.Errors[0] : "SMB publication returned no result";
            return new(new StageResult(false, null, $"SMB publication state is unknown; WinRM was not retried: {unknown}. Recovery locations: target {plan.Target}, candidate {plan.Stage}, backup {plan.Backup}."), false);
        }
        string detail = GetString(row, "message") ?? "SMB copy failed";
        string? recovery = GetString(row, "recoveryPath");
        if (!string.IsNullOrWhiteSpace(recovery)) detail += $" Previous package retained at {recovery}.";
        return new(new StageResult(false, null, detail), GetBool(row, "safeToFallback"));
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

    // --- WinRM fallback (zip + chunk + expand) -----------------------------

    private async Task<StageResult> WinRmStageAsync(
        string host, PackagePublicationPlan plan, PSCredential? credential, string? smbError, CancellationToken token)
    {
        string sourcePath = plan.Source;
        bool sourceIsFolder = plan.IsFolder;
        string targetPath = plan.Target;
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

        token.ThrowIfCancellationRequested();
        PSExecutionResult result;
        try
        {
            result = await _powerShell
                .RunRemoteAsync(host, PackagePublication.BuildPublishScript(
                    $"\"$env:WINDIR\\Temp\\{zipName}\"", true, targetPath, plan.Stage, plan.Backup,
                    sourceIsFolder, PackagePublication.ManifestBase64(plan.Manifest), expectedSha256), credential, cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Fail(host, smbError, $"Publication outcome is unknown: {ex.Message}. {RecoveryContext(plan)}");
        }

        PSObject? row = result.Output.Count > 0 ? result.Output[0] : null;
        if (row is null)
        {
            return Fail(host, smbError, $"Publication outcome is unknown: {(result.Errors.Count > 0 ? result.Errors[0] : "no result returned from the target")}. {RecoveryContext(plan)}");
        }

        if (GetBool(row, "ok"))
        {
            string success = GetString(row, "message") ?? $"Staged and verified at {targetPath}";
            string? retained = GetString(row, "recoveryPath");
            if (!string.IsNullOrWhiteSpace(retained) && !success.Contains(retained, StringComparison.OrdinalIgnoreCase)) success += $" Recovery path: {retained}.";
            return new StageResult(true, targetPath, success);
        }
        string detail = GetString(row, "message") ?? "stage failed on the target";
        string? recovery = GetString(row, "recoveryPath");
        if (!string.IsNullOrWhiteSpace(recovery)) detail += $" Previous package retained at {recovery}.";
        return Fail(host, smbError, detail);
    }

    private static string RecoveryContext(PackagePublicationPlan plan) =>
        $"Check destination {plan.Target}, candidate {plan.Stage}, and recovery backup {plan.Backup} before retrying.";

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

    // --- PSObject scalar readers (same shape as VitalsProbe / RemediationService) ---

    private static object? Value(PSObject row, string name) => row.Properties[name]?.Value;

    private static bool GetBool(PSObject row, string name) => Value(row, name) is bool b && b;

    private static string? GetString(PSObject row, string name)
    {
        object? value = Value(row, name);
        return string.IsNullOrWhiteSpace(value?.ToString()) ? null : value!.ToString();
    }

    private sealed record SmbAttempt(StageResult Result, bool SafeToFallback);
}
