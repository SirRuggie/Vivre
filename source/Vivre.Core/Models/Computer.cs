using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Vivre.Core.Updates;

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

    // Health signals (null = unknown/not checked). true = condition present.
    /// <summary>A reboot is pending.</summary>
    [ObservableProperty]
    public partial bool? RebootRequired { get; set; }

    // RebootRequired is the second input to the derived chip state — keep it live.
    partial void OnRebootRequiredChanged(bool? value) => OnPropertyChanged(nameof(PatchState));

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

    // --- Windows Update lane (the BatchPatch-replacement view; see UPDATE_PLAN.md) ---

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
            PatchPhase.Scanning => PatchState.Scanning,
            PatchPhase.Downloading => PatchState.Downloading,
            PatchPhase.Installing => PatchState.Installing,
            // Reboot finished (flag cleared) ⇒ green "back online"; otherwise amber.
            PatchPhase.PendingReboot or PatchPhase.Rebooting => pending ? PatchState.RebootPending : PatchState.Done,
            // A scanned/finished/idle row still shows amber if a reboot is outstanding.
            PatchPhase.Done => pending ? PatchState.RebootPending : PatchState.Done,
            PatchPhase.Available => pending ? PatchState.RebootPending : PatchState.Available,
            _ => pending ? PatchState.RebootPending : PatchState.Idle,
        };
    }

    // Keep the derived chip state live when either input changes (generated by the toolkit).
    partial void OnUpdatePhaseChanged(string? value) => OnPropertyChanged(nameof(PatchState));

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
    public partial DateTime? ScheduledNextRun { get; set; }

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

    /// <summary>When the Installed-scope scan last completed (null = never).</summary>
    [ObservableProperty]
    public partial DateTime? LastScannedInstalled { get; set; }

    /// <summary>Relative "time since last reboot" (e.g. "3h", "2d") for the grid; exact value in the tooltip.</summary>
    public string? LastRebootDisplay => LastBootTime is { } boot ? Relative(DateTime.Now - boot) : null;

    /// <summary>
    /// Re-evaluates <see cref="LastRebootDisplay"/> (it's relative to <c>DateTime.Now</c>, so it
    /// drifts between health checks). Called on a timer by the shell so the grid stays current.
    /// </summary>
    public void RefreshRelativeTime() => OnPropertyChanged(nameof(LastRebootDisplay));

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
