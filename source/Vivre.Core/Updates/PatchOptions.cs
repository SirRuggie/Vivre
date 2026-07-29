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

    // NOTE: there is deliberately no auto-reboot option. Installs report reboot-required and stop; the
    // reboot is always a separate, explicit operator action (a confirmed Reboot/Reboot Wave, or an
    // operator-created scheduled task). Nothing here ever causes a box to reboot on its own.

    /// <summary>Cap on how many hosts install at once. Each install holds a persistent streaming
    /// WinRM session (mostly idle-waiting once open) plus the target downloading/installing as
    /// SYSTEM. The practical governor is update-download bandwidth (N hosts pulling cumulative
    /// updates at once), not the client — raise it on a fast LAN with WSUS/SCCM + Delivery
    /// Optimization peering, lower it over a slow WAN. Scan has its own (much higher) cap.
    /// Default 50; operator-tunable in Settings → "Max simultaneous installs".</summary>
    public int MaxConcurrentHosts { get; set; } = 50;

    /// <summary>App-wide shared budget for all remote READ operations: vitals probes, health checks,
    /// update scans, software checks, and custom-column sweeps — across ALL open tabs. This is a
    /// different concern from <see cref="MaxConcurrentHosts"/> (which throttles heavy install/uninstall
    /// SYSTEM-task operations and is per-tab). Default 32 matches the historical hard-coded cap in
    /// WorkspaceViewModel so tuning is unchanged until explicitly raised. Raise it on a fast LAN with
    /// many machines; lower it over a slow WAN or when targets are under heavy load.</summary>
    public int MaxConcurrentScans { get; set; } = 32;

    /// <summary>Give up on a single host after this long (a hung box must not block the grid).</summary>
    public TimeSpan PerHostTimeout { get; set; } = TimeSpan.FromHours(3);

    /// <summary>How long the install poll loop tolerates no progress-file change before flagging a stuck host.</summary>
    public TimeSpan StuckThreshold { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long to tolerate <em>complete silence</em> from the target mid-install — no progress
    /// AND no heartbeat — before declaring the session dead/hung. The controller heartbeats every
    /// ~15s while the session is alive (even during a slow download), so this only trips when the
    /// connection is genuinely gone; it never false-positives on a slow-but-working update. Default
    /// 90s = ~6 missed heartbeats.
    /// </summary>
    public TimeSpan NoResponseTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>How often the controller polls the target's progress JSON.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Whether a SCAN may fall back to the SMB/SCM agent lane when WinRM is lost for a NON-Kerberos reason
    /// (the box unreachable over WinRM, the service not up yet, or a mid-scan drop).
    /// <para><b>DEFAULT TRUE, deliberately.</b> The SMB lane is the only way to scan a Kerberos-broken box, so
    /// a caller that FORGETS this flag must keep the fallback. The failure polarity is one-way on purpose: a
    /// forgotten flag re-opens an EDR-noise hole (a temporary remote <c>Vivre_WUA_*</c> service is created),
    /// which is recoverable; silently stripping a cohort's only scan transport is not.</para>
    /// <para><b>Only the post-reboot rescan sets this false</b>, and only on its non-final attempts
    /// (<c>WorkspaceViewModel.ReportPostRebootOutcomeAsync</c>): that path fires the instant TCP 445 answers,
    /// i.e. by construction BEFORE WinRM is listening, so early attempts were dropping an agent EXE and creating
    /// a service on healthy boxes purely because WinRM had not finished starting. The FINAL attempt leaves it
    /// true, so every cohort — including a Kerberos-broken box with no cache entry yet — still gets its rescue
    /// and the row still resolves. Operator-initiated scans, install, uninstall and 2016 Stage are unaffected:
    /// their fallback is unconditional and this flag never reaches them.</para>
    /// <para>The Kerberos catch is NOT gated by this — a host already known to reject Kerberos falls back on
    /// attempt 1, verifies immediately, and creates exactly one service. That is the intended outcome.</para>
    /// </summary>
    public bool AllowSmbScanFallback { get; set; } = true;

    /// <summary>
    /// A shallow copy, so a per-host install scope (<see cref="IncludeKbArticleIds"/>) can be set
    /// without mutating the shared session options that concurrent hosts read. The list properties
    /// are treated as immutable (always reassigned, never mutated), so a shallow copy is safe.
    /// </summary>
    public PatchOptions Clone() => (PatchOptions)MemberwiseClone();
}
