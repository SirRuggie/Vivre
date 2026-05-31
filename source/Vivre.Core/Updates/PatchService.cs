using System.Collections.Concurrent;
using Vivre.Core.Credentials;
using Vivre.Core.PowerShell;

namespace Vivre.Core.Updates;

/// <inheritdoc cref="IPatchService"/>
/// <remarks>
/// Thin orchestrator over <see cref="WuaUpdateLane"/> (the WUA lane is the only lane
/// today; an SCCM-deployment lane is deferred — see UPDATE_PLAN.md). Mirrors
/// <c>ConfigMgrClient</c>'s construction (an <see cref="IPowerShellHost"/> in, logic
/// delegated). Takes a <see cref="ConnectionCredential"/> rather than a
/// <c>PSCredential</c> because install needs the raw credential for the DCOM
/// <c>Win32_Process.Create</c> fallback as well as the WinRM path.
///
/// <para><b>Per-host serialization:</b> install, uninstall, and an Installed-scope scan all touch
/// the target's CBS/DISM servicing stack, so this service refuses to run two of them against the
/// same host at once — it claims the host for the duration and skips a second concurrent request.
/// This is the authoritative guard: <c>PatchService</c> is a single app-wide instance, so it also
/// catches the cross-tab "same host in two tabs" case the per-row UI guard (each tab has its own
/// <c>Computer</c>) cannot see. An Applicable-scope scan is a read-only WUA search (no CBS/DISM)
/// and is never serialized.</para>
/// </remarks>
public sealed class PatchService : IPatchService
{
    private readonly WuaUpdateLane _wua;

    // Hosts with a CBS/DISM operation in flight (install / uninstall / Installed-scope scan).
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    public PatchService(IPowerShellHost powerShell) => _wua = new WuaUpdateLane(powerShell);

    public async Task<HostPatchStatus> ScanAsync(
        string host,
        PatchOptions options,
        ConnectionCredential? credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Applicable-scope scan is read-only (no CBS/DISM) — let it run anytime.
        if (options.Scope != UpdateScope.Installed)
        {
            return await _wua.ScanAsync(host, options, credential, cancellationToken).ConfigureAwait(false);
        }

        if (!TryClaim(host))
        {
            return AlreadyInProgress;
        }

        try
        {
            return await _wua.ScanAsync(host, options, credential, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Release(host);
        }
    }

    public async Task<HostPatchStatus> InstallAsync(
        string host,
        PatchOptions options,
        ConnectionCredential? credential,
        IProgress<HostPatchStatus> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);

        if (!TryClaim(host))
        {
            progress.Report(AlreadyInProgress);
            return AlreadyInProgress;
        }

        try
        {
            return await _wua.InstallAsync(host, options, credential, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Release(host);
        }
    }

    public async Task<HostPatchStatus> UninstallAsync(
        string host,
        PatchOptions options,
        ConnectionCredential? credential,
        IProgress<HostPatchStatus> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);

        if (!TryClaim(host))
        {
            progress.Report(AlreadyInProgress);
            return AlreadyInProgress;
        }

        try
        {
            return await _wua.UninstallAsync(host, options, credential, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Release(host);
        }
    }

    private bool TryClaim(string host) => _inFlight.TryAdd(host ?? string.Empty, 0);

    private void Release(string host) => _inFlight.TryRemove(host ?? string.Empty, out _);

    private static HostPatchStatus AlreadyInProgress =>
        new(PatchPhase.Idle, "Skipped — an update operation is already in progress on this host.");
}
