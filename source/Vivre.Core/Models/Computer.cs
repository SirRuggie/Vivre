using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Vivre.Core.Updates;
using Vivre.Core.Vitals;

namespace Vivre.Core.Models;

/// <summary>
/// A single machine shown in the computer grid. Observable so the WPF DataGrid
/// updates live as ping / health / update results arrive. Grid-row fields:
/// <list type="bullet">
///   <item>Name        ← ComputerName</item>
///   <item>IsOnline     ← OnlineStatus (legacy used an Int16 tri-state; a bool is
///                        enough for the grid today, revisit if "unknown" is needed)</item>
///   <item>SiteCode     ← SiteCode</item>
///   <item>AgentVersion ← AgentVersion</item>
///   <item>LastStatus   ← StatusMessage</item>
///   <item>LastError    ← ErrorMessage</item>
/// </list>
/// </summary>
public partial class Computer : ObservableObject
{
    public Computer()
    {
    }

    public Computer(string name) => Name = name;

    /// <summary>Host name of the SCCM client.</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>Reachability: null = not checked yet (grey), true = responded (green), false = offline (red).</summary>
    [ObservableProperty]
    public partial bool? IsOnline { get; set; }

    /// <summary>ConfigMgr site code (e.g. "PS1"), null until queried.</summary>
    [ObservableProperty]
    public partial string? SiteCode { get; set; }

    /// <summary>ConfigMgr client agent version, null until queried.</summary>
    [ObservableProperty]
    public partial string? AgentVersion { get; set; }

    /// <summary>OS caption + build (e.g. "Windows Server 2022 Standard — 10.0.20348"); filled by a
    /// health check or on-demand when the detail window opens. Null until known.</summary>
    [ObservableProperty]
    public partial string? OperatingSystem { get; set; }

    /// <summary>Most recent status message from an action or health check.</summary>
    [ObservableProperty]
    public partial string? LastStatus { get; set; }

    /// <summary>Most recent error message, null when the last operation succeeded.</summary>
    [ObservableProperty]
    public partial string? LastError { get; set; }

    /// <summary>
    /// Output of the most recent PowerShell/command run against this machine, shown in
    /// the grid's "Command result" column (full text on hover / in the script window).
    /// </summary>
    [ObservableProperty]
    public partial string? CommandResult { get; set; }

    // --- Software check (ad-hoc "is product X installed?" → a grid column; see SoftwareProbe) ---

    /// <summary>Result of the most recent software check (e.g. "CrowdStrike Windows Sensor 7.18" or
    /// "CrowdStrike — not found"); null until a check runs. Shown in the grid's Software column.</summary>
    [ObservableProperty]
    public partial string? SoftwareCheck { get; set; }

    /// <summary>Whether the last software check found the product: true = present, false = absent,
    /// null = not checked / errored. Drives the Software cell colour (green found / red missing).</summary>
    [ObservableProperty]
    public partial bool? SoftwareFound { get; set; }

    /// <summary>True when the product is installed but its checked service isn't running (or is missing)
    /// — the "present but not protecting" case. Overrides the green to amber in the Software column.</summary>
    [ObservableProperty]
    public partial bool? SoftwareServiceDown { get; set; }

    // Raw software-check fields behind the display string, kept off the observable surface (like Vitals)
    // — they back the on-demand "Export software report (CSV)" so the report has real per-column values.

    /// <summary>What the last software check searched for (e.g. "CrowdStrike"); null until a check runs.</summary>
    public string? SoftwareQuery { get; set; }

    /// <summary>Matched product display name from the last check (null if not found / not checked).</summary>
    public string? SoftwareName { get; set; }

    /// <summary>Matched product version from the last check (null if not found / unversioned).</summary>
    public string? SoftwareVersion { get; set; }

    /// <summary>Service the last check looked at, or null when no service was checked.</summary>
    public string? SoftwareServiceName { get; set; }

    /// <summary>Last check's service result: "Running" / "Stopped" / "not found", or null when none checked.</summary>
    public string? SoftwareServiceState { get; set; }

    /// <summary>When the last software check ran for this row (null = never).</summary>
    public DateTime? SoftwareCheckedAt { get; set; }

