namespace Vivre.Core.Updates;

/// <summary>Which set of updates the scan/install lane targets — applicable updates (the install
/// flow) or already-installed updates (the uninstall flow).</summary>
public enum UpdateScope
{
    /// <summary>Updates that are applicable but not installed yet (default; the install flow).</summary>
    Applicable,

    /// <summary>Already-installed updates, surfaced so the user can uninstall a selected subset.</summary>
    Installed,
}

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
    /// Whether the scan/install lane targets applicable updates (default — the install flow) or
    /// already-installed updates (the uninstall flow). Side-panel toggle drives this on the VM.
    /// </summary>
    public UpdateScope Scope { get; set; } = UpdateScope.Applicable;

    /// <summary>
    /// Case-insensitive substrings; any update whose title contains one is skipped
    /// (e.g. "SQL", "Silverlight"). Empty = install everything applicable.
    /// </summary>
    public IReadOnlyList<string> ExcludeNameContains { get; set; } = [];

    /// <summary>
    /// Whether the WUA search returns driver updates. Default <c>false</c> matches the Windows
    /// Update UI and BatchPatch — drivers are filtered out at the search (<c>Type='Software'</c>),
    /// so a scan/install only sees software updates. Turn on to also scan/install drivers from the
    /// chosen source.
    /// </summary>
    public bool IncludeDrivers { get; set; }

    /// <summary>
    /// When non-null, restricts install to updates whose KB article id is in this list (the
    /// per-machine checklist's ticked updates). <c>null</c> ⇒ install everything applicable
    /// (still minus <see cref="ExcludeNameContains"/>); an <em>empty</em> list ⇒ nothing selected.
    /// Per-host — set on a <see cref="Clone"/>, never on the shared instance.
    /// </summary>
    public IReadOnlyList<string>? IncludeKbArticleIds { get; set; }

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

    /// <summary>
    /// A shallow copy, so a per-host install scope (<see cref="IncludeKbArticleIds"/>) can be set
    /// without mutating the shared session options that concurrent hosts read. The list properties
    /// are treated as immutable (always reassigned, never mutated), so a shallow copy is safe.
    /// </summary>
    public PatchOptions Clone() => (PatchOptions)MemberwiseClone();
}
