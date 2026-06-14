using Vivre.Core.Credentials;

namespace Vivre.Core.Updates;

/// <summary>
/// Orchestrates the Windows Update Agent (WUA) lane — the BatchPatch replacement.
/// Scan reads the applicable-update list over WinRM; Install runs the work as a
/// one-time SYSTEM scheduled task on the target (WUA install won't run inside a
/// WinRM network-logon → <c>WU_E_NO_INTERACTIVE_USER</c>) and reports progress
/// by polling a JSON file the task writes.
/// </summary>
public interface IPatchService
{
    /// <summary>
    /// Searches <paramref name="host"/> for applicable updates from the configured
    /// source and returns the list + count (after the exclude filter). Read-only.
    /// </summary>
    Task<HostPatchStatus> ScanAsync(
        string host,
        PatchOptions options,
        ConnectionCredential? credential,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads + installs all applicable updates on <paramref name="host"/> via a
    /// one-time SYSTEM scheduled task, reporting each phase through
    /// <paramref name="progress"/>. Always cleans up the task + temp files (incl. on
    /// cancellation/error). Returns the terminal status.
    /// </summary>
    Task<HostPatchStatus> InstallAsync(
        string host,
        PatchOptions options,
        ConnectionCredential? credential,
        IProgress<HostPatchStatus> progress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uninstalls the selected installed updates on <paramref name="host"/> via the same
    /// one-time SYSTEM scheduled task pattern as <see cref="InstallAsync"/>. Honors
    /// <see cref="PatchOptions.IncludeKbArticleIds"/> as the per-machine selection, and only
    /// touches updates Windows marks as <c>IsUninstallable</c>. Reports progress through
    /// <paramref name="progress"/>; cleans up the task + temp files regardless of outcome.
    /// </summary>
    Task<HostPatchStatus> UninstallAsync(
        string host,
        PatchOptions options,
        ConnectionCredential? credential,
        IProgress<HostPatchStatus> progress,
        CancellationToken cancellationToken = default);

    // --- Server 2016 (build 14393) full-package cumulative-update lane -----------------------------
    // These four sidestep the broken Express-delta WUA pipeline on 2016 by DISM-adding the full CU .msu
    // over SMB/DCOM. The caller gates to 14393 (see LcuRouting); each shares this service's per-host
    // serialization with Install/Uninstall so a stage can't collide with a WUA install on the same box.

    /// <summary>
    /// Daytime step: verify the right CU package is in <paramref name="packageDirectory"/>, then deliver +
    /// DISM-add it on <paramref name="host"/> while it keeps serving. Does not reboot. Terminal status is
    /// <see cref="PatchPhase.PendingReboot"/> (staged — reboot-ready), <see cref="PatchPhase.Done"/>
    /// (already current), or a failure carrying the catalog link when the package isn't present/correct.
    /// </summary>
    Task<HostPatchStatus> StageLcuAsync(
        string host,
        string packageDirectory,
        LcuTarget target,
        PatchOptions options,
        IProgress<HostPatchStatus> progress,
        CancellationToken cancellationToken = default);

    /// <summary>Reclaims component-store space (DISM /StartComponentCleanup as SYSTEM) so a tight 2016 box
    /// has room for the CU. The agent refuses if a reboot is already pending, so it can't disturb a staged
    /// update. Run before <see cref="StageLcuAsync"/> on a low-disk box.</summary>
    Task<HostPatchStatus> ComponentCleanupLcuAsync(
        string host,
        PatchOptions options,
        IProgress<HostPatchStatus> progress,
        CancellationToken cancellationToken = default);

    /// <summary>Night step: reboot a staged box and track the commit until the UBR confirms success, a
    /// rollback is detected, the reboot won't take, or it stays offline past the hard cap. Graceful first,
    /// auto-escalating to forced; the clock only flags "overdue", the UBR decides pass/fail.</summary>
    Task<HostPatchStatus> RebootWaveLcuAsync(
        string host,
        int targetUbr,
        RebootWaveOptions waveOptions,
        IProgress<HostPatchStatus> progress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reboot-and-verify for non-2016 boxes (Server 2019, 2022, Windows 10/11, etc.) after a WUA
    /// install. Reboots the host, waits for it to come back online, then confirms the OS management
    /// stack is responsive (<see cref="ReadyConfirmation"/>). Unlike the 2016 lane, success is
    /// declared when the OS answers — whether updates "took" is validated by the caller's WUA rescan.
    ///
    /// <para>Uses the same per-host <c>_inFlight</c> claim as <see cref="RebootWaveLcuAsync"/> so a
    /// reboot wave can never collide with an install or another wave on the same host.</para>
    ///
    /// <para>Nothing calls this yet — commit 5 wires it from the VM.</para>
    /// </summary>
    Task<HostPatchStatus> RebootWaveWuaAsync(
        string host,
        RebootWaveOptions waveOptions,
        IProgress<HostPatchStatus> progress,
        CancellationToken cancellationToken = default);

    /// <summary>The durable net: read the host's build/UBR and decide whether the staged CU committed.
    /// Read-only (no CBS/DISM), so it is never serialized — an operator can check any box, any time. A box
    /// that can't be read yet is <see cref="LcuVerifyOutcome.Unreachable"/> (retry), never a failure.</summary>
    Task<LcuVerifyResult> VerifyLcuAsync(
        string host,
        int targetUbr,
        CancellationToken cancellationToken = default);

    /// <summary>Read-only precheck (touches no host): is the configured 2016 CU <c>.msu</c> present + correct
    /// in <paramref name="packageDirectory"/>? Lets the UI guide the operator to drop the file BEFORE a Stage
    /// runs, so a missing package never starts a sweep. <see cref="LcuPackageStatus.Found"/> = ready.</summary>
    LcuPackageResolution CheckLcuPackage(string packageDirectory, LcuTarget target);
}