    /// <summary>Per-machine values for user-defined custom columns (column name → value). Bindable per key
    /// (<c>{Binding CustomValues[Name]}</c>) so a custom column's cell updates live as a sweep fills it.</summary>
    public CustomValueStore CustomValues { get; } = new();

    // Health signals (null = unknown/not checked). true = condition present.
    /// <summary>A reboot is pending.</summary>
    [ObservableProperty]
    public partial bool? RebootRequired { get; set; }

    // RebootRequired is the second input to the derived chip state — keep it live.
    partial void OnRebootRequiredChanged(bool? value)
    {
#if DEBUG
        AssertLiveFilteredWriteOnUi(nameof(RebootRequired));
#endif
        OnPropertyChanged(nameof(PatchState));
    }

    /// <summary>Required updates are missing.</summary>
    [ObservableProperty]
    public partial bool? MissingUpdates { get; set; }

    /// <summary>An install/update is in progress.</summary>
    [ObservableProperty]
    public partial bool? RunningUpdates { get; set; }

    /// <summary>An interactive user is logged on.</summary>
    [ObservableProperty]
    public partial bool? UserLoggedOn { get; set; }

    /// <summary>Last OS boot time, null if unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastRebootDisplay))]
    public partial DateTime? LastBootTime { get; set; }

    // --- Vitals & triage (deep OS health → a 0-100 "life force" score; see VitalityScorer) ---

    /// <summary>Free space on the system drive (%), null until vitals are read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VitalsSummary))]
    public partial double? SystemDriveFreePercent { get; set; }

    /// <summary>Physical memory in use (%), null until vitals are read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VitalsSummary))]
    public partial double? MemoryUsedPercent { get; set; }

    /// <summary>Instantaneous CPU load (%), null until vitals are read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VitalsSummary))]
    public partial double? CpuLoadPercent { get; set; }

    /// <summary>Count of auto-start services found stopped, null until vitals are read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VitalsSummary))]
    public partial int? StoppedAutoServiceCount { get; set; }

    /// <summary>When vitals were last read for this row (null = never).</summary>
    [ObservableProperty]
    public partial DateTime? VitalsCheckedAt { get; set; }

    /// <summary>The 0-100 vitality score shown in the grid's Vitals chip; null = unknown/offline.
    /// Set by the view model after running <see cref="VitalityScorer.Score"/>.</summary>
    [ObservableProperty]
    public partial int? VitalityScore { get; set; }

    /// <summary>The health band the score falls into; drives the Vitals chip colour. Null until scored.</summary>
    [ObservableProperty]
    public partial VitalityBand? VitalityBand { get; set; }

    /// <summary>The worst contributing factors behind the score (the "why"), for the triage panel.
    /// Observable so an open Machine Details panel re-resolves the "why" list when Check Vitals re-runs.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> VitalityReasons { get; set; } = [];

    /// <summary>
    /// True when the box has a non-runtime admin-attention finding (currently: WinRM/Kerberos
    /// degradation — the host was reached over SMB/DCOM because it rejected Kerberos). The band-floor
    /// in <see cref="VitalityScorer"/> guarantees a visual grid distinction (amber not green), but this
    /// property allows the UI to surface an additional badge/indicator on the row.
    /// </summary>
    [ObservableProperty]
    public partial bool VitalityNeedsAttention { get; set; }

    /// <summary>True when this row's vitals were read over the SMB/DCOM fallback because WinRM was
    /// unusable — drives the Machine Details "Connection" callout. Set in the view model's ApplyVitals.</summary>
    [ObservableProperty]
    public partial bool WinRmDegraded { get; set; }

    /// <summary>Short plain-English caption for the connection callout (what happened), or null when WinRM
    /// is healthy. From <see cref="WinRmHealthGuidance.Caption"/>.</summary>
    [ObservableProperty]
    public partial string? WinRmStateCaption { get; set; }

    /// <summary>The actual WinRM error that triggered the fallback (the "what"), or null. Surfaced in the
    /// connection callout's expandable details.</summary>
    [ObservableProperty]
    public partial string? WinRmFailureDetail { get; set; }

