namespace Vivre.Core.Vitals;

/// <summary>
/// A deep health snapshot of one machine — the "vitals" behind a <c>Vivre Card</c>'s life force.
/// Complements <see cref="Sccm.SccmClientInfo"/> (the SCCM-client health) with the OS-level signals
/// an admin reaches for when triaging a sick box: disk, memory, CPU, uptime, dead services, and
/// recent error events. Every scalar is nullable — <c>null</c> means "couldn't read it" (a probe was
/// denied or the class was absent), which the scorer treats as unknown rather than a problem.
/// </summary>
/// <param name="SystemDriveFreePercent">Free space on the system drive as a percentage (0-100).</param>
/// <param name="SystemDriveFreeGb">Free space on the system drive in GB.</param>
/// <param name="MemoryUsedPercent">Physical memory in use as a percentage (0-100).</param>
/// <param name="CpuLoadPercent">Instantaneous CPU load as a percentage (0-100); a coarse snapshot.</param>
/// <param name="LastBootTime">Last OS boot time (drives the uptime penalty); null if unknown.</param>
/// <param name="StoppedAutoServiceCount">Count of auto-start services that aren't running.</param>
/// <param name="RecentErrorEventCount">Count of Critical/Error events in the last 24h (System+Application).</param>
/// <param name="RebootPending">A reboot is pending (CBS / CCM).</param>
/// <param name="UserLoggedOn">An interactive user session is present.</param>
public sealed record MachineVitals(
    double? SystemDriveFreePercent = null,
    double? SystemDriveFreeGb = null,
    double? MemoryUsedPercent = null,
    double? CpuLoadPercent = null,
    DateTime? LastBootTime = null,
    int? StoppedAutoServiceCount = null,
    int? RecentErrorEventCount = null,
    bool? RebootPending = null,
    bool? UserLoggedOn = null)
{
    /// <summary>OS caption + build (e.g. "Windows Server 2016 Standard — 10.0.14393"); null if unread.
    /// Captured in the same CIM pull so the row's OS is known without a separate lazy query.</summary>
    public string? OperatingSystem { get; init; }

    /// <summary>Per-drive free space for every fixed disk (populated by the probe; empty if unread).</summary>
    public IReadOnlyList<DriveVitals> Drives { get; init; } = [];

    /// <summary>Display names of the auto-start services found stopped (capped by the probe).</summary>
    public IReadOnlyList<string> StoppedAutoServices { get; init; } = [];

    /// <summary>The most recent Critical/Error events (capped by the probe), newest first.</summary>
    public IReadOnlyList<EventDigest> RecentErrorEvents { get; init; } = [];

    /// <summary>User names of interactive sessions, when resolvable.</summary>
    public IReadOnlyList<string> LoggedOnUsers { get; init; } = [];

    /// <summary>Time since the last boot, or null when <see cref="LastBootTime"/> is unknown.</summary>
    public TimeSpan? Uptime => LastBootTime is { } boot ? DateTime.Now - boot : null;

    /// <summary>True when no signal at all was read — the probe round-tripped but came back blank.</summary>
    public bool IsEmpty =>
        SystemDriveFreePercent is null && MemoryUsedPercent is null && CpuLoadPercent is null
        && LastBootTime is null && StoppedAutoServiceCount is null && RecentErrorEventCount is null
        && RebootPending is null && Drives.Count == 0;
}

/// <summary>Free-space vitals for a single fixed drive.</summary>
/// <param name="Letter">Drive letter (e.g. "C:").</param>
/// <param name="FreePercent">Free space as a percentage (0-100).</param>
/// <param name="FreeGb">Free space in GB.</param>
/// <param name="SizeGb">Total drive size in GB.</param>
public sealed record DriveVitals(string Letter, double FreePercent, double FreeGb, double SizeGb);

/// <summary>A condensed Event Log entry for the triage view.</summary>
/// <param name="Time">When the event was logged.</param>
/// <param name="Log">Log name (e.g. "System").</param>
/// <param name="Provider">Event source / provider.</param>
/// <param name="Id">Event id.</param>
/// <param name="Level">Level name (e.g. "Error", "Critical").</param>
/// <param name="Message">First line of the event message.</param>
public sealed record EventDigest(
    DateTime? Time,
    string? Log,
    string? Provider,
    int Id,
    string? Level,
    string? Message);
