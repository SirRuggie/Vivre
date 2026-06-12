namespace Vivre.Core.Updates;

/// <summary>
/// The SMB + SCM update lane Vivre falls back to when a host rejects WinRM/Kerberos (0x80090322).
/// A seam over <see cref="SmbAgentLane"/> so the lane selection in <see cref="WuaUpdateLane"/> (try
/// WinRM → on <see cref="PowerShell.KerberosWrongPrincipalException"/> route here) is unit-testable
/// without a live target — the same role <c>IDcomVitalsReader</c> plays for the Vitals fallback.
/// </summary>
public interface ISmbAgentLane
{
    Task<HostPatchStatus> ScanAsync(string host, PatchOptions options, CancellationToken cancellationToken);

    Task<HostPatchStatus> InstallAsync(
        string host, PatchOptions options, IProgress<HostPatchStatus> progress, CancellationToken cancellationToken);

    Task<HostPatchStatus> UninstallAsync(
        string host, PatchOptions options, IProgress<HostPatchStatus> progress, CancellationToken cancellationToken);

    /// <summary>Copies the full CU .msu to the target and DISM-adds it as SYSTEM (the 2016 LCU lane). Stages
    /// only — terminal "PendingReboot" means staged/reboot-ready; the operator commits via a Reboot Wave.</summary>
    Task<HostPatchStatus> InstallFullPackageAsync(
        string host, string sourcePackagePath, PatchOptions options,
        IProgress<HostPatchStatus> progress, CancellationToken cancellationToken);

    /// <summary>Runs DISM /StartComponentCleanup as SYSTEM to reclaim component-store space. The agent
    /// refuses (defers) if a reboot is pending, so it can't collide with a staged update.</summary>
    Task<HostPatchStatus> RunComponentCleanupAsync(
        string host, PatchOptions options, IProgress<HostPatchStatus> progress, CancellationToken cancellationToken);
}