    /// <summary>The fix for the WinRM degradation, as scannable bullets (shown in the Connection
    /// callout), or null. From <see cref="WinRmHealthGuidance.FixBullets"/>.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string>? WinRmFix { get; set; }

    /// <summary>The parsed OS build (e.g. 14393 for Server 2016), or null until the OS has been read.
    /// Drives the self-populating 2016 panel + the LCU/WUA routing (<c>LcuRouting</c>). Null is deliberately
    /// "not 2016" — an unread box is never classified or routed into the 2016 lane.</summary>
    [ObservableProperty]
    public partial int? OsBuild { get; set; }

    /// <summary>True once the 2016 full-package lane has successfully STAGED this box this session (set at
    /// reboot-ready; cleared on a verified commit). It's the signal Verify uses to tell a genuine rollback
    /// from a box that was simply never staged — <see cref="RebootRequired"/> can't, because the health
    /// refresh, the reboot-pending probe, and any reboot-required scan all set it for unrelated reboots.
    /// Observable + data-bound: the "STAGED — needs Reboot Wave" tag (WorkspaceView.xaml) requires this
    /// true, so a box whose wave verified green (flag cleared) never re-shows the tag when a later
    /// Windows self-reboot re-lights <see cref="RebootRequired"/> via the monitor's reboot-pending probe.</summary>
    [ObservableProperty]
    public partial bool StagedThisSession { get; set; }

    /// <summary>True when this Server 2016 box needs the DISM full-package staging lane (WUA's Express-delta
    /// fails its monthly CU); false (default) means it patches via normal Windows Update like a 2019/2022 box.
    /// Observable so the grid + routing react live. Seeded from the persisted AppSettings.StagedHosts set by
    /// host name when the row is added; the operator toggles it (a later commit). Only meaningful for 2016 boxes.</summary>
    [ObservableProperty]
    public partial bool RequiresStagedPatching { get; set; }

    /// <summary>True once this 2016 box's CU has been confirmed committed this session — set by a Verified
    /// <c>VerifyLcu</c> result and by a successful 2016-lane Reboot Wave; cleared when the box is re-staged.
    /// Lets the staged-patching decision dialog skip a box whose CU is already done (it just needs its remaining
    /// minor updates via WUA, no Stage prompt). Distinct from <see cref="StagedThisSession"/> (staged-but-not-yet-
    /// committed) and from <see cref="RebootRequired"/> (set for unrelated pending reboots). Runtime-only, not
    /// persisted, not observable — only the install router and the planner read it.</summary>
    public bool LcuVerifiedThisSession { get; set; }

    /// <summary>Installed-update count from the most recent install attempt this session (0 if none).
    /// Written by InstallRowAsync; read later by the post-reboot outcome message. Runtime-only, not persisted, not observable.</summary>
    public int LastInstallInstalledCount { get; set; }

    /// <summary>Failed-update count from the most recent install attempt this session (0 if none).
    /// Written by InstallRowAsync; read later by the post-reboot outcome message. Runtime-only, not persisted, not observable.</summary>
    public int LastInstallFailedCount { get; set; }

    /// <summary>The full vitals snapshot behind the score — the Machine Details triage panel's per-drive /
    /// per-reading breakdown binds THROUGH this (e.g. <c>Vitals.Drives</c>, <c>Vitals.SystemDriveFreePercent</c>,
    /// <c>Vitals.LastBootTime</c>). Observable so a Check Vitals run WHILE Machine Details is open re-resolves
    /// those readings live: ApplyVitals replaces the whole snapshot, and the notification re-evaluates the
    /// bindings (previously it was a plain property, so the panel showed the old snapshot until reopened).
    /// Null until vitals are read. NOT in the grid's live-filtered set, so notifying here never re-shapes the
    /// grid CollectionView — and ApplyVitals runs on the UI thread.</summary>
    [ObservableProperty]
    public partial MachineVitals? Vitals { get; set; }

