using System.Management.Automation;

namespace Vivre.Core.Remediation;

/// <summary>The outcome of a single remediation action.</summary>
/// <param name="Ok">True when the action succeeded.</param>
/// <param name="Message">A human-readable result/failure line for the activity log + status.</param>
public sealed record RemediationResult(bool Ok, string Message);

/// <summary>The outcome of a disk-cleanup run.</summary>
/// <param name="Ok">True when cleanup ran (even if little was reclaimed).</param>
/// <param name="ReclaimedBytes">Bytes freed on the system drive (never negative).</param>
/// <param name="NewFreePercent">System-drive free percentage after cleanup, or null if unknown.</param>
/// <param name="Message">A human-readable summary.</param>
public sealed record DiskCleanupResult(bool Ok, long ReclaimedBytes, double? NewFreePercent, string Message);

/// <summary>One process in the "top processes" triage list.</summary>
/// <param name="Name">Process image name (no extension).</param>
/// <param name="Id">PID.</param>
/// <param name="WorkingSetMb">Private working set in MB (the reliable "what's hogging memory" signal).</param>
/// <param name="CpuSeconds">Total CPU time consumed since start, in seconds (secondary signal); null if unknown.</param>
public sealed record ProcessInfo(string Name, int Id, double WorkingSetMb, double? CpuSeconds);

/// <summary>
/// Acts on the problems the Vitals breakdown surfaces — the "heal the card" half of triage. Every
/// call runs one short PowerShell action through <see cref="PowerShell.IPowerShellHost"/> (local for
/// the local box, WinRM otherwise) under the supplied admin credential, mirroring
/// <see cref="Vitals.VitalsProbe"/>. Read-modify actions only; nothing here needs the SYSTEM agent.
/// </summary>
public interface IRemediationService
{
    /// <summary>Starts a stopped service identified by its display name (exact match).</summary>
    Task<RemediationResult> StartServiceAsync(
        string host, string serviceDisplayName, PSCredential? credential = null, CancellationToken cancellationToken = default);

    /// <summary>Frees space on the system drive: clears TEMP, the Windows Update download cache, and
    /// the recycle bin. Reports bytes reclaimed + the new free percentage.</summary>
    Task<DiskCleanupResult> FreeDiskSpaceAsync(
        string host, PSCredential? credential = null, CancellationToken cancellationToken = default);

    /// <summary>Lists the top processes by memory (working set), newest snapshot — for spotting a hog.</summary>
    Task<IReadOnlyList<ProcessInfo>> GetTopProcessesAsync(
        string host, PSCredential? credential = null, CancellationToken cancellationToken = default);

    /// <summary>Force-ends a process by PID.</summary>
    Task<RemediationResult> EndProcessAsync(
        string host, int processId, PSCredential? credential = null, CancellationToken cancellationToken = default);
}
