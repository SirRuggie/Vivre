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
}