    /// <summary>One-line vitals digest for the chip tooltip / triage header
    /// (e.g. "Disk 4% · Mem 92% · CPU 30% · 3 stopped svc"); null until any vital is read.</summary>
    public string? VitalsSummary
    {
        get
        {
            var parts = new List<string>();
            if (SystemDriveFreePercent is { } disk) { parts.Add($"Disk {disk:0.#}% free"); }
            if (MemoryUsedPercent is { } mem) { parts.Add($"Mem {mem:0}%"); }
            if (CpuLoadPercent is { } cpu) { parts.Add($"CPU {cpu:0}%"); }
            if (StoppedAutoServiceCount is { } svc && svc > 0) { parts.Add($"{svc} stopped svc"); }
            return parts.Count > 0 ? string.Join(" · ", parts) : null;
        }
    }

    // --- Windows Update lane (the BatchPatch-replacement view; see docs/windows-patching-lane.md) ---

    /// <summary>Per-host update status for the "Windows update message" column (e.g. "Installing 3 of 8").</summary>
    [ObservableProperty]
    public partial string? UpdateMessage { get; set; }

    /// <summary>Reboot-action status for the "Reboot message" column (e.g. "Offline since 09:21 — waiting…",
    /// then "Back online 09:24 (down 3m) — reboot still pending"). Written by the monitor on online/offline
    /// transitions so a reboot is visibly tracked end-to-end.</summary>
    [ObservableProperty]
    public partial string? RebootMessage { get; set; }

    /// <summary>Monitor bookkeeping: when this row last transitioned online→offline, so the down-time can
    /// be reported when it returns. Null when online / never seen down. Not persisted, not bound.</summary>
    public DateTime? WentOfflineAt { get; set; }

    /// <summary>Live install progress 0-100 for the progress bar, null when indeterminate/idle.</summary>
    [ObservableProperty]
    public partial int? UpdateProgress { get; set; }

    /// <summary>Applicable-update count from the last scan, null until scanned.</summary>
    [ObservableProperty]
    public partial int? UpdatesAvailable { get; set; }

    /// <summary>Current patch phase name (Scanning/Installing/…); drives logging/state, not a column.</summary>
    [ObservableProperty]
    public partial string? UpdatePhase { get; set; }

    /// <summary>
    /// Glanceable display state for the Status chip / message color / fleet counts — a reduction of
    /// <see cref="UpdatePhase"/> combined with <see cref="RebootRequired"/>. Resolves the two
    /// awkward cases so the chip never lies: a just-installed row whose phase still says
    /// "PendingReboot" but whose reboot has since cleared reads <see cref="PatchState.Done"/> (the
    /// green "back online"), and a Done/Available/Idle row that has a reboot pending reads
    /// <see cref="PatchState.RebootPending"/> (amber). Recomputed when either input changes.
    /// </summary>
    public PatchState PatchState => DerivePatchState(UpdatePhase, RebootRequired);

    private static PatchState DerivePatchState(string? phase, bool? rebootRequired)
    {
        bool pending = rebootRequired == true;
        PatchPhase parsed = Enum.TryParse(phase, ignoreCase: true, out PatchPhase p) ? p : PatchPhase.Idle;

        return parsed switch
        {
            PatchPhase.Error => PatchState.Error,
            // Couldn't reach Windows Update (transient retries exhausted): a failure, so it reduces to
            // the red Error display-state (never green / "up to date") and is counted/filtered as a
            // failure everywhere. The distinct "Can't reach WU" chip label comes from the UpdatePhase
            // string in the grid template, mirroring how Staging/Cleaning carry their own labels.
            PatchPhase.Unreachable => PatchState.Error,
            // Deferred: the agent refused a Stage/Cleanup because a reboot is already pending. A deferral
            // means the box IS reboot-pending, so it reads amber RebootPending (never green / "staged") —
            // the distinct "Deferred" chip label + "reboot first" message come from the UpdatePhase/Message
            // strings, mirroring how Staging/Cleaning carry their own labels.
            PatchPhase.Deferred => PatchState.RebootPending,
            PatchPhase.Scanning => PatchState.Scanning,
            // The 2016 stage (DISM add-package) and component cleanup (DISM /StartComponentCleanup) are their
            // own phases shown as distinct chip labels ("Staging"/"Cleaning up"), but reduce to the same
            // Scanning display-state so colour/tally/Stop "working" logic is unchanged.
            PatchPhase.Staging or PatchPhase.Cleaning => PatchState.Scanning,
            // Cleaned: cleanup finished — green like Done, but with the distinct "Cleaned" chip label so it
            // doesn't falsely read "Up to date" (a cleanup does not prove update currency).
            PatchPhase.Cleaned => pending ? PatchState.RebootPending : PatchState.Done,
            PatchPhase.Downloading => PatchState.Downloading,
            PatchPhase.Installing => PatchState.Installing,
            PatchPhase.Uninstalling => PatchState.Uninstalling,
            // Reboot finished (flag cleared) ⇒ green "back online"; otherwise amber.
            PatchPhase.PendingReboot or PatchPhase.Rebooting => pending ? PatchState.RebootPending : PatchState.Done,
            // A scanned/finished/idle row still shows amber if a reboot is outstanding.
            PatchPhase.Done => pending ? PatchState.RebootPending : PatchState.Done,
            PatchPhase.Available => pending ? PatchState.RebootPending : PatchState.Available,
            _ => pending ? PatchState.RebootPending : PatchState.Idle,
        };
    }

