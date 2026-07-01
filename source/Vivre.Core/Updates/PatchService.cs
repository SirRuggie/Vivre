using System.Collections.Concurrent;
using Vivre.Core.Credentials;
using Vivre.Core.Logging;
using Vivre.Core.PowerShell;

namespace Vivre.Core.Updates;

/// <inheritdoc cref="IPatchService"/>
/// <remarks>
/// Thin orchestrator over <see cref="WuaUpdateLane"/> (the WUA lane is the only lane
/// today; an SCCM-deployment lane is deferred — see docs/windows-patching-lane.md). Mirrors
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

    // The Server 2016 full-package CU lane + its night-time reboot wave. Built here (not in the
    // composition root) so they ride the WUA lane's same SMB/DCOM transport + agent bytes, and so every
    // 2016 operation shares the _inFlight per-host guard below with Install/Uninstall.
    private readonly FullPackageLcuLane _lcu;
    private readonly RebootWave _wave;

    // Hosts with a CBS/DISM operation in flight (install / uninstall / Installed-scope scan / 2016
    // stage / cleanup / reboot-wave). Verify is read-only and deliberately not claimed.
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    public PatchService(IPowerShellHost powerShell, IActivityLog? activity = null)
    {
        _wua = new WuaUpdateLane(powerShell, activityLog: activity);
        _lcu = new FullPackageLcuLane(_wua.Smb);
        _wave = new RebootWave(new DcomRebootTrigger(), new TcpReachabilityProbe());
    }

    public async Task<HostPatchStatus> ScanAsync(
        string host,
        PatchOptions options,
        ConnectionCredential? credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

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
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

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
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

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

    // --- Server 2016 full-package CU lane (gated to build 14393 by the caller) --------------------

    public async Task<HostPatchStatus> StageLcuAsync(
        string host,
        string packageDirectory,
        LcuTarget target,
        PatchOptions options,
        IProgress<HostPatchStatus> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);

        if (!TryClaim(host))
        {
            progress.Report(AlreadyInProgress);
            return AlreadyInProgress;
        }

        try
        {
            return await _lcu.StageAsync(host, packageDirectory, target, options, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Release(host);
        }
    }

    public async Task<HostPatchStatus> ComponentCleanupLcuAsync(
        string host,
        PatchOptions options,
        IProgress<HostPatchStatus> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);

        if (!TryClaim(host))
        {
            progress.Report(AlreadyInProgress);
            return AlreadyInProgress;
        }

        try
        {
            return await _lcu.ComponentCleanupAsync(host, options, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Release(host);
        }
    }

    public async Task<HostPatchStatus> RebootWaveLcuAsync(
        string host,
        int targetUbr,
        RebootWaveOptions waveOptions,
        IProgress<HostPatchStatus> progress,
        CancellationToken cancellationToken = default,
        IRebootGate? rebootGate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(waveOptions);
        ArgumentNullException.ThrowIfNull(progress);

        if (!TryClaim(host))
        {
            progress.Report(AlreadyInProgress);
            return AlreadyInProgress;
        }

        try
        {
            var readiness = new DcomRebootReadinessProbe();
            var confirmation = new UbrConfirmation(new DcomLcuBuildReader(), targetUbr);
            return await _wave.RebootAndCommitAsync(host, waveOptions, readiness, confirmation, progress, cancellationToken, rebootGate).ConfigureAwait(false);
        }
        finally
        {
            Release(host);
        }
    }

    public async Task<HostPatchStatus> RebootWaveWuaAsync(
        string host,
        RebootWaveOptions waveOptions,
        IProgress<HostPatchStatus> progress,
        CancellationToken cancellationToken = default,
        IRebootGate? rebootGate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(waveOptions);
        ArgumentNullException.ThrowIfNull(progress);

        if (!TryClaim(host))
        {
            progress.Report(AlreadyInProgress);
            return AlreadyInProgress;
        }

        try
        {
            var readiness = new BasicReachabilityReadinessProbe();
            var confirmation = new ReadyConfirmation();
            return await _wave.RebootAndCommitAsync(host, waveOptions, readiness, confirmation, progress, cancellationToken, rebootGate).ConfigureAwait(false);
        }
        finally
        {
            Release(host);
        }
    }

    public Task<LcuVerifyResult> VerifyLcuAsync(
        string host,
        int targetUbr,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        // Read-only registry read (no CBS/DISM): never serialized, so a box can be verified even while
        // something else is queued against it — mirrors the unclaimed Applicable-scope scan.
        return _lcu.VerifyAsync(host, targetUbr, cancellationToken);
    }

    public LcuPackageResolution CheckLcuPackage(string packageDirectory, LcuTarget target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentNullException.ThrowIfNull(target);

        // Pure local directory read — no host involved, so no _inFlight claim.
        return _lcu.CheckPackage(packageDirectory, target);
    }

    // host is validated non-null/whitespace at each public entry point.
    private bool TryClaim(string host) => _inFlight.TryAdd(host, 0);

    private void Release(string host) => _inFlight.TryRemove(host, out _);

    private static HostPatchStatus AlreadyInProgress =>
        new(PatchPhase.Idle, "Skipped — an update operation is already in progress on this host.");
}
