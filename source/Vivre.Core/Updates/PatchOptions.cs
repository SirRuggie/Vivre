namespace Vivre.Core.Updates;

/// <summary>When the install should run.</summary>
public enum RunBehavior
{
    /// <summary>Register the SYSTEM task and start it immediately.</summary>
    InstallNow,

    /// <summary>Register the SYSTEM task with a one-time trigger at <see cref="PatchOptions.ScheduleAt"/>.</summary>
    ScheduleAt,
}

/// <summary>What to do once the install finishes.</summary>
public enum RebootBehavior
{
    /// <summary>Install only; report "reboot required" and leave the box to be rebooted manually (default).</summary>
    ReportOnly,

    /// <summary>Reboot the target after install and wait for it to come back online (Phase 2).</summary>
    RebootAndWait,
}

/// <summary>
/// The shared, session-only patch settings (mirrors the session-only credential model —
/// held in memory, not persisted). Steers scope via <see cref="Source"/> +
/// <see cref="ExcludeNameContains"/> rather than per-KB selection.
/// </summary>
public sealed class PatchOptions
{
    /// <summary>Which update catalogue to scan/install from.</summary>
    public UpdateSource Source { get; set; } = UpdateSource.WindowsUpdate;

    /// <summary>
    /// Case-insensitive substrings; any update whose title contains one is skipped
    /// (e.g. "SQL", "Silverlight"). Empty = install everything applicable.
    /// </summary>
    public IReadOnlyList<string> ExcludeNameContains { get; set; } = [];

    /// <summary>Run now or at a scheduled time.</summary>
    public RunBehavior RunBehavior { get; set; } = RunBehavior.InstallNow;

    /// <summary>The trigger time when <see cref="RunBehavior"/> is <see cref="RunBehavior.ScheduleAt"/>.</summary>
    public DateTime? ScheduleAt { get; set; }

    /// <summary>Whether to reboot + wait after install.</summary>
    public RebootBehavior RebootBehavior { get; set; } = RebootBehavior.ReportOnly;

    /// <summary>Cap on how many hosts install at once (installs are heavy — keep below the ping sweep).</summary>
    public int MaxConcurrentHosts { get; set; } = 4;

    /// <summary>Give up on a single host after this long (a hung box must not block the grid).</summary>
    public TimeSpan PerHostTimeout { get; set; } = TimeSpan.FromHours(3);

    /// <summary>How long the install poll loop tolerates no progress-file change before flagging a stuck host.</summary>
    public TimeSpan StuckThreshold { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How often the controller polls the target's progress JSON.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);
}