    // Keep the derived chip state live when either input changes (generated by the toolkit).
    partial void OnUpdatePhaseChanged(string? value)
    {
#if DEBUG
        AssertLiveFilteredWriteOnUi(nameof(UpdatePhase));
#endif
        OnPropertyChanged(nameof(PatchState));
    }

#if DEBUG
    /// <summary>
    /// DEBUG-only hook the UI layer sets once (App.OnStartup) to "are we on the WPF Dispatcher thread?".
    /// <see cref="UpdatePhase"/> and <see cref="RebootRequired"/> feed the grid's live-filtered
    /// <see cref="PatchState"/>; writing either off the UI thread re-shapes the live CollectionView on the
    /// writing thread and throws "the calling thread cannot access this object". This assert makes such an
    /// off-thread write fail loudly HERE — naming the offending property — instead of as that opaque crash,
    /// so any future off-thread writer (a callback handed to a Core runner, a stray Task.Run, …) is caught.
    /// Null in non-UI hosts (unit tests) → the assert is a no-op. Vivre.Core stays UI-agnostic: the UI
    /// injects the check rather than Core taking a WPF dependency.
    /// </summary>
    public static Func<bool>? LiveFilteredWriteIsOnUiThread { get; set; }

    private static void AssertLiveFilteredWriteOnUi(string property) =>
        System.Diagnostics.Debug.Assert(
            LiveFilteredWriteIsOnUiThread?.Invoke() ?? true,
            $"Off-thread write to live-filtered property {property}: it re-shapes the grid's live " +
            "CollectionView on the writing thread and throws 'the calling thread cannot access this object'. " +
            "Marshal the write to the Dispatcher, or route it through the UI-built IProgress.");
#endif

    /// <summary>Last update-lane error for this row, null when the last operation succeeded.</summary>
    [ObservableProperty]
    public partial string? UpdateError { get; set; }

    /// <summary>
    /// True while an install/uninstall is actively running against this row (set for the whole
    /// streaming operation). Guards against a concurrent scan or a second install clobbering an
    /// in-flight row's progress — a scan skips a patching row, and an install/uninstall won't
    /// start on one already patching. Not persisted; runtime-only.
    /// </summary>
    [ObservableProperty]
    public partial bool IsPatching { get; set; }

    /// <summary>Queued scheduled-task action for the "Scheduled task action" column (Phase 2).</summary>
    [ObservableProperty]
    public partial string? ScheduledAction { get; set; }

    /// <summary>Next run time of the queued scheduled task for its column (Phase 2).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScheduled))]
    public partial DateTime? ScheduledNextRun { get; set; }

    /// <summary>True when a scheduled run has been queued for this row (<see cref="ScheduledNextRun"/> is set).
    /// Single source of truth for the Scheduled pill and chip so they always agree.</summary>
    public bool IsScheduled => ScheduledNextRun is not null;

    /// <summary>
    /// Applicable updates found by the last "Applicable"-scope scan, each with a keep/skip
    /// checkbox (<see cref="SelectableUpdate.IsSelected"/>). Per-scope so toggling the side-panel
    /// scope doesn't lose the data — the user scans once per scope and can swap between them
    /// freely. The collection instance is stable; only its contents change.
    /// </summary>
    public ObservableCollection<SelectableUpdate> ApplicableUpdates { get; } = [];

