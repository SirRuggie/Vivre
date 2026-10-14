using System.Collections.Concurrent;
using System.IO;
using System.Management.Automation;
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
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PublicationLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly IPowerShellHost _powerShell;
    private readonly HostOperationCoordinator _operations;
    private readonly ProtectedStagingPathRegistry _protectedPaths;
    private readonly StagingTransferJournal _transfers;

    /// <param name="powerShell">The remoting host all transport goes through.</param>
    public DeploymentService(
        IPowerShellHost powerShell,
        HostOperationCoordinator? operations = null,
        ProtectedStagingPathRegistry? protectedPaths = null,
        StagingTransferJournal? transfers = null)
    {
        _powerShell = powerShell;
        _operations = operations ?? new HostOperationCoordinator();
        _protectedPaths = protectedPaths ?? new ProtectedStagingPathRegistry();
        _transfers = transfers ?? new StagingTransferJournal();
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
            string? pending = null;
            if (!HostName.IsLocal(host))
            {
                try { pending = await RetryPendingCleanupAsync(host, credential, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { return new StageResult(false, null, $"Staging blocked — pending transfer cleanup records could not be read: {ex.Message}"); }
            }
            StageResult result = await StagePreparedAsync(host, plan, credential, token, claim).ConfigureAwait(false);
            return pending is null ? result : result with { Message = result.Message + " " + pending };
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
        StagingArchive? archive = null;
        StagingTransferJournal.Lease? lease = null;
        bool remoteAttempted = false;
        bool publicationStarted = false;
        bool initializationOutstanding = false;
        StageResult result = Fail(host, smbError, "transfer did not complete");
        OperationCanceledException? cancellation = null;
        var cleanupNotes = new List<string>();
        try
        {
            archive = await StagingArchive.CreateAsync(plan.Source, plan.IsFolder, token).ConfigureAwait(false);
            _protectedPaths.Register("localhost", archive.Path);
            lease = _transfers.Create(host, plan.Target);
            StagingTransferRecord transfer = lease.Record;
            _protectedPaths.Register(host, transfer.Directory);
            _protectedPaths.Register(host, transfer.Directory + ".cleanup-" + transfer.OwnerToken);
            _protectedPaths.Register(host, transfer.Directory + ".init-" + transfer.OwnerToken);
            token.ThrowIfCancellationRequested();
            remoteAttempted = true;
            // Let the short remote initialization finish atomically, but bound our wait. An abandoned
            // initializer keeps its journal and must not race immediate cleanup.
            initializationOutstanding = true;
            Task<PSExecutionResult> initialization = _powerShell.RunRemoteAsync(host,
                StagingTransferScripts.BuildInitialize(transfer.Directory, transfer.Id, transfer.OwnerToken),
                credential, cancellationToken: CancellationToken.None);
            _ = initialization.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            PSExecutionResult initialized = await initialization.WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            initializationOutstanding = false;
            RequireTransferSuccess(initialized, "initialize transfer");
            lease.MarkInitialized();
            token.ThrowIfCancellationRequested();

            await foreach (StagingArchiveChunk chunk in archive.ReadChunksAsync(token).ConfigureAwait(false))
            {
                RequireTransferSuccess(await _powerShell.RunRemoteAsync(host,
                    StagingTransferScripts.BuildChunk(transfer.Directory, transfer.Id, transfer.OwnerToken, chunk.Base64, chunk.Offset),
                    credential, cancellationToken: token).ConfigureAwait(false), "write transfer chunk");
            }

            token.ThrowIfCancellationRequested();
            // Once publication begins, preserve F05's non-cancellable replacement/recovery transaction.
            // Its exclusive transfer lock also prevents a late-running command racing a cleanup retry.
            string publish = PackagePublication.BuildPublishScript("$payload", true, plan.Target, plan.Stage, plan.Backup,
                plan.IsFolder, PackagePublication.ManifestBase64(plan.Manifest), archive.Sha256, cleanupSourceZip: false);
            publicationStarted = true;
            PSExecutionResult response = await _powerShell.RunRemoteAsync(host,
                StagingTransferScripts.WrapPublication(transfer.Directory, transfer.Id, transfer.OwnerToken, publish),
                credential, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            PSObject? row = response.Output.Count == 1 ? response.Output[0] : null;
            if (row is null || Value(row, "ok") is not bool || response.HadErrors || response.Errors.Count > 0)
                result = Fail(host, smbError, $"Publication outcome is unknown. {RecoveryContext(plan)}");
            else
            {
                string message = GetString(row, "message") ?? (GetBool(row, "ok") ? $"Staged and verified at {plan.Target}" : "publication failed");
                string? recovery = GetString(row, "recoveryPath");
                if (recovery is not null && !message.Contains(recovery, StringComparison.OrdinalIgnoreCase)) message += $" Recovery path: {recovery}.";
                result = GetBool(row, "ok") ? new StageResult(true, plan.Target, message) : Fail(host, smbError, message);
            }
        }
        catch (OperationCanceledException ex) { cancellation = ex; }
        catch (Exception ex) { result = Fail(host, smbError, $"{(publicationStarted ? "Publication outcome is unknown. " : "")}{ex.Message}. {RecoveryContext(plan)}"); }
        finally
        {
            if (lease is not null)
            {
                try
                {
                    string? note = initializationOutstanding
                        ? $"Transfer initialization may still be running at {lease.Record.Directory}; cleanup remains pending in the saved journal for a later staging request."
                        : remoteAttempted
                        ? await CleanupTransferAsync(lease, credential, CancellationToken.None).ConfigureAwait(false)
                        : CompleteUnstartedTransfer(lease);
                    if (note is not null) cleanupNotes.Add(note);
                }
                finally { lease.Dispose(); }
            }
            if (archive is not null)
            {
                try { archive.Dispose(); }
                catch (Exception ex) { cleanupNotes.Add($"Local archive cleanup remains pending at {archive.Path}: {ex.Message}"); }
            }
        }
        string cleanup = string.Join(" ", cleanupNotes);
        if (cancellation is not null)
            throw new OperationCanceledException("Staging cancelled. " + cancellation.Message + " " + cleanup, cancellation, token);
        return cleanup.Length == 0 ? result : result with { Message = result.Message + " " + cleanup };
    }

    private static void RequireTransferSuccess(PSExecutionResult result, string operation)
    {
        if (!result.HadErrors && result.Errors.Count == 0 && result.Output.Count == 1 && GetBool(result.Output[0], "ok")) return;
        string reason = result.Output.Count > 0 ? GetString(result.Output[0], "message") ?? "no valid acknowledgement" : "no acknowledgement";
        if (result.Errors.Count > 0) reason += ": " + result.Errors[0];
        throw new IOException($"Could not {operation}: {reason}");
    }

    private async Task<string?> RetryPendingCleanupAsync(string host, PSCredential? credential, CancellationToken token)
    {
        var notes = new List<string>();
        foreach (StagingTransferJournal.Lease lease in _transfers.AcquirePending(host))
        {
            using (lease)
            {
                token.ThrowIfCancellationRequested();
                _protectedPaths.Register(host, lease.Record.Directory);
                _protectedPaths.Register(host, lease.Record.Directory + ".cleanup-" + lease.Record.OwnerToken);
                _protectedPaths.Register(host, lease.Record.Directory + ".init-" + lease.Record.OwnerToken);
                string? note = await CleanupTransferAsync(lease, credential, token).ConfigureAwait(false);
                if (note is not null) notes.Add(note);
            }
        }
        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    private async Task<string?> CleanupTransferAsync(StagingTransferJournal.Lease lease, PSCredential? credential, CancellationToken token)
    {
        StagingTransferRecord transfer = lease.Record;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            Task<PSExecutionResult> cleanup = _powerShell.RunRemoteAsync(transfer.Host,
                StagingTransferScripts.BuildCleanup(transfer.Directory, transfer.Id, transfer.OwnerToken),
                credential, cancellationToken: deadline.Token);
            // A transport ignoring cancellation must not hang the caller. Its remote file lock keeps
            // a late cleanup mutually exclusive with any other command for this transfer.
            _ = cleanup.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            PSExecutionResult response = await cleanup.WaitAsync(deadline.Token).ConfigureAwait(false);
            RequireTransferSuccess(response, "clean transfer files");
            if (!transfer.InitializationConfirmed && GetBool(response.Output[0], "absent"))
                return $"Transfer initialization outcome is unknown at {transfer.Directory}; cleanup record retained because a late initializer may still appear. Retry staging later; if it remains absent, verify remote execution has stopped before manually clearing the record.";
            lease.Complete();
            return null;
        }
        catch (OperationCanceledException ex) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException($"Transfer cleanup remains pending at {transfer.Directory}; saved for a later staging retry.", ex, token);
        }
        catch (Exception ex)
        {
            return $"Transfer cleanup remains pending at {transfer.Directory} (transfer {transfer.Id}): {ex.Message}. Saved for retry on the next Stage software request to this host.";
        }
    }

    private static string? CompleteUnstartedTransfer(StagingTransferJournal.Lease lease)
    {
        try { lease.Complete(); return null; }
        catch (Exception ex) { return $"Unstarted transfer record remains pending for {lease.Record.Directory}: {ex.Message}"; }
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
