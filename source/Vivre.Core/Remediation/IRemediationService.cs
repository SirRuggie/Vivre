using System.Management.Automation;

namespace Vivre.Core.Remediation;

/// <summary>The outcome of a single remediation action.</summary>
/// <param name="Ok">True when the action succeeded.</param>
/// <param name="Message">A human-readable result/failure line for the activity log + status.</param>
public sealed record RemediationResult(bool Ok, string Message);

/// <summary>Overall outcome of a disk-cleanup run.</summary>
public enum DiskCleanupOutcome
{
    Completed,
    PartiallyCompleted,
    Failed,
}

/// <summary>The outcome of a disk-cleanup run.</summary>
/// <param name="Outcome">Truthful aggregate outcome across cleanup components and service recovery.</param>
/// <param name="ReclaimedBytes">Reliably measured bytes freed, or null when either measurement was unavailable.</param>
/// <param name="NewFreePercent">System-drive free percentage after cleanup, or null if unknown.</param>
/// <param name="Message">A human-readable summary.</param>
public sealed record DiskCleanupResult(
    DiskCleanupOutcome Outcome,
    long? ReclaimedBytes,
    double? NewFreePercent,
    string Message)
{
    public bool Ok => Outcome == DiskCleanupOutcome.Completed;

    /// <summary>True when the target script returned a trustworthy terminal row. False for transport,
    /// engine, or malformed-result uncertainty; independent of whether cleanup itself succeeded.</summary>
    public bool ExecutionConfirmed { get; init; } = true;

    /// <summary>True when the host can safely leave cleanup exclusion. False when execution is
    /// uncertain or cleanup may have left Windows Update service recovery unresolved.</summary>
    public bool SafeToReleaseHost { get; init; } = true;
}

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
    /// the recycle bin. Reports bytes reclaimed + the new free percentage when they can be measured.
    /// <paramref name="protectedPaths"/> are preserved in addition to Vivre's built-in package/work paths.</summary>
    Task<DiskCleanupResult> FreeDiskSpaceAsync(
        string host,
        PSCredential? credential = null,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? protectedPaths = null);

    /// <summary>Lists the top processes by memory (working set), newest snapshot — for spotting a hog.</summary>
    Task<IReadOnlyList<ProcessInfo>> GetTopProcessesAsync(
        string host, PSCredential? credential = null, CancellationToken cancellationToken = default);

    /// <summary>Force-ends a process by PID.</summary>
    Task<RemediationResult> EndProcessAsync(
        string host, int processId, PSCredential? credential = null, CancellationToken cancellationToken = default);
}