    /// <summary>Installed updates found by the last "Installed"-scope scan; same shape as
    /// <see cref="ApplicableUpdates"/> but feeds the uninstall flow.</summary>
    public ObservableCollection<SelectableUpdate> InstalledUpdates { get; } = [];

    /// <summary>
    /// Ticks every applicable update for install. Used by the reboot-and-verify post-reboot rescan so the
    /// updates it surfaces as "still applicable" are one-click-installable — the same readiness a fresh
    /// scan gives. (A fresh scan auto-ticks its finds; a rescan, via the scan path's preserve-prior
    /// selection, would otherwise inherit a just-completed install's untick on a re-found still-applicable
    /// update, leaving it surfaced-but-unchecked.) Selection only — touches <see cref="ApplicableUpdates"/>
    /// (never <see cref="InstalledUpdates"/>), and never installs or reboots.
    /// </summary>
    public void SelectAllApplicableUpdates()
    {
        foreach (SelectableUpdate update in ApplicableUpdates)
        {
            update.IsSelected = true;
        }
    }

    /// <summary>"N update(s) available" / "Up to date" message from the last Applicable scan;
    /// null until that scope has been scanned for this machine.</summary>
    [ObservableProperty]
    public partial string? ApplicableMessage { get; set; }

    /// <summary>"N installed update(s)" / "No installed updates" message from the last Installed
    /// scan; null until that scope has been scanned for this machine.</summary>
    [ObservableProperty]
    public partial string? InstalledMessage { get; set; }

    /// <summary>Count from the last Applicable-scope scan; null until scanned in that scope.</summary>
    [ObservableProperty]
    public partial int? ApplicableCount { get; set; }

    /// <summary>Count from the last Installed-scope scan; null until scanned in that scope.</summary>
    [ObservableProperty]
    public partial int? InstalledCount { get; set; }

    /// <summary>When the Applicable-scope scan last completed (null = never).</summary>
    [ObservableProperty]
    public partial DateTime? LastScannedApplicable { get; set; }

    /// <summary>
    /// Short honest note about the most recent install attempt — shown as a banner in the Updates
    /// panel on partial failure (e.g. "Install completed with 2 failure(s) — rescan after reboot for
    /// exact state"). Null when there is nothing to report. Runtime-only — never persisted.
    /// Cleared when a new Applicable-scope scan repopulates the checklist.
    /// </summary>
    [ObservableProperty]
    public partial string? LastInstallNote { get; set; }

    /// <summary>When the Installed-scope scan last completed (null = never).</summary>
    [ObservableProperty]
    public partial DateTime? LastScannedInstalled { get; set; }

    /// <summary>Relative "time since last reboot" (e.g. "3h", "2d") for the grid; exact value in the tooltip.</summary>
    public string? LastRebootDisplay => LastBootTime is { } boot ? Relative(DateTime.Now - boot) : null;

    /// <summary>
    /// Re-evaluates <see cref="LastRebootDisplay"/> (it's relative to <c>DateTime.Now</c>, so it
    /// drifts between health checks). Called on a timer by the shell so the grid stays current.
    /// </summary>
    public void RefreshRelativeTime()
    {
        // Skip rows with no boot time: LastRebootDisplay is a constant null then, so raising the
        // notification would only churn the binding engine for no visible change. (LastRebootDisplay is
        // NOT a live-filter input, so this never affects the grid's filtering — purely a display refresh.)
        if (LastBootTime is null)
        {
            return;
        }

        OnPropertyChanged(nameof(LastRebootDisplay));
    }

    private static string Relative(TimeSpan since)
    {
        if (since < TimeSpan.Zero)
        {
            return "0m";
        }

        return since.TotalDays >= 1 ? $"{(int)since.TotalDays}d"
            : since.TotalHours >= 1 ? $"{(int)since.TotalHours}h"
            : since.TotalMinutes >= 1 ? $"{(int)since.TotalMinutes}m"
            : "just now";
    }
}
