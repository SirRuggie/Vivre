using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Management.Automation;
using System.Text;
using System.Threading;
using System.Windows.Data;
using System.Windows.Threading;
using Vivre.Core.Columns;
using Vivre.Core.Computers;
using Vivre.Core.Credentials;
using Vivre.Core.Deploy;
using Vivre.Core.Logging;
using Vivre.Core.Models;
using Vivre.Core.Net;
using Vivre.Core.PowerShell;
using Vivre.Core.Remoting;
using Vivre.Core.Sccm;
using Vivre.Core.Scripts;
using Vivre.Core.Software;
using Vivre.Core.Updates;
using Vivre.Core.Remediation;
using Vivre.Core.Threading;
using Vivre.Core.Vitals;
using Vivre.Core.Wug;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Vivre.Desktop.ViewModels;

/// <summary>M9: severity of a completed operation — computed from real counts, not string-sniffing.</summary>
public enum OperationSeverity
{
    /// <summary>All rows succeeded or scan/schedule operations with no errors.</summary>
    Success,

    /// <summary>Some rows succeeded, some failed.</summary>
    Warning,

    /// <summary>All rows failed, or a critical error condition.</summary>
    Error,
}

/// <summary>The grid's quick state filter (paired with the name search). Drives which rows show.</summary>
public enum RowFilter
{
    /// <summary>Everything (default — no state filtering).</summary>
    All,

    /// <summary>Rows with updates available to install.</summary>
    UpdatesAvailable,

    /// <summary>Rows with a pending reboot.</summary>
    RebootPending,

    /// <summary>Rows whose last operation errored.</summary>
    Errors,

    /// <summary>Rows that are offline.</summary>
    Offline,

    /// <summary>Rows that finished cleanly (Done).</summary>
    Done,

    /// <summary>Rows whose vitality is Warning / Critical / Offline (the sick ones, for triage).</summary>
    Unhealthy,

    /// <summary>Confirmed Server 2016 (build 14393) rows — the self-populating view that drives the
    /// full-package CU lane. Unread boxes (no OS build yet) are excluded until a vitals check confirms them.</summary>
    Server2016,

    /// <summary>Rows that have not yet been scanned in this session — <see cref="Computer.UpdatePhase"/> is null.</summary>
    NotScanned,

    /// <summary>Rows with a scheduled update run queued — <see cref="Computer.ScheduledNextRun"/> is set.</summary>
    Scheduled,
}

/// <summary>
/// What the Stage flow shows the operator when this month's 2016 CU <c>.msu</c> isn't ready in the package
/// folder — the guided "here's what's missing and how to fix it" payload for the package-needed dialog.
/// </summary>
/// <param name="Ready">True when the correct package is present (Stage can proceed).</param>
/// <param name="Kb">The KB to download, e.g. "KB5094122".</param>
/// <param name="Arch">Architecture token, e.g. "x64".</param>
/// <param name="Folder">The full folder path the .msu must go in (openable / copy-pasteable).</param>
/// <param name="CatalogUrl">Microsoft Update Catalog search URL pre-filled to the KB.</param>
/// <param name="Problem">Plain-language reason the package isn't ready (missing / wrong / ambiguous).</param>
public sealed record LcuStageReadiness(
    bool Ready, string Kb, string Arch, string Folder, string CatalogUrl, string Problem);

/// <summary>
/// One independent workspace = one tab: its own computer list, selection, and
/// operations (which can run concurrently with other tabs). Owns the grid and the
/// Ping/Check sweeps. Remote operations use the credential from <see cref="Credentials"/>
/// (app-wide, shared across tabs). Created per tab by <see cref="ShellViewModel"/>.
/// </summary>
public partial class WorkspaceViewModel : ObservableObject, ITabViewModel, IDisposable
{
    private const int PingTimeoutMs = 2000;
    private const int MonitorIntervalSeconds = 20;

    private readonly IHostPinger _pinger;
    private readonly IHostProbe _hostProbe;
    private readonly IConfigMgrClient _configMgr;
    private readonly IWinRmEnabler _winRm;
    private readonly CredentialStore _credentials;
    private readonly IComputerListStore _lists;
    private readonly IActivityLog _activity;
    private readonly IScriptLibrary _scripts;
    private readonly IPatchService _patch;
    private readonly PatchOptions _patchOptions;
    private readonly IHostRebootProbe _rebootProbe;
    private readonly IPowerShellHost _powerShell;
    private readonly IVitalsProbe _vitals;
    private readonly IRemediationService _remediation;
    private readonly IDeploymentService _deployment;
    private readonly ISoftwareProbe _software;
    private readonly ICustomColumnProbe _customColumns;
    private readonly ICatalogSizeService _catalogSize;
    private readonly OrphanRebootServiceReaper _reaper;
    private readonly AppSettingsStore _appSettings = new();

    /// <summary>User-defined custom columns (machine mode), loaded from settings; the view builds a grid
    /// column per entry and the CSV export appends them. Mutated via Add/Remove which persist.</summary>
    public ObservableCollection<CustomColumnSpec> CustomColumns { get; } = [];

    /// <summary>Built-in machine-grid column headers the user has hidden (Name is never hideable); the
    /// view applies these to the grid. Mutated via SetColumnHidden which persists.</summary>
    public ObservableCollection<string> HiddenColumns { get; } = [];
    // Vitals + scans are read-only remote pulls; they share ONE app-wide budget (like the reboot probe
    // below) so opening more tabs never multiplies concurrent WinRM connections — every tab draws from
    // this shared budget and they interleave fairly, keeping this PC and the targets from being flooded.
    // The per-host timeout is started AFTER a slot is acquired, so time spent queued never counts.
    //
    // The budget is split via SplitThrottle to guarantee passive custom-column fills are never starved by
    // a saturated vitals/scan sweep (FIFO starvation: on a 319-row paste all 319 vitals tasks enqueue on
    // a single semaphore first; the 319 column waiters land behind them and wait ~2 minutes).
    //   • Total cap = MaxConcurrentScans (32 default) — UNCHANGED from the single-semaphore baseline.
    //   • Active pool = total − reserve: used by all registered sweeps (vitals, health, scan, software).
    //   • Reserved pool = Math.Min(4, total − 1): guaranteed for passive fills; active sweeps may NEVER
    //     touch it.  Passive fills may ALSO borrow from the active pool when it has room (idle-system
    //     column fill still runs at full width).  Worst-case in-flight = (total−reserve) + reserve = total.
    //     Two passive users share the reserve: custom-column fills AND user-fired ConfigMgr client actions
    //     — both short-lived and must not be starved behind a saturated sweep's FIFO queue.
    // Sized on first construction (see ctor). No lock needed: WorkspaceViewModel is constructed only on
    // the UI thread, so first-tab-wins is deterministic.
    private static SplitThrottle? _remoteSweepThrottleBacking;
    private static SplitThrottle _remoteSweepThrottle => _remoteSweepThrottleBacking!;
    private const int VitalsPerHostTimeoutSeconds = 120;
    private const int HealthPerHostTimeoutSeconds = 60;
    private const int SoftwarePerHostTimeoutSeconds = 60;
    // Bounds one client-action trigger inside RunRemoteAsync: the per-host WinRM gate wait
    // (a client action can queue behind an in-flight probe on the same box), the 20s connect,
    // and the Invoke-CimMethod call. Only the app-wide _remoteSweepThrottle queue is excluded
    // (the CTS starts after that acquire). 60s matches HealthPerHostTimeoutSeconds.
    private const int ClientActionPerHostTimeoutSeconds = 60;
    // Bounds the monitor's per-host reboot-pending probe: its CCM DetermineIfRebootPending WMI leg can
    // hang forever on a broken client, and an unbounded await froze the whole monitor pass (HIGH-2).
    // 120s matches vitals; the 60s health check already runs a superset of this probe's CCM work, so
    // this is pure headroom against false-degrading a slow-but-healthy box.
    private const int RebootProbeTimeoutSeconds = 120;
    // A WUA scan is a read-only WUA search; this is the PER-ATTEMPT wall-clock cap (5 min). Each retry
    // attempt inside ScanRowAsync gets its OWN fresh budget (NOT one budget shared across all attempts +
    // backoffs — that shared form would kill attempt 2 before attempt 3 ran), so a slow attempt is bounded
    // independently and a per-attempt timeout becomes a transient (retry), resolving to the honest "Can't
    // reach WU" state only if EVERY attempt times out. Sized generously because a search on a badly-behind
    // box legitimately takes minutes (90s false-timed-out real scans). The bounded retry count caps total
    // wall-clock; the scan sweep's own per-host timeout is left at the default as a loose final backstop.
    private const int ScanAttemptTimeoutSeconds = 300;
    // Shared across tabs so a many-machine fleet can't flood WinRM with reboot probes at once.
    private static readonly SemaphoreSlim _rebootProbeThrottle = new(8);
    // Caps the parallel Enable WinRM fan-out (DCOM, its own channel — no shared gate with the probes).
    // 8 mirrors the reboot-probe cap: Enable WinRM targets sick boxes, and hammering a degraded target
    // with a wide burst makes it worse; worst case for an all-hung selection is ceil(N/8) x ~25s.
    private static readonly SemaphoreSlim _enableWinRmThrottle = new(8);
    // Caps the monitor's per-pass reachability fan-out. The monitor pings (and DCOM-probes) every row
    // every MonitorIntervalSeconds; a 300-box list would otherwise launch 300 concurrent probes at once,
    // and N open tabs would multiply that. Shared across tabs (like _rebootProbeThrottle) so the whole app
    // stays bounded; 32 is wide enough that a single tab still sweeps quickly. The reboot-pending probe
    // keeps its own separate, smaller cap above.
    private static readonly SemaphoreSlim _monitorThrottle = new(32);
    // Hosts whose WinRM/PSRP shell init is failing (RemoteShellInitException — pending reboot or
    // MaxShellsPerUser). Value = the next time we'll RE-TEST it: we back off from probing every 20s
    // (hammering a degraded box makes it worse) but still retry every few minutes so we notice when
    // it recovers (a successful probe clears the flag immediately). Concurrent: probes run up to
    // _rebootProbeThrottle-wide.
    private readonly ConcurrentDictionary<string, DateTime> _degradedHosts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan DegradedRetryInterval = TimeSpan.FromMinutes(5);
    // Hosts whose WinRM reboot probe is permanently unsupported this session because they reject Kerberos
    // (0x80090322 — the http SPN belongs to the SSRS service account by design, never recovers). Unlike
    // _degradedHosts these are NOT retried: re-probing only spams the log every cycle, and their reboot
    // state is handled by the 2016 lane's DCOM Verify. (Value byte is unused — it's a concurrent set.)
    private readonly ConcurrentDictionary<string, byte> _winRmRebootProbeUnsupported = new(StringComparer.OrdinalIgnoreCase);
    // After a host comes back online we re-probe its reboot state a few times: a just-booted box
    // transiently still reports reboot-pending, so a single probe could catch that and strand the
    // amber dot forever (once RebootRequired is true we otherwise stop probing).
    private readonly ConcurrentDictionary<string, int> _rebootRecheckBudget = new(StringComparer.OrdinalIgnoreCase);
    private const int PostBootRebootRechecks = 5;
    // Last time we ran a reboot-pending probe for a host (any reason). The reboot-pending probe runs on a
    // single slow cadence (RebootPendingRecheckInterval) for ALL boxes — pending or not — rather than on
    // every 20s monitor pass, so a full online fleet doesn't churn a fresh WinRM shell per row each pass
    // (heavy shell churn that can poison a degraded target). The 20s loop now does only the cheap
    // online/offline ping; this stamp gates the reboot probe. Read-only (a registry/SCCM marker read); no reboot.
    private readonly ConcurrentDictionary<string, DateTime> _lastRebootProbeAt = new(StringComparer.OrdinalIgnoreCase);
    // The unified reboot-pending probe cadence: every box (pending or not) is re-probed at most this often.
    // A box known reboot-pending self-clears its amber pill on a later poll if it rebooted out-of-band; a
    // not-pending box notices a newly-pending state — both at this slow rate, never every 20s.
    private static readonly TimeSpan RebootPendingRecheckInterval = TimeSpan.FromMinutes(5);
    // How many times in a row a box has failed its cheap reachability probe (ping/DCOM) in the monitor.
    // Cleared the moment a probe succeeds. Drives the offline-confirmation below.
    private readonly ConcurrentDictionary<string, int> _consecutiveProbeFailures = new(StringComparer.OrdinalIgnoreCase);
    // Consecutive failed reachability probes required before a previously-online box is declared offline —
    // kills the false "Went offline → Back online" blips from a single dropped ping/busy WMI under load.
    private const int OfflineConfirmThreshold = 2;
    // The post-reboot rescan runs on a box that JUST rebooted (boot-time-confirmed) but may still be
    // settling; a transient unreachable mid-settle gets a short wait + retry rather than a stuck red Error.
    private static readonly TimeSpan PostRebootRescanRetryDelay = TimeSpan.FromSeconds(20);
    private const int PostRebootRescanAttempts = 3;
    // Machines with a pending scheduled task — install or reboot (name → trigger time). Drives the
    // "Scheduled task" columns and lets the monitor clear them once the time has passed (client-side).
    private readonly ConcurrentDictionary<string, DateTime> _scheduledTasks = new(StringComparer.OrdinalIgnoreCase);
    // All operations currently running in this tab (Ping/Check/Scan/Install/Uninstall sweeps).
    // Tracked as a set rather than a single field so independent operations can overlap — Stop
    // cancels them all, and IsBusy stays true until the last one finishes. This is what lets you
    // add + scan a new machine while another machine is mid-install.
    // INVARIANT: UI-thread-only. BeginOperation/EndOperation/Stop are reached from [RelayCommand]
    // handlers whose sweeps await WITHOUT ConfigureAwait(false), so their continuations resume on
    // the UI context — this plain List needs no locking only because of that. If you ever add
    // ConfigureAwait(false) to a sweep's own awaits, switch this to a ConcurrentDictionary/lock.
    private readonly List<CancellationTokenSource> _activeCts = [];
    // Install/uninstall throttle (heavy SYSTEM-task operations; per-tab). Non-readonly so
    // CurrentInstallThrottle() can swap it when the operator changes "Max simultaneous installs"
    // in Settings. In-flight sweeps capture the old semaphore reference at sweep-start and keep
    // using it; only sweeps started after the change use the new cap.
    private SemaphoreSlim _patchThrottle;
    private int _patchThrottleCap;
    private static int ClampInstallCap(int v) => Math.Clamp(v, 1, 200);

    // Silent transient-retry budget for a WUA scan/install (see TransientRetryRunner + TransientWuaError).
    // 3 retries × ~60s: each failing WUA attempt already burns ~2.5 min on Windows' own internal retries,
    // so 3 attempts + the 60s pauses ≈ 9-10 min of coverage — comfortably past a typical transient blip
    // (the proven one was 2m38s), while the 60s pause also spaces out codes that fail fast. Modest on
    // purpose: re-running a box is cheap, so a longer outage surfaces honestly rather than waiting forever.
    private const int MaxTransientRetries = 3;
    private static readonly TimeSpan TransientRetryBackoff = TimeSpan.FromSeconds(60);

    // An SLS outage fails every box at once, so a FIXED backoff makes the whole fleet retry in lockstep and
    // hammer the recovering service. Spread them with up to 15s of random jitter (same rationale + pattern
    // as the reboot-trigger jitter — Random.Shared.Next). Per-attempt, so the spread re-rolls each round.
    private const int TransientRetryJitterMs = 15_000;

    /// <summary>The inter-attempt backoff (60s) plus up to 15s of random jitter, so a fleet-wide SLS outage
    /// doesn't retry in lockstep against the recovering service. Used by both the scan and install retry.</summary>
    private static Task TransientBackoffDelayAsync(int retryNumber, CancellationToken token) =>
        Task.Delay(TransientRetryBackoff + TimeSpan.FromMilliseconds(Random.Shared.Next(TransientRetryJitterMs + 1)), token);

    // Reboot-and-verify wave throttles. Two separate concerns:
    //   _waveThrottle: concurrency width for the watch loop itself — effectively unbounded (256)
    //     so ALL selected boxes start their offline watch simultaneously; a slow box (e.g. 45-min
    //     Server 2016 commit) NEVER blocks a fast box from completing its verify/report.
    //   _rebootTriggerThrottle: concurrency width only around the instant the reboot is ISSUED —
    //     a small burst cap that staggers reboot commands to protect DCs/DNS/auth services from
    //     having too many boxes drop off the network at exactly the same moment.
    // Shared across tabs (static) so a multi-tab fleet scenario doesn't multiply the burst.
    private static readonly SemaphoreSlim _waveThrottle = new(256);
    private const int MaxConcurrentRebootIssue = 12;
    private static readonly SemaphoreSlim _rebootTriggerThrottle = new(MaxConcurrentRebootIssue);

    private CancellationTokenSource? _monitorCts;

    // --- Row-disjoint concurrency registry ---
    // Each active operation registers the set of rows it holds. UI-thread-only (same invariant as
    // _activeCts above). A row held by any registered operation cannot be targeted by a new one;
    // it is skipped at launch and gets a persistent skip message in the appropriate column.
    // The registry is keyed by machine name (case-insensitive); value = the holding operation record.
    private readonly Dictionary<string, OperationRecord> _heldRows =
        new(StringComparer.OrdinalIgnoreCase);

    // Per-operation narration record (replaces the old scalar _sweepLabel/_sweepTotal/_sweepCompleted/_sweepStopwatch).
    private sealed class OperationRecord
    {
        internal string Label { get; }          // e.g. "Checking vitals"
        internal int Total { get; }             // row count at launch (after eligibility filter)
        internal int _completed;                // rows finished (Interlocked)
        internal int Completed => Volatile.Read(ref _completed);
        internal readonly Stopwatch Stopwatch = Stopwatch.StartNew();
        internal OperationRecord(string label, int total) { Label = label; Total = total; }
    }
    // Active operation records (parallel to _activeCts — same index).
    private readonly List<OperationRecord> _activeOps = [];

    private DispatcherTimer? _sweepNarrationTimer;

    // 3-second hold-open latch for M11 fleet band: keeps the band visible briefly after completion.
    private DispatcherTimer? _fleetBandHoldTimer;
    private bool _fleetBandHeld;   // true while hold-open is active (prevents premature collapse)

    // Coalesces the whole-fleet summary recompute. A row-state change (e.g. a vitals/scan result) or a
    // collection change marks the tallies dirty (O(1)) instead of re-walking all N rows on the spot; a
    // UI-thread timer recomputes once per ~200 ms window while dirty, then goes idle. This is what stops the
    // O(N^2) recompute storm an N-machine auto-check-on-load sweep used to cause (319 boxes → 20-30 s freeze).
    // Per-row bindings (the machine's own dot/score/status) update IMMEDIATELY, independent of this.
    private bool _fleetDirty;
    private DispatcherTimer? _fleetRecomputeTimer;
    private static readonly TimeSpan FleetRecomputeWindow = TimeSpan.FromMilliseconds(200);

    // The grid's default view, with a live filter (name search + state). Both mode grids bind
    // Computers, so they share this view — filtering once affects whichever grid is showing.
    private readonly ICollectionView _computersView;

    /// <summary>Tab title (editable — double-click the tab header to rename).</summary>
    [ObservableProperty]
    public partial string Title { get; set; } = "New tab";

    /// <summary>Machine workspaces are always closeable (<see cref="ITabViewModel"/>).</summary>
    public bool CanClose => true;

    /// <summary>Rows shown in the computer grid.</summary>
    public ObservableCollection<Computer> Computers { get; } = [];

    /// <summary>Rows the user has selected (kept in sync from the grid's selection).</summary>
    public ObservableCollection<Computer> SelectedComputers { get; } = [];

    /// <summary>"N selected" for the status bar, or empty when nothing is selected. The main
    /// guardrail for selection-scoped actions (Delete / Scan / Install selected).</summary>
    public string SelectionSummary => SelectedComputers.Count > 0 ? $"{SelectedComputers.Count} selected" : string.Empty;

    /// <summary>True when at least one row is selected (drives the status-bar selection indicator).</summary>
    public bool HasSelection => SelectedComputers.Count > 0;

    /// <summary>Live selected-row count for the contextual command bar label (e.g. "3 machines selected").</summary>
    public int SelectedComputerCount => SelectedComputers.Count;

    /// <summary>Enabled while at least one selected row is free (not held by any running operation).
    /// Gates the selection-cluster Scan (N) / Install (N) buttons. The click path re-filters via the
    /// registry, so any staleness between this re-evaluation and the click is harmless.</summary>
    public bool CanActOnSelection => HasSelection && SelectedComputers.Any(c => !_heldRows.ContainsKey(c.Name));

    /// <summary>Raised by <see cref="RequestClearSelection"/> so the active <c>WorkspaceView</c> can call
    /// <c>UnselectAll()</c> on both its DataGrids. The VM itself does NOT hold a reference to the grids —
    /// the view subscribes in <c>OnViewLoaded</c> and unsubscribes in <c>OnViewUnloaded</c>.</summary>
    public event Action? ClearSelectionRequested;

    /// <summary>Called by MainWindow's command-bar Clear button to deselect all rows in the active grids.
    /// Raises <see cref="ClearSelectionRequested"/>; the subscribed WorkspaceView handles the actual UnselectAll.</summary>
    public void RequestClearSelection() => ClearSelectionRequested?.Invoke();

    /// <summary>Live "Online: (online/total)" summary for this tab, shown in the bottom status bar.
    /// Recomputed whenever a row's online state changes or the list grows/shrinks.</summary>
    public string OnlineSummary => $"Online: ({Computers.Count(c => c.IsOnline == true)}/{Computers.Count})";

    /// <summary>Names of the online rows (IsOnline == true), for the grid's Copy ▸ All online devices.</summary>
    public IReadOnlyList<string> OnlineNames => [.. Computers.Where(c => c.IsOnline == true).Select(c => c.Name)];

    /// <summary>Names of the offline rows (IsOnline == false), for the grid's Copy ▸ All offline devices.</summary>
    public IReadOnlyList<string> OfflineNames => [.. Computers.Where(c => c.IsOnline == false).Select(c => c.Name)];

    /// <summary>ConfigMgr client actions shown in the grid's right-click menu.</summary>
    public IReadOnlyList<ScheduleAction> ClientActions => Core.Sccm.ClientActions.All;

    // --- grid filter (name search + state chips) -------------------------------------------

    /// <summary>True once the tab has any machines — drives the filter bar's visibility.</summary>
    public bool HasComputers => Computers.Count > 0;

    /// <summary>Free-text name filter for the grid (substring, case-insensitive).</summary>
    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    /// <summary>The active quick state-filter chip.</summary>
    [ObservableProperty]
    public partial RowFilter ActiveFilter { get; set; } = RowFilter.All;

    partial void OnFilterTextChanged(string value) => RefreshFilter();

    partial void OnActiveFilterChanged(RowFilter value) => RefreshFilter();

    private void RefreshFilter()
    {
        _computersView.Refresh();
        OnPropertyChanged(nameof(IsFilterActive));
        OnPropertyChanged(nameof(FilterStatus));
        OnPropertyChanged(nameof(VisibleRowCount));
        OnPropertyChanged(nameof(GridOverlayState));
        OnPropertyChanged(nameof(ShowMachineGrid));
        OnPropertyChanged(nameof(ShowUpdateGrid));
    }

    /// <summary>True when a name filter or a non-All state filter is in effect.</summary>
    public bool IsFilterActive => ActiveFilter != RowFilter.All || !string.IsNullOrWhiteSpace(FilterText);

    /// <summary>"Showing N of M" while filtered, otherwise empty.</summary>
    public string FilterStatus =>
        IsFilterActive ? $"Showing {VisibleRowCount} of {Computers.Count}" : string.Empty;

    /// <summary>Number of rows currently shown (the filtered set) — also what an export/CSV includes.</summary>
    public int VisibleRowCount => _computersView.Cast<Computer>().Count();

    // --- M13/M29: overlay state machine ---

    /// <summary>Mutually-exclusive grid-area overlay, by precedence:
    /// 0 = data present (no overlay), 2 = cold start (no machines ever loaded),
    /// 3 = filter-empty (machines exist but the filter hides all).
    /// (No "bulk loading" state: rows are added synchronously and appear at once, so there is never a
    /// blank loading gap — the toolbar ring + sweep narration cover the subsequent check.)</summary>
    public int GridOverlayState
    {
        get
        {
            if (!HasComputers) return 2;
            if (VisibleRowCount == 0) return 3;
            return 0;
        }
    }

    /// <summary>Show the machines DataGrid only when in machine mode AND there is data to show. When an
    /// empty state is up the grid is collapsed so the (column-wide) DataGrid can't push the layout past
    /// the viewport — which would shove the centred empty-state card off to the side.</summary>
    public bool ShowMachineGrid => IsMachineMode && GridOverlayState == 0;

    /// <summary>Show the Windows Update view only when in update mode AND there is data (see
    /// <see cref="ShowMachineGrid"/>).</summary>
    public bool ShowUpdateGrid => IsUpdateMode && GridOverlayState == 0;

    /// <summary>The grid filter predicate: name-substring AND the active state chip.</summary>
    private bool RowMatchesFilter(object obj)
    {
        if (obj is not Computer c)
        {
            return false;
        }

        string q = FilterText?.Trim() ?? string.Empty;
        if (q.Length > 0 && !(c.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return false;
        }

        return ActiveFilter switch
        {
            RowFilter.Offline => c.IsOnline == false,
            RowFilter.UpdatesAvailable => c.PatchState == PatchState.Available || (c.UpdatesAvailable ?? 0) > 0 || c.MissingUpdates == true,
            RowFilter.RebootPending => c.RebootRequired == true || c.PatchState == PatchState.RebootPending,
            RowFilter.Errors => c.PatchState == PatchState.Error || !string.IsNullOrEmpty(c.LastError) || !string.IsNullOrEmpty(c.UpdateError),
            RowFilter.Done => c.PatchState == PatchState.Done,
            RowFilter.Unhealthy => c.VitalityBand is VitalityBand.Warning or VitalityBand.Critical or VitalityBand.Offline,
            RowFilter.Server2016 => LcuRouting.Is2016(c.OsBuild),
            RowFilter.NotScanned => c.UpdatePhase == null,
            RowFilter.Scheduled => c.ScheduledNextRun is not null,
            _ => true,
        };
    }

    /// <summary>Builds a CSV report of the rows currently shown (respects the filter) for a
    /// maintenance-window write-up / ticket: machine, online, state, updates, reboot, error, OS, schedule.</summary>
    public string BuildReportCsv()
    {
        // Append a column per user-defined custom column so the export reflects the grid the user built.
        List<CustomColumnSpec> custom = [.. CustomColumns];

        var sb = new StringBuilder();
        string header = "Name,Online,Status,Updates available,Update message,Reboot pending,Reboot message,Last error,OS,Scheduled,Scheduled time";
        if (custom.Count > 0)
        {
            header += "," + string.Join(",", custom.Select(c => Csv(c.Name)));
        }

        sb.AppendLine(header);
        foreach (Computer c in _computersView.Cast<Computer>())
        {
            var fields = new List<string>
            {
                Csv(c.Name),
                Csv(c.IsOnline switch { true => "Online", false => "Offline", _ => "?" }),
                Csv(c.PatchState.ToString()),
                Csv(c.UpdatesAvailable?.ToString() ?? string.Empty),
                Csv(c.UpdateMessage ?? c.LastStatus ?? string.Empty),
                Csv(c.RebootRequired switch { true => "Yes", false => "No", _ => "?" }),
                Csv(c.RebootMessage ?? string.Empty),
                Csv(c.LastError ?? c.UpdateError ?? string.Empty),
                Csv(c.OperatingSystem ?? string.Empty),
                Csv(c.ScheduledAction ?? string.Empty),
                Csv(c.ScheduledNextRun?.ToString("yyyy-MM-dd HH:mm") ?? string.Empty),
            };
            fields.AddRange(custom.Select(col => Csv(c.CustomValues[col.Name] ?? string.Empty)));
            sb.AppendLine(string.Join(",", fields));
        }

        return sb.ToString();
    }

    /// <summary>True when at least one shown row has had a software check — gates the "Export software
    /// report" menu item so an all-blank report isn't offered.</summary>
    public bool HasSoftwareResults => _computersView.Cast<Computer>().Any(c => c.SoftwareCheck is not null);

    /// <summary>Builds an on-demand CSV of the software-check results for the rows currently shown
    /// (respects the filter) — a per-machine report for handing off: machine, online, what was searched,
    /// installed?, product + version, the service checked, and whether it's running.</summary>
    public string BuildSoftwareReportCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Machine,Online,Searched for,Installed,Product,Version,Service,Service running,Checked");
        foreach (Computer c in _computersView.Cast<Computer>())
        {
            sb.AppendLine(string.Join(",", new[]
            {
                Csv(c.Name),
                Csv(c.IsOnline switch { true => "Online", false => "Offline", _ => "Unknown" }),
                Csv(c.SoftwareQuery ?? string.Empty),
                Csv(c.SoftwareFound switch { true => "Yes", false => "No", _ => string.Empty }),
                Csv(c.SoftwareName ?? string.Empty),
                Csv(c.SoftwareVersion ?? string.Empty),
                Csv(c.SoftwareServiceName ?? string.Empty),
                Csv(c.SoftwareServiceState switch
                {
                    null => string.Empty,
                    "not found" => "Not found",
                    "Running" => "Yes",
                    _ => "No",
                }),
                Csv(c.SoftwareCheckedAt?.ToString("yyyy-MM-dd HH:mm") ?? string.Empty),
            }));
        }

        return sb.ToString();
    }

    private static string Csv(string value)
    {
        // Guard against CSV/formula injection: a value from a target machine (software DisplayName,
        // error string, custom-column output) that starts with = + - @ is interpreted as a formula
        // by Excel / LibreOffice when the file is opened.  Prefix with a tab to neutralise it,
        // then fall through to the quoted branch so the tab is preserved in the output cell.
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
        {
            value = "\t" + value;
        }

        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r') || value.StartsWith('\t')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    /// <summary>True while a sweep (Ping All / Check All) is running — drives the busy indicator and button enable-state.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PingAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(PingOfflineCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckVitalsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanUpdatesCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdatesCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallCheckedCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallCheckedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanFocusedCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanTargetCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallTargetCommand))]
    [NotifyPropertyChangedFor(nameof(CanInstallChecked))]
    [NotifyPropertyChangedFor(nameof(CanUninstallChecked))]
    [NotifyPropertyChangedFor(nameof(CanInstallAll))]
    [NotifyPropertyChangedFor(nameof(SweepStatus))]
    [NotifyPropertyChangedFor(nameof(IsVitalsSweepRunning))]
    public partial bool IsBusy { get; set; }

    /// <summary>M8 + M12: one-line sweep narration shown beside the ProgressRing and in the status bar.
    /// ONE op  → "Checking vitals — 12/48 · 00:32" (same as before).
    /// TWO ops → "2 operations · Install 3/12 · Scan 7/40" (per-op fragments; elapsed for oldest op).
    /// THREE+  → "3 operations · Install 3/12 · Scan 7/40 · +1 more".
    /// Empty when idle.</summary>
    public string SweepStatus
    {
        get
        {
            if (!IsBusy || _activeOps.Count == 0) return string.Empty;

            if (_activeOps.Count == 1)
            {
                OperationRecord op = _activeOps[0];
                int completed = op.Completed;
                int total = op.Total;
                TimeSpan elapsed = op.Stopwatch.Elapsed;
                string elapsedStr = elapsed.TotalHours >= 1
                    ? $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
                    : $"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}";
                return total > 0
                    ? $"{op.Label} — {completed}/{total} · {elapsedStr}"
                    : $"{op.Label} · {elapsedStr}";
            }

            // Multiple ops: show count + per-op fragments (up to 2 named; oldest elapsed).
            // Find the oldest (longest-running) stopwatch for the overall elapsed.
            TimeSpan oldestElapsed = _activeOps.Max(o => o.Stopwatch.Elapsed);
            string oldestStr = oldestElapsed.TotalHours >= 1
                ? $"{(int)oldestElapsed.TotalHours:D2}:{oldestElapsed.Minutes:D2}:{oldestElapsed.Seconds:D2}"
                : $"{(int)oldestElapsed.TotalMinutes:D2}:{oldestElapsed.Seconds:D2}";

            var parts = new List<string> { $"{_activeOps.Count} operations" };
            int shown = 0;
            foreach (OperationRecord op in _activeOps)
            {
                if (shown >= 2) break;
                string frag = op.Total > 0
                    ? $"{op.Label} {op.Completed}/{op.Total}"
                    : op.Label;
                parts.Add(frag);
                shown++;
            }

            if (_activeOps.Count > 2)
            {
                parts.Add($"+{_activeOps.Count - 2} more");
            }

            parts.Add(oldestStr);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>P: true while ANY active operation is labeled "Checking vitals" (AND busy).
    /// Drives the amber banner in WorkspaceView Row 1. Redefined to support concurrency: the banner
    /// appears whenever a vitals sweep is running, even if another op (e.g. Install) started first.</summary>
    public bool IsVitalsSweepRunning =>
        IsBusy && _activeOps.Any(op => string.Equals(op.Label, "Checking vitals", StringComparison.Ordinal));

    /// <summary>M11: the fleet band stays visible during a patch sweep OR during the 3-second hold-open
    /// after completion.</summary>
    public bool IsPatchOperationOrFleetHeld =>
        IsPatchOperationActive || _fleetBandHeld;

    /// <summary>M11: elapsed time string for the fleet band header ("01:23").
    /// Uses the oldest (longest-running) active operation's elapsed, or zero when idle.</summary>
    public string FleetElapsed
    {
        get
        {
            if (_activeOps.Count == 0) return string.Empty;
            TimeSpan e = _activeOps.Max(op => op.Stopwatch.Elapsed);
            if (e == TimeSpan.Zero) return string.Empty;
            return e.TotalHours >= 1
                ? $"{(int)e.TotalHours:D2}:{e.Minutes:D2}:{e.Seconds:D2}"
                : $"{(int)e.TotalMinutes:D2}:{e.Seconds:D2}";
        }
    }

    /// <summary>M11: "N/M machines" label for the fleet band (e.g. "12/48 machines").
    /// Aggregates completed/total by SUMMING across all active operations.</summary>
    public string FleetNofM
    {
        get
        {
            if (_activeOps.Count == 0) return string.Empty;
            int totalCompleted = _activeOps.Sum(op => op.Completed);
            int totalRows = _activeOps.Sum(op => op.Total);
            return totalRows > 0 ? $"{totalCompleted}/{totalRows} machines" : string.Empty;
        }
    }

    /// <summary>
    /// When true the tab shows the Windows Update grid + patch command bar instead of the
    /// Machines grid (same machine list, different lane). Bound to the per-tab mode toggle.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMachineMode))]
    [NotifyPropertyChangedFor(nameof(CanShowInstallToolbar))]
    [NotifyPropertyChangedFor(nameof(ShowMachineGrid))]
    [NotifyPropertyChangedFor(nameof(ShowUpdateGrid))]
    public partial bool IsUpdateMode { get; set; }

    /// <summary>Inverse of <see cref="IsUpdateMode"/> — each grid binds its own bool through one converter.</summary>
    public bool IsMachineMode => !IsUpdateMode;

    /// <summary>True when the tab holds work worth a confirm before closing — loaded machines, or a
    /// live monitor / sweep. Empty, idle tabs close instantly (so the guard never habituates).</summary>
    public bool HasWork => Computers.Count > 0 || IsBusy || IsMonitoring;

    /// <summary>
    /// Side-panel scope toggle: false = Applicable (default; the install flow), true = Installed
    /// (the uninstall flow — the checklist shows installed updates with checkboxes, non-uninstallable
    /// rows greyed). Per-tab; drives <see cref="PatchOptions.Scope"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsApplicableMode))]
    [NotifyPropertyChangedFor(nameof(CanShowInstallToolbar))]
    [NotifyPropertyChangedFor(nameof(FocusedActiveUpdates))]
    [NotifyCanExecuteChangedFor(nameof(InstallCheckedCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallCheckedCommand))]
    [NotifyPropertyChangedFor(nameof(CanInstallChecked))]
    [NotifyPropertyChangedFor(nameof(CanUninstallChecked))]
    public partial bool IsInstalledMode { get; set; }

    /// <summary>Inverse of <see cref="IsInstalledMode"/> — bound by the "Applicable" radio in the side panel.</summary>
    public bool IsApplicableMode => !IsInstalledMode;

    /// <summary>
    /// The main toolbar's "Install" button is shown only when the tab is in Windows Update (Patching)
    /// mode <em>and</em> the scope is Applicable (uninstalling all installed updates from the toolbar
    /// would be too destructive a default — uninstall is per-machine from the side panel only).
    /// </summary>
    public bool CanShowInstallToolbar => IsUpdateMode && !IsInstalledMode;

    /// <summary>
    /// The collection bound to the side-panel checklist DataGrid — points at the focused machine's
    /// <see cref="Computer.ApplicableUpdates"/> or <see cref="Computer.InstalledUpdates"/> depending
    /// on the current scope. Each cache is populated by a Scan in its own scope and persists across
    /// scope toggles and panel close/reopen — so once you've scanned a machine in Installed scope
    /// you don't have to scan again every time the panel pops back up.
    /// </summary>
    public ObservableCollection<SelectableUpdate>? FocusedActiveUpdates =>
        FocusedComputer is null
            ? null
            : (IsInstalledMode ? FocusedComputer.InstalledUpdates : FocusedComputer.ApplicableUpdates);

    /// <summary>The update source for scan/install (bound to the patch command bar's Source toggle).</summary>
    [ObservableProperty]
    public partial UpdateSource SelectedSource { get; set; }

    /// <summary>Comma/newline-separated exclude terms (bound to the Exclude box); parsed into <see cref="PatchOptions"/>.</summary>
    [ObservableProperty]
    public partial string ExcludeText { get; set; } = string.Empty;

    /// <summary>
    /// Whether the patch command bar's "Include drivers" checkbox is on. Off by default — the WUA
    /// search adds <c>Type='Software'</c>, matching the Windows Update UI and BatchPatch.
    /// </summary>
    [ObservableProperty]
    public partial bool IncludeDrivers { get; set; }

    /// <summary>The update sources offered in the Source toggle.</summary>
    public IReadOnlyList<UpdateSource> UpdateSources { get; } =
        [UpdateSource.WindowsUpdate, UpdateSource.MicrosoftUpdate, UpdateSource.Managed];

    /// <summary>
    /// The machine whose update checklist the Windows Update side panel shows — the grid's focused
    /// row (set from the code-behind selection handler). Null = no machine focused.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCheckedCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallCheckedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanFocusedCommand))]
    [NotifyPropertyChangedFor(nameof(FocusedActiveUpdates))]
    [NotifyPropertyChangedFor(nameof(CanInstallChecked))]
    [NotifyPropertyChangedFor(nameof(CanUninstallChecked))]
    [NotifyPropertyChangedFor(nameof(IsFocusedPatching))]
    [NotifyPropertyChangedFor(nameof(IsFocusedRebootPending))]
    public partial Computer? FocusedComputer { get; set; }

    /// <summary>Keep a subscription to the focused machine's <see cref="Computer.IsPatching"/> so the
    /// checklist + per-machine buttons lock while it installs/uninstalls, and re-track the checklist
    /// when focus changes.</summary>
    partial void OnFocusedComputerChanged(Computer? oldValue, Computer? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnFocusedComputerPropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnFocusedComputerPropertyChanged;
        }

        RetrackChecklist();
    }

    private void OnFocusedComputerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Computer.IsPatching))
        {
            OnPropertyChanged(nameof(IsFocusedPatching));
            RefreshChecklistCommandState();
        }
        else if (e.PropertyName == nameof(Computer.RebootRequired))
        {
            OnPropertyChanged(nameof(IsFocusedRebootPending));
            RefreshChecklistCommandState();
        }
    }

    /// <summary>True while the focused machine has an install/uninstall in flight — drives the
    /// checklist lock (DataGrid/All/None disabled) and the per-machine button enable-state.</summary>
    public bool IsFocusedPatching => FocusedComputer?.IsPatching == true;

    /// <summary>
    /// True when the focused machine has a reboot pending. After an install, WUA keeps reporting the
    /// just-installed update as still "applicable" until the reboot finalizes the servicing
    /// transaction — so a re-scan shows it again and the user could be fooled into re-installing.
    /// Installing more while a reboot is pending is also what the agent's boot-busy guard refuses to
    /// do. So we surface this and block Install until the machine is rebooted.
    /// </summary>
    public bool IsFocusedRebootPending => FocusedComputer?.RebootRequired == true;

    /// <summary>
    /// When true, a background loop continuously re-checks every row's online/offline state on
    /// an interval (and newly-added rows are checked immediately). On by default; turned off by
    /// Stop or the Monitor toggle. Bound two-way to the toolbar's Monitor toggle.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    public partial bool IsMonitoring { get; set; }

    /// <summary>The credential used for remote operations (shared with the Settings + Run Script windows).</summary>
    public CredentialStore Credentials => _credentials;

    /// <summary>Services are injected from the composition root (App) and shared across tabs.</summary>
    public WorkspaceViewModel(IHostPinger pinger, IHostProbe hostProbe, IConfigMgrClient configMgr, IWinRmEnabler winRm, CredentialStore credentials, IComputerListStore lists, IActivityLog activity, IScriptLibrary scripts, IPatchService patch, PatchOptions patchOptions, IHostRebootProbe rebootProbe, IPowerShellHost powerShell, IVitalsProbe vitals, IRemediationService remediation, IDeploymentService deployment, ISoftwareProbe software, ICustomColumnProbe customColumns, ICatalogSizeService catalogSize, OrphanRebootServiceReaper reaper)
    {
        _pinger = pinger;
        _hostProbe = hostProbe;
        _configMgr = configMgr;
        _winRm = winRm;
        _powerShell = powerShell;
        _credentials = credentials;
        _lists = lists;
        _activity = activity;
        _scripts = scripts;
        _patch = patch;
        _patchOptions = patchOptions;
        _patchThrottleCap = ClampInstallCap(_appSettings.Load().MaxSimultaneousInstalls);
        _patchThrottle = new SemaphoreSlim(_patchThrottleCap);
        // First tab wins: the shared read budget is set once from the singleton PatchOptions and then
        // reused by every subsequent tab. Safe without a lock because all WorkspaceViewModel instances
        // are constructed on the UI thread (ShellViewModel.NewTab dispatches to no background thread).
        if (_remoteSweepThrottleBacking is null)
        {
            int total    = Math.Max(2, patchOptions.MaxConcurrentScans);
            int reserved = Math.Min(4, total - 1);
            _remoteSweepThrottleBacking = new SplitThrottle(total, reserved);
        }
        _rebootProbe = rebootProbe;
        _vitals = vitals;
        _remediation = remediation;
        _deployment = deployment;
        _software = software;
        _customColumns = customColumns;
        _catalogSize = catalogSize;
        _reaper = reaper;
        LoadColumnLayout();
        SelectedSource = patchOptions.Source;
        ExcludeText = string.Join(", ", patchOptions.ExcludeNameContains);
        IncludeDrivers = patchOptions.IncludeDrivers;
        IsInstalledMode = patchOptions.Scope == UpdateScope.Installed;
        // Keep the status-bar online/total live as rows are added/removed and their state changes.
        Computers.CollectionChanged += OnComputersChanged;

        // The grid filter: a live-shaped view over Computers so a row that (e.g.) errors mid-sweep
        // appears under the Errors filter automatically. With the default filter (All + no text)
        // every row passes, so this is inert until the user filters.
        _computersView = CollectionViewSource.GetDefaultView(Computers);
        _computersView.Filter = RowMatchesFilter;
        if (_computersView is ICollectionViewLiveShaping live && live.CanChangeLiveFiltering)
        {
            foreach (string prop in (string[])
                [nameof(Computer.Name), nameof(Computer.IsOnline), nameof(Computer.PatchState),
                 nameof(Computer.RebootRequired), nameof(Computer.LastError), nameof(Computer.UpdateError),
                 nameof(Computer.UpdatesAvailable), nameof(Computer.MissingUpdates), nameof(Computer.VitalityBand),
                 nameof(Computer.OsBuild), nameof(Computer.UpdatePhase), nameof(Computer.ScheduledNextRun)])
            {
                live.LiveFilteringProperties.Add(prop);
            }

            live.IsLiveFiltering = true;
        }

        // Subscribe to the CollectionView's own CollectionChanged — this fires AFTER the view
        // incorporates the new/removed row (unlike Computers.CollectionChanged which fires before),
        // so GridOverlayState and VisibleRowCount read the correct post-update count.
        ((INotifyCollectionChanged)_computersView).CollectionChanged += OnVisibleRowsChanged;

        // No seeding — the grid starts empty; the user opens a saved list or pastes one.
        IsMonitoring = true; // start watching online/offline straight away
    }

    /// <summary>Re-notify overlay and filter-status properties after the CollectionView updates its count.
    /// Coalesced: these are whole-view counts (VisibleRowCount walks the view), so a bulk add that fires this
    /// per row would be O(N^2) — mark dirty and let the timer recompute once.</summary>
    private void OnVisibleRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        MarkFleetDirty();
    }

    /// <summary>Subscribe/unsubscribe row state changes and refresh the online/total summary.</summary>
    private void OnComputersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (Computer c in e.OldItems)
            {
                c.PropertyChanged -= OnComputerStateChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (Computer c in e.NewItems)
            {
                c.PropertyChanged += OnComputerStateChanged;
            }
        }

        // The per-row subscribe/unsubscribe above ALWAYS runs immediately (never coalesced) so every row stays
        // live and the Reset/NewItems==null re-subscription is never skipped. Only the expensive whole-fleet
        // recompute is coalesced — a bulk add fires this per row, so doing it on the spot is O(N^2); the timer
        // does it once. RaiseFleetAggregates covers the full set this used to raise inline (+ VisibleRowCount).
        MarkFleetDirty();
    }

    private void OnComputerStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // IsOnline drives OnlineSummary (a whole-list count). Every box's online result fires this during
            // an auto-check sweep, so COALESCE the recompute instead of re-counting the whole list per result
            // (this per-result whole-fleet recompute was the measured O(N^2) freeze). The row's own online dot
            // still updates immediately via its binding — only the fleet tally is coalesced.
            case nameof(Computer.IsOnline):
                MarkFleetDirty();
                break;
            // Progress ticks are the highest-frequency change funnelled through here during an install sweep,
            // and FleetProgress is the ONLY fleet aggregate whose value depends on UpdateProgress — so a tick
            // raises JUST FleetProgress, IMMEDIATELY (kept narrow + live for a smooth bar; never the full
            // recompute). EXACT match only — a null/blank PropertyName never lands here; it falls below.
            case nameof(Computer.UpdateProgress):
                OnPropertyChanged(nameof(FleetProgress));
                break;
            // PatchState / VitalityBand drive the fleet tallies (a null/blank "all changed" is folded in so a
            // coarse change still recomputes). These storm during scan / install / vitals sweeps — one per
            // result across the fleet — so COALESCE the whole-fleet recompute via the timer (O(N^2) → O(N)).
            // The row's own chip/band update immediately via its bindings; only the TALLIES are coalesced.
            case nameof(Computer.PatchState):
            case nameof(Computer.VitalityBand):
            case null:
            case "":
                MarkFleetDirty();
                break;
            // A vitals check populates OsBuild, which makes a box appear in the self-populating 2016 panel —
            // COALESCE the re-tally (it storms as the whole fleet gets classified during the sweep). The 2016
            // count/visibility AND the orphaned-filter reset are both in RaiseFleetAggregates.
            case nameof(Computer.OsBuild):
                MarkFleetDirty();
                break;
            // The operator marked/unmarked a SINGLE box for staged patching — a rare one-off action, not a
            // sweep storm — so raise immediately (the Staged column should appear/disappear at once).
            case nameof(Computer.RequiresStagedPatching):
                OnPropertyChanged(nameof(HasStagedServer2016));
                break;
        }
    }

    /// <summary>Re-publish all the derived fleet aggregates after a row state change.</summary>
    private void RaiseFleetChanged()
    {
        OnPropertyChanged(nameof(FleetSummary));
        OnPropertyChanged(nameof(HasFleetSummary));
        OnPropertyChanged(nameof(VitalsFleetSummary));
        OnPropertyChanged(nameof(HasVitalsFleetSummary));
        OnPropertyChanged(nameof(IsPatchOperationActive));
        OnPropertyChanged(nameof(IsPatchOperationOrFleetHeld)); // M11: drives band visibility
        OnPropertyChanged(nameof(FleetProgress));
        OnPropertyChanged(nameof(ShowUpdateFirstRunHint));
        // Live filtering can change the shown count as rows change state under an active filter.
        OnPropertyChanged(nameof(FilterStatus));
    }

    /// <summary>Request a coalesced recompute of the whole-fleet summaries. Cheap (O(1)): marks the tallies
    /// dirty and ensures the recompute timer is running. The actual O(N) recompute happens at most once per
    /// <see cref="FleetRecomputeWindow"/> on the UI thread (see <see cref="OnFleetRecomputeTick"/>), so an
    /// N-result sweep costs O(N) total instead of the old O(N^2) (one whole-list recompute per result).
    /// Called only on the UI thread (collection/view changes and row PropertyChanged all fire there).</summary>
    private void MarkFleetDirty()
    {
        _fleetDirty = true;
        _fleetRecomputeTimer ??= CreateFleetRecomputeTimer();
        if (!_fleetRecomputeTimer.IsEnabled)
        {
            _fleetRecomputeTimer.Start();
        }
    }

    /// <summary>Creates the coalescing timer. A <see cref="DispatcherTimer"/> fires on the thread that creates
    /// it — the UI thread (the VM is built and operated on the UI thread, same as <see cref="_fleetBandHoldTimer"/>)
    /// — so the recompute and its property raises stay UI-thread, which the live-filtered grid requires.</summary>
    private DispatcherTimer CreateFleetRecomputeTimer()
    {
        var timer = new DispatcherTimer { Interval = FleetRecomputeWindow };
        timer.Tick += OnFleetRecomputeTick;
        return timer;
    }

    /// <summary>Coalescing tick: if anything was marked dirty since the last tick, recompute the fleet
    /// aggregates ONCE and clear the flag; otherwise stop (go idle). This guarantees the trailing edge — the
    /// LAST dirty mark is always followed by a tick that finds it dirty and recomputes, because the timer only
    /// stops on a tick that finds NOTHING dirty (and dirty is cleared only by a recompute). So the final
    /// tallies are exactly correct once the sweep ends; no final update is ever dropped.</summary>
    private void OnFleetRecomputeTick(object? sender, EventArgs e)
    {
        if (_fleetDirty)
        {
            _fleetDirty = false;
            RaiseFleetAggregates();
        }
        else
        {
            _fleetRecomputeTimer?.Stop();
        }
    }

    /// <summary>The COMPLETE set of whole-fleet aggregates a row-state or collection change can affect, raised
    /// together once by the coalescing timer. Superset union of what <see cref="OnComputersChanged"/>'s tail,
    /// the storm cases of <see cref="OnComputerStateChanged"/>, and <see cref="OnVisibleRowsChanged"/> each used
    /// to raise synchronously per change — so none goes stale. (Re-raising an aggregate whose value didn't
    /// actually change is harmless: WPF skips the binding update when the value is equal.)</summary>
    private void RaiseFleetAggregates()
    {
        OnPropertyChanged(nameof(OnlineSummary));
        OnPropertyChanged(nameof(HasComputers));
        OnPropertyChanged(nameof(VisibleRowCount));
        OnPropertyChanged(nameof(GridOverlayState));
        OnPropertyChanged(nameof(ShowMachineGrid));
        OnPropertyChanged(nameof(ShowUpdateGrid));
        ScanTargetCommand.NotifyCanExecuteChanged();
        InstallTargetCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanInstallAll));
        OnPropertyChanged(nameof(Server2016Count));
        OnPropertyChanged(nameof(HasServer2016));
        OnPropertyChanged(nameof(HasStagedServer2016));
        // The 2016 chip just vanished — never leave its (now-invisible) filter active hiding every row.
        if (!HasServer2016 && ActiveFilter == RowFilter.Server2016) { ActiveFilter = RowFilter.All; }
        RaiseFleetChanged();
    }

    // --- fleet aggregates (Status band + bottom bar + first-run hint) ---------------------

    /// <summary>Compact per-state tally for the status band / bottom bar, e.g.
    /// "12 done · 3 installing · 2 reboot · 1 failed". Empty until a scan/install has touched a row.</summary>
    public string FleetSummary
    {
        get
        {
            int done = 0, working = 0, reboot = 0, failed = 0, available = 0;
            foreach (Computer c in Computers)
            {
                switch (c.PatchState)
                {
                    case PatchState.Done: done++; break;
                    case PatchState.Scanning or PatchState.Downloading or PatchState.Installing or PatchState.Uninstalling: working++; break;
                    case PatchState.RebootPending: reboot++; break;
                    case PatchState.Error: failed++; break;
                    case PatchState.Available: available++; break;
                }
            }

            var parts = new List<string>(5);
            if (working > 0) parts.Add($"{working} working");
            if (available > 0) parts.Add($"{available} available");
            if (reboot > 0) parts.Add($"{reboot} reboot");
            if (done > 0) parts.Add($"{done} done");
            if (failed > 0) parts.Add($"{failed} failed");
            return parts.Count == 0 ? string.Empty : "Updates: " + string.Join(" · ", parts);
        }
    }

    /// <summary>Whether <see cref="FleetSummary"/> is non-empty (drives the bottom-bar separator dot).</summary>
    public bool HasFleetSummary => FleetSummary.Length > 0;

    /// <summary>Per-band vitality tally for the bottom bar, e.g.
    /// "Vitals: Healthy 40 · Warning 6 · Critical 2". Empty until a vitals sweep has scored a row.</summary>
    public string VitalsFleetSummary
    {
        get
        {
            int healthy = 0, warning = 0, critical = 0, offline = 0, unknown = 0;
            foreach (Computer c in Computers)
            {
                switch (c.VitalityBand)
                {
                    case VitalityBand.Healthy: healthy++; break;
                    case VitalityBand.Warning: warning++; break;
                    case VitalityBand.Critical: critical++; break;
                    case VitalityBand.Offline: offline++; break;
                    case VitalityBand.Unknown: unknown++; break;
                }
            }

            var parts = new List<string>(5);
            if (healthy > 0) parts.Add($"Healthy {healthy}");
            if (warning > 0) parts.Add($"Warning {warning}");
            if (critical > 0) parts.Add($"Critical {critical}");
            if (offline > 0) parts.Add($"Offline {offline}");
            if (unknown > 0) parts.Add($"Unknown {unknown}");
            return parts.Count == 0 ? string.Empty : "Vitals: " + string.Join(" · ", parts);
        }
    }

    /// <summary>Whether <see cref="VitalsFleetSummary"/> is non-empty (drives its bottom-bar separator).</summary>
    public bool HasVitalsFleetSummary => VitalsFleetSummary.Length > 0;

    /// <summary>True while any row is actively scanning/downloading/installing — drives the band's visibility.</summary>
    public bool IsPatchOperationActive =>
        Computers.Any(c => c.PatchState is PatchState.Scanning or PatchState.Downloading or PatchState.Installing or PatchState.Uninstalling);

    /// <summary>Overall progress (avg of the rows currently downloading/installing) for the band's bar; 0 when none.</summary>
    public int FleetProgress
    {
        get
        {
            int[] vals = [.. Computers
                .Where(c => c.PatchState is PatchState.Downloading or PatchState.Installing or PatchState.Uninstalling)
                .Select(c => c.UpdateProgress ?? 0)];
            return vals.Length == 0 ? 0 : (int)Math.Round(vals.Average());
        }
    }

    /// <summary>Shown in update mode until any row has been scanned (a real PatchState beyond Idle appeared).</summary>
    public bool ShowUpdateFirstRunHint =>
        IsUpdateMode && Computers.Count > 0 && Computers.All(c => c.PatchState == PatchState.Idle);

    /// <summary>Push the Source toggle's value into the shared patch options.</summary>
    partial void OnSelectedSourceChanged(UpdateSource value) => _patchOptions.Source = value;

    /// <summary>Parse the Exclude box into the shared patch options (split on comma/newline, trim, drop blanks).</summary>
    partial void OnExcludeTextChanged(string value) =>
        _patchOptions.ExcludeNameContains =
            [.. (value ?? string.Empty)
                .Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>Push the "Include drivers" checkbox into the shared patch options.</summary>
    partial void OnIncludeDriversChanged(bool value) => _patchOptions.IncludeDrivers = value;

    /// <summary>
    /// Push the side-panel scope toggle into the shared patch options and restore each row's
    /// visible "Windows update message" / count from the per-scope cache (so the grid reflects
    /// whichever scope is active). The per-scope SelectableUpdate collections on
    /// <see cref="Computer"/> are NOT cleared — once a scope has been scanned, that data sticks
    /// across toggles and panel close/reopen until the user re-scans in that scope.
    /// </summary>
    partial void OnIsInstalledModeChanged(bool value)
    {
        _patchOptions.Scope = value ? UpdateScope.Installed : UpdateScope.Applicable;

        foreach (Computer c in Computers)
        {
            // Net rule: no terminal status — success (Done/RebootPending, which includes the 2016
            // Cleaned/Deferred terminals) or failure (Error, incl. the "Can't reach WU" Unreachable
            // state) — is ever blanked by a scope-toggle, and neither is an in-flight row (IsPatching).
            // Those rows carry operation detail (e.g. "Installed 3 updates", "Can't reach WU"),
            // NOT a scope-scoped scan result; the target scope's cached message is often null for them,
            // so swapping would silently blank the detail. Only a non-terminal scanned state (e.g.
            // Available) has a real cached message on both sides and should swap. The per-scope COUNTS
            // still track for every row regardless — only the message is preserved for terminal/in-flight rows.
            if (!ScopeToggleRule.PreservesMessageOnScopeToggle(c.PatchState, c.IsPatching))
            {
                c.UpdateMessage = value ? c.InstalledMessage : c.ApplicableMessage;
            }

            c.UpdatesAvailable = value ? c.InstalledCount : c.ApplicableCount;
        }

        // The active checklist collection changed (Applicable ↔ Installed) — re-track it for the
        // Install/Uninstall enable-state.
        RetrackChecklist();
    }

    /// <summary>
    /// When the tab flips into Windows Update (Patching) mode, kick an immediate monitor pass so the
    /// Pending Reboot column populates straight away instead of waiting for the next 20 s tick.
    /// </summary>
    partial void OnIsUpdateModeChanged(bool value)
    {
        // The first-run hint + fleet band are update-mode-only — refresh their visibility on the flip.
        OnPropertyChanged(nameof(ShowUpdateFirstRunHint));
        OnPropertyChanged(nameof(IsPatchOperationActive));

        // Entering Health (machine mode): the patch-only filters have no chip in the Health bar to clear
        // them, so don't leave the grid silently filtered — fall back to All. (Distinct trigger from the
        // Server2016 orphan-reset that fires when 2016 boxes leave a tab; both converge on ActiveFilter=All.)
        if (!value && ActiveFilter is RowFilter.UpdatesAvailable or RowFilter.Server2016)
        {
            ActiveFilter = RowFilter.All;
        }

        if (value && IsMonitoring && _monitorCts is { } cts && Computers.Count > 0)
        {
            _ = MonitorRowsAsync([.. Computers], cts.Token);
        }
    }

    /// <summary>Starts/stops the continuous monitor loop when <see cref="IsMonitoring"/> flips.</summary>
    partial void OnIsMonitoringChanged(bool value)
    {
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = null;

        if (value)
        {
            var cts = new CancellationTokenSource();
            _monitorCts = cts;
            _ = MonitorLoopAsync(cts.Token);
        }
    }

    /// <summary>The shared activity log (also handed to the Run Script window).</summary>
    public IActivityLog Activity => _activity;

    /// <summary>The shared script library — backs the cascading "Run script" menu and the Run Script window.</summary>
    public IScriptLibrary ScriptLibrary => _scripts;

    /// <summary>The shared routing PowerShell host — also handed to the Run Script window so script runs go
    /// through the same Kerberos→SMB transport cache (fast-fail + WinRM-healthy recording) as every other op.</summary>
    public IPowerShellHost PowerShell => _powerShell;

    /// <summary>PowerShell credential for remote ops, or null to use the current Windows login.</summary>
    private PSCredential? CurrentPsCredential() => _credentials.Current?.ToPowerShellCredential();

    /// <summary>Replaces the grid with a fresh set of machines (from the loader or a saved list).</summary>
    public void SetComputers(IEnumerable<string> names)
    {
        // Drop per-host monitor state for the outgoing rows FIRST: Computers.Clear() raises a Reset
        // (OldItems is null), so OnComputersChanged can't do it — a same-named host in the new list would
        // otherwise inherit stale state (e.g. a permanently-suppressed reboot probe or a 5-min back-off).
        foreach (Computer existing in Computers)
        {
            ForgetHostState(existing.Name);
        }

        Computers.Clear();
        SelectedComputers.Clear();
        FocusedComputer = null;
        AddComputers(names);
    }

    /// <summary>Appends machines to the grid, skipping blanks and ones already present.</summary>
    public void AddComputers(IEnumerable<string> names)
    {
        var existing = new HashSet<string>(Computers.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        var added = new List<Computer>();
        // Load settings once before the loop — StagedHosts is the persisted set of host names the
        // operator has flagged for the DISM lane; seed each new row's flag from it now.
        HashSet<string> stagedHosts = _appSettings.Load().StagedHosts;
        foreach (string name in names.Select(n => n.Trim()).Where(n => n.Length > 0))
        {
            if (existing.Add(name))
            {
                var computer = new Computer(name) { LastStatus = "Not checked" };
                computer.RequiresStagedPatching = StagedHostMatching.IsStaged(stagedHosts, name);
                Computers.Add(computer);
                added.Add(computer);
            }
        }

        if (added.Count == 0)
        {
            return;
        }

        // Check newly-added rows right away rather than waiting for the next monitor tick.
        if (IsMonitoring && _monitorCts is { } cts)
        {
            _ = MonitorRowsAsync(added, cts.Token);
        }

        // "Live by default": run the FULL Check Vitals pass on the new rows — exactly what the Check
        // Vitals button does (SCCM client health, the health dots, AND the 0-100 vitals score) — so the
        // grid's data is just there on load. Unless turned off (Settings ▸ Auto-check on load). Scoped to
        // the rows just loaded; the manual button stays for re-checks.
        if (AutoCheckOnLoadEnabled())
        {
            // Defer the auto-check kickoff to AFTER the just-added rows render. Kicking it inline runs the
            // sweep's synchronous prologue (a "Checking…" write per row + the first ping batch) on the UI
            // thread before the grid can paint, and on a cold process the first remote call's continuation pays
            // a one-time PowerShell SDK warm-up there too — starving the shell's first paint for several seconds.
            // At Background priority (below Render/Input/Loaded) the loaded rows lay out and paint first, then
            // vitals begin a render cycle later. Auto-check still runs on load — only its START moves later; the
            // heavy remote work (runspace open / invoke) is already off the UI thread. Capture the exact rows
            // just added so the deferred sweep's scope is unchanged, and run inline when there's no dispatcher
            // (e.g. tests) so behaviour is preserved there.
            IReadOnlyList<Computer> toSweep = added;
            void KickAutoCheck()
            {
                // Vitals registers rows in the held-rows registry (preventing conflicting ops); the custom-column
                // fill runs passively (no registration) so it never blocks and is never blocked by the sweep.
                _ = RunSweepAsync(toSweep, CheckHealthAndVitalsRowAsync, "Checking vitals");
                _ = RunCustomColumnsSelectedAsync(toSweep);
            }

            Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                KickAutoCheck();
            }
            else
            {
                _ = dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, (Action)KickAutoCheck);
            }

            // Reap any orphaned Vivre_Reboot_* services on the just-loaded hosts — leftovers of the
            // SMB/SCM reboot fallback's best-effort delete losing the race with the reboot. Inside the
            // auto-check gate deliberately: it is the app's "may Vivre reach out to every box on load?"
            // consent switch. ApplicationIdle (below the vitals Background kickoff): the reaper has zero
            // first-paint value and must not compete with the vitals burst. ReapAsync owns all its
            // faults (never throws) and dedups per session, so tab churn never re-sweeps.
            string[] toReap = [.. added.Select(c => c.Name)];
            if (dispatcher is null)
            {
                _ = _reaper.ReapAsync(toReap);
            }
            else
            {
                _ = dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                    (Action)(() => _ = _reaper.ReapAsync(toReap)));
            }
        }
    }

    // Reads the persisted "auto-check on load" flag (default on). Read at load time so a runtime toggle
    // in Settings takes effect on the next list load without restarting.
    private bool AutoCheckOnLoadEnabled()
    {
        try
        {
            return _appSettings.Load().AutoCheckOnLoad;
        }
        catch (Exception ex)
        {
            _activity.Warn(null, $"Couldn't read the auto-check-on-load setting — defaulting to on. {ex.Message}");
            return true;
        }
    }

    // --- Named machine lists (Lists ▾ button) ---

    /// <summary>Names of the saved machine lists.</summary>
    public IReadOnlyList<string> SavedLists() => _lists.List();

    /// <summary>Loads a saved list into the grid and renames the tab to the list's name.</summary>
    public void OpenList(string name)
    {
        SetComputers(_lists.Load(name));
        Title = name;
    }

    /// <summary>Saves the current grid as a named list.</summary>
    public void SaveCurrentAsList(string name) => _lists.Save(name, Computers.Select(c => c.Name));

    /// <summary>Deletes a saved list.</summary>
    public void DeleteList(string name) => _lists.Delete(name);

    /// <summary>True when the tab has at least one row NOT currently held by the operation registry
    /// (or when no rows exist yet — the old IsBusy gate kept all commands off until busy cleared,
    /// but with row-disjoint concurrency the buttons enable as soon as any row is free).</summary>
    private bool CanStartSweep() => Computers.Count == 0 || HasFreeRows();

    /// <summary>CanExecute for ScanTargetCommand and InstallTargetCommand: the toolbar Scan/Install all
    /// buttons must also be disabled when the tab has no machines to avoid kicking an empty sweep.</summary>
    private bool CanScanOrInstallAll() => Computers.Count > 0 && HasFreeRows();

    /// <summary>Bug 1: Install all button (Click handler, not RelayCommand) uses this for IsEnabled.
    /// Mirrors the CanExecute of InstallTargetCommand so both paths stay in sync.</summary>
    public bool CanInstallAll => Computers.Count > 0 && HasFreeRows();

    /// <summary>True when at least one row in the tab is not held by any active operation.</summary>
    private bool HasFreeRows() => Computers.Any(c => !_heldRows.ContainsKey(c.Name));

    /// <summary>Live tooltip for the Stop button: "Stop all — N operations running" (or singular form).</summary>
    public string StopTooltip
    {
        get
        {
            int n = _activeCts.Count;
            return n <= 1
                ? "Stop — cancel all running operations in this tab."
                : $"Stop all — {n} operations running.";
        }
    }

    // Stop is available whenever ANY work is in progress — a running sweep (IsBusy), the background monitor,
    // OR any row left in a transient working state (Scanning/Downloading/Installing/Uninstalling). The last
    // clause is the escape hatch: even if a sweep faulted and cleared IsBusy while rows are stranded, Stop
    // stays clickable so the operator can recover them without restarting Vivre.
    private bool CanStop() => IsBusy || IsMonitoring || AnyRowWorking();

    private bool AnyRowWorking() =>
        Computers.Any(c => c.PatchState is PatchState.Scanning or PatchState.Downloading or PatchState.Installing or PatchState.Uninstalling);

    /// <summary>Pings every row (reachability only — no SCCM health).</summary>
    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanStartSweep))]
    private Task PingAllAsync() => RunSweepAsync([.. Computers], PingRowAsync, "Pinging");

    /// <summary>Re-pings the rows not currently online (offline or never checked).</summary>
    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanStartSweep))]
    private Task PingOfflineAsync() => RunSweepAsync([.. Computers.Where(c => c.IsOnline != true)], PingRowAsync, "Pinging offline");

    /// <summary>Pings and pulls SCCM client health for every row (health is attempted even if ping fails).</summary>
    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanStartSweep))]
    private Task CheckAllAsync() => RunSweepAsync([.. Computers], CheckRowAsync, "Checking health");

    /// <summary>The single "Check Vitals" button: SCCM client health AND deep OS vitals (disk / memory /
    /// CPU / uptime / stopped services / recent errors, scored 0-100) for every row — one click does
    /// both. Read-only, no confirm. Each row's health+vitals pass holds ONE active-pool slot from the shared
    /// <see cref="_remoteSweepThrottle"/> (see <see cref="CheckHealthAndVitalsRowAsync"/>), so every open
    /// tab draws from one app-wide budget and they interleave fairly.</summary>
    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanStartSweep))]
    private Task CheckVitalsAsync() => RunSweepAsync([.. Computers], CheckHealthAndVitalsRowAsync, "Checking vitals");

    /// <summary>Reads vitals for just the given rows (right-click ▸ Triage on a single row); empty ⇒ all.</summary>
    public Task CheckVitalsSelectedAsync(IReadOnlyList<Computer> rows) =>
        RunSweepAsync(rows.Count > 0 ? rows : [.. Computers], CheckVitalsRowAsync, "Checking vitals");

    /// <summary>Per-row work for the combined "Check Vitals" sweep: SCCM client health, then OS vitals,
    /// under ONE shared-throttle slot held across both halves. Holding a single slot (instead of acquiring
    /// once for health and again for vitals) keeps each row's two reads contiguous, so a row goes from
    /// "Checking…" straight through to a score and open tabs interleave fairly — rather than the whole fleet
    /// finishing health first and only then starting vitals (which made a second tab show "health
    /// unavailable" and then sit idle until the first tab's vitals had all completed).</summary>
    private async Task CheckHealthAndVitalsRowAsync(Computer computer, CancellationToken token)
    {
        computer.LastStatus = "Checking…"; // immediate pending state before queueing, so a waiting row never looks idle
        await _remoteSweepThrottle.Active.WaitAsync(token);
        try
        {
            // Skip the vitals half when the health core found the box genuinely offline (unreachable by ping
            // AND ambient DCOM) — no point paying the vitals timeout on a box we already know is down.
            if (await CheckRowCoreAsync(computer, token))
            {
                await CheckVitalsCoreAsync(computer, token);
            }
        }
        finally
        {
            _remoteSweepThrottle.Active.Release();
        }
    }

    /// <summary>Runs health + vitals for just the given rows (e.g. the single-machine "Check Vitals"
    /// button in the detail window); empty ⇒ all rows. Uses <see cref="CheckHealthAndVitalsRowAsync"/>
    /// so both the SCCM health half and the OS vitals half run in one throttle slot.</summary>
    public Task CheckHealthAndVitalsSelectedAsync(IReadOnlyList<Computer> rows) =>
        RunSweepAsync(rows.Count > 0 ? rows : [.. Computers], CheckHealthAndVitalsRowAsync, "Checking vitals");

    /// <summary>Returns true when <paramref name="c"/> is currently held by a running operation
    /// (i.e. a sweep has it registered in the held-rows registry). Used by the detail window to
    /// disable its "Check Vitals" button while a fleet sweep is already touching the machine.</summary>
    internal bool IsRowBusy(Computer c) => _heldRows.ContainsKey(c.Name);

    /// <summary>Builds the per-machine Details view-model, wired for triage: the remediation service,
    /// the session credential, a combined health+vitals refresh (used by the "Check Vitals" button in
    /// the detail window), and a can-start delegate that gates the button while the machine is held.</summary>
    public ComputerDetailViewModel CreateDetailViewModel(Computer computer) =>
        new(
            computer,
            _activity,
            _remediation,
            CurrentPsCredential,
            () => CheckVitalsSelectedAsync([computer]),
            async () =>
            {
                await CheckHealthAndVitalsSelectedAsync([computer]);
                await FetchOperatingSystemAsync(computer);
            },
            () => !IsRowBusy(computer));

    // --- Software check (ad-hoc "is product X installed?" → the Software column) ---

    /// <summary>Checks the given rows for an installed product whose name contains <paramref name="query"/>
    /// (right-click ▸ Check software…); empty ⇒ all rows. When <paramref name="serviceName"/> is given,
    /// also reports whether the matching service is running. Fills each row's Software column with the
    /// match + version (+ service state). Read-only; no confirm.</summary>
    public Task CheckSoftwareSelectedAsync(IReadOnlyList<Computer> rows, string query, string? serviceName = null) =>
        RunSweepAsync(rows.Count > 0 ? rows : [.. Computers], (c, ct) => CheckSoftwareRowAsync(c, query, serviceName, ct), "Checking software");

    // Per-row software check: a light, read-only registry (+ optional service) query, bounded by the
    // scan throttle with a per-host timeout so a hung box can't stall the sweep.
    private async Task CheckSoftwareRowAsync(Computer computer, string query, string? serviceName, CancellationToken token)
    {
        await _remoteSweepThrottle.Active.WaitAsync(token);
        try
        {
            // Skip the doomed software check on a genuinely-offline box (unreachable by ping AND ambient
            // DCOM — the check's only two transports are WinRM and DCOM, so both channels dead means the
            // check cannot succeed). Write a clean "Offline" instead of burning a timeout into a
            // misleading "WinRM unavailable". Per-sweep: a box that answers on a later check re-runs
            // and refills normally.
            if (await IsGenuinelyOfflineAsync(computer.Name, token))
            {
                computer.SoftwareFound = null;
                computer.SoftwareServiceDown = null;
                computer.SoftwareCheck = "Offline";
                computer.SoftwareQuery = query;
                computer.SoftwareServiceName = string.IsNullOrWhiteSpace(serviceName) ? null : serviceName;
                computer.SoftwareName = null;
                computer.SoftwareVersion = null;
                computer.SoftwareServiceState = null;
                return;
            }

            computer.SoftwareFound = null;
            computer.SoftwareServiceDown = null;
            computer.SoftwareCheck = $"Checking {query}…";
            // Capture the raw fields for the on-demand CSV report (cleared per run, set on completion).
            computer.SoftwareQuery = query;
            computer.SoftwareServiceName = string.IsNullOrWhiteSpace(serviceName) ? null : serviceName;
            computer.SoftwareName = null;
            computer.SoftwareVersion = null;
            computer.SoftwareServiceState = null;

            using var perHost = CancellationTokenSource.CreateLinkedTokenSource(token);
            perHost.CancelAfter(TimeSpan.FromSeconds(SoftwarePerHostTimeoutSeconds));
            try
            {
                SoftwareCheckResult r = await _software.CheckAsync(computer.Name, query, serviceName, CurrentPsCredential(), perHost.Token);
                computer.SoftwareName = r.Name;
                computer.SoftwareVersion = r.Version;
                computer.SoftwareServiceState = r.ServiceState;
                computer.SoftwareCheckedAt = DateTime.Now;
                if (r.Found)
                {
                    string label = string.IsNullOrWhiteSpace(r.Version) ? r.Name ?? query : $"{r.Name} {r.Version}";
                    // Append the service verdict when one was checked; flag "installed but not running" amber.
                    bool serviceDown = r.ServiceState is not null && !string.Equals(r.ServiceState, "Running", StringComparison.OrdinalIgnoreCase);
                    string servicePart = r.ServiceState switch
                    {
                        null => string.Empty,
                        "Running" => " · running",
                        "not found" => " · no service",
                        _ => $" · {r.ServiceState.ToLowerInvariant()}",
                    };
                    computer.SoftwareFound = true;
                    computer.SoftwareServiceDown = serviceDown;
                    computer.SoftwareCheck = label + servicePart;
                    _activity.Info(computer.Name, $"Software check '{query}' — found: {label}{servicePart}");
                }
                else
                {
                    computer.SoftwareFound = false;
                    computer.SoftwareServiceDown = false;
                    computer.SoftwareCheck = $"{query} — not found";
                    _activity.Info(computer.Name, $"Software check '{query}' — not found");
                }
            }
            catch (OperationCanceledException) when (perHost.IsCancellationRequested && !token.IsCancellationRequested)
            {
                computer.SoftwareFound = null;
                computer.SoftwareCheck = "check timed out";
                computer.LastError = $"Software check timed out after {SoftwarePerHostTimeoutSeconds}s";
                _activity.Warn(computer.Name, computer.LastError);
            }
            catch (OperationCanceledException)
            {
                computer.SoftwareCheck = "cancelled";
                throw;
            }
            catch (Exception ex) when (ex.IsWinRmUnavailable())
            {
                computer.SoftwareFound = null;
                computer.SoftwareCheck = "WinRM unavailable";
                computer.LastError = "WinRM is broken on this box, so the software check can't run remotely here.";
                _activity.Warn(computer.Name, $"Software check '{query}' skipped — WinRM unavailable on this box.");
            }
            catch (Exception ex)
            {
                computer.SoftwareFound = null;
                computer.SoftwareCheck = "check failed";
                computer.LastError = ex.Message;
                _activity.Warn(computer.Name, $"Software check '{query}' failed — {ex.Message}");
            }
        }
        finally
        {
            _remoteSweepThrottle.Active.Release();
        }
    }

    // --- Custom grid columns (user-defined script-backed columns + hide/show built-ins) ---

    private void LoadColumnLayout()
    {
        try
        {
            AppSettings s = _appSettings.Load();
            foreach (CustomColumnSpec spec in s.CustomColumns)
            {
                CustomColumns.Add(spec);
            }

            foreach (string header in s.HiddenColumns)
            {
                HiddenColumns.Add(header);
            }
        }
        catch (Exception ex)
        {
            _activity.Warn(null, $"Couldn't load saved column layout — using defaults. {ex.Message}");
        }
    }

    // Load-modify-save so we don't clobber other settings (theme, etc.) saved meanwhile.
    private void SaveColumnLayout()
    {
        try
        {
            AppSettings s = _appSettings.Load();
            s.CustomColumns = [.. CustomColumns];
            s.HiddenColumns = [.. HiddenColumns];
            _appSettings.Save(s);
        }
        catch (Exception ex)
        {
            _activity.Warn(null, $"Couldn't save column layout: {ex.Message}");
        }
    }

    /// <summary>Adds (or replaces by name) a custom column and persists. The view picks up the collection
    /// change to build the grid column; the caller typically runs it afterwards to fill values.</summary>
    public void AddCustomColumn(CustomColumnSpec spec)
    {
        CustomColumnSpec? existing = CustomColumns.FirstOrDefault(c => string.Equals(c.Name, spec.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            CustomColumns.Remove(existing);
        }

        CustomColumns.Add(spec);
        SaveColumnLayout();
    }

    /// <summary>Removes a custom column by name and persists. The view removes the grid column.</summary>
    public void RemoveCustomColumn(string name)
    {
        CustomColumnSpec? existing = CustomColumns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            CustomColumns.Remove(existing);
            SaveColumnLayout();
        }
    }

    /// <summary>Hides/shows a built-in column (by header) and persists; the view applies the visibility.</summary>
    public void SetColumnHidden(string header, bool hidden)
    {
        bool has = HiddenColumns.Contains(header);
        if (hidden && !has)
        {
            HiddenColumns.Add(header);
        }
        else if (!hidden && has)
        {
            HiddenColumns.Remove(header);
        }
        else
        {
            return;
        }

        SaveColumnLayout();
    }

    /// <summary>Runs every custom column's script across the given rows (empty ⇒ all) and fills each row's
    /// values. One combined call per host (not per column). Read-only; no confirm.
    /// Runs in passive mode: does NOT register rows in the held-rows registry, so it never blocks
    /// and is never blocked by concurrent vitals/scan/install operations. Stop still cancels it.
    /// Uses the shared app-wide <see cref="_remoteSweepThrottle"/> reserved-priority budget — guaranteed progress
    /// even when the active pool is saturated by a concurrent vitals/scan sweep.</summary>
    public Task RunCustomColumnsSelectedAsync(IReadOnlyList<Computer> rows)
    {
        // Custom columns are a Health-grid feature: SyncColumns targets ComputerGrid only and
        // UpdateGrid has no custom-column rendering path, so running fills on a Patching tab
        // wastes remote probes for columns the grid can never display.
        // Also skip when no specs are configured — a zero-spec pass would narrate an op that
        // does nothing ("Custom columns 0/N").
        if (IsUpdateMode || CustomColumns.Count == 0)
        {
            return Task.CompletedTask;
        }

        IReadOnlyList<CustomColumnSpec> specs = [.. CustomColumns];
        return RunSweepAsync(rows.Count > 0 ? rows : [.. Computers], (c, ct) => RunCustomColumnRowAsync(c, specs, ct), "Custom columns", passive: true);
    }

    /// <summary>Runs a single custom column's script across the rows (empty ⇒ all) — used when a column is
    /// added, so only the new column fills and the others' already-fetched values aren't re-run.
    /// Runs in passive mode (see <see cref="RunCustomColumnsSelectedAsync"/>).</summary>
    public Task RunCustomColumnAsync(IReadOnlyList<Computer> rows, CustomColumnSpec spec)
    {
        // Custom columns are a Health-grid feature; the Patching grid (UpdateGrid) never renders
        // them, so skip the fill entirely on update-mode tabs.
        if (IsUpdateMode)
        {
            return Task.CompletedTask;
        }

        return RunSweepAsync(rows.Count > 0 ? rows : [.. Computers], (c, ct) => RunCustomColumnRowAsync(c, [spec], ct), "Custom columns", passive: true);
    }

    // Per-row: one combined custom-column call, bounded by the scan throttle with a per-host timeout.
    // Uses AcquirePassiveAsync so the reserved pool guarantees prompt access even when the active pool
    // is fully held by a saturated vitals/scan sweep.
    private async Task RunCustomColumnRowAsync(Computer computer, IReadOnlyList<CustomColumnSpec> specs, CancellationToken token)
    {
        using IDisposable permit = await _remoteSweepThrottle.AcquirePassiveAsync(token);

        // Skip the doomed custom-column probe on a genuinely-offline box (unreachable by ping AND ambient
        // DCOM) — it would only burn a timeout and litter "timed out"/"WinRM n/a" in the cells. Write a clean
        // "Offline" instead. Per-sweep: a box that answers on a later sweep re-runs and refills normally.
        if (await IsGenuinelyOfflineAsync(computer.Name, token))
        {
            foreach (CustomColumnSpec spec in specs)
            {
                computer.CustomValues[spec.Name] = "Offline";
            }

            return;
        }

        foreach (CustomColumnSpec spec in specs)
        {
            computer.CustomValues[spec.Name] = "…";
        }

        using var perHost = CancellationTokenSource.CreateLinkedTokenSource(token);
        perHost.CancelAfter(TimeSpan.FromSeconds(SoftwarePerHostTimeoutSeconds));
        try
        {
            IReadOnlyDictionary<string, string?> values =
                await _customColumns.RunAsync(computer.Name, specs, CurrentPsCredential(), perHost.Token);
            foreach (CustomColumnSpec spec in specs)
            {
                computer.CustomValues[spec.Name] = values.TryGetValue(spec.Name, out string? v) ? v : null;
            }
        }
        catch (OperationCanceledException) when (perHost.IsCancellationRequested && !token.IsCancellationRequested)
        {
            foreach (CustomColumnSpec spec in specs)
            {
                computer.CustomValues[spec.Name] = "timed out";
            }

            _activity.Warn(computer.Name, $"Custom columns timed out after {SoftwarePerHostTimeoutSeconds}s");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex.IsWinRmUnavailable())
        {
            foreach (CustomColumnSpec spec in specs)
            {
                computer.CustomValues[spec.Name] = "WinRM n/a";
            }

            string swHint = ex is KerberosWrongPrincipalException ? WinRmDeadEnd.SoftwareRedirect : string.Empty;
            computer.LastError = "WinRM is broken on this box, so custom columns can't run remotely here." + swHint;
            _activity.Warn(computer.Name, $"Custom columns skipped — WinRM unavailable on this box.{swHint}");
        }
        catch (Exception ex)
        {
            foreach (CustomColumnSpec spec in specs)
            {
                computer.CustomValues[spec.Name] = "error";
            }

            computer.LastError = ex.Message;
            _activity.Warn(computer.Name, $"Custom columns failed — {ex.Message}");
        }
    }

    // --- Windows Update lane (scan / install) ---

    /// <summary>Scans every row for applicable updates from the selected source.</summary>
    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanStartSweep))]
    private Task ScanUpdatesAsync() => RunPatchSweepAsync([.. Computers], ScanRowAsync, "Scan", _remoteSweepThrottle.Active);

    /// <summary>Downloads + installs applicable updates on every row (via a one-time SYSTEM task per host).</summary>
    /// <remarks>Install/Uninstall sweeps pass an INFINITE per-host timeout (like 2016 Clean up): the old 3-hour
    /// wall clock tore down actively-progressing installs mid-run (and its cleanup deleted the progress file
    /// under the live watcher). Two nets replace it: the lane's silence watchdog
    /// (<see cref="PatchOptions.NoResponseTimeout"/>) fails a dead/hung SESSION fast, and the watcher's
    /// task-state death probe reports an agent that dies without a terminal line (crash, EDR kill, or the
    /// task's 12h ExecutionTimeLimit) within ~16s — so a box writing progress is never cut off, and a dead
    /// one never spins forever.</remarks>
    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanStartSweep))]
    private Task InstallUpdatesAsync() => RunPatchSweepAsync([.. Computers], (c, ct) => InstallRowAsync(c, ct), "Install",
        perHostTimeout: System.Threading.Timeout.InfiniteTimeSpan);

    /// <summary>Scans the given rows (right-click "Updates ▸ Scan"); empty ⇒ all rows.</summary>
    public Task ScanSelectedAsync(IReadOnlyList<Computer> rows) =>
        RunPatchSweepAsync(rows.Count > 0 ? rows : [.. Computers], ScanRowAsync, "Scan", _remoteSweepThrottle.Active);

    /// <summary>Installs on the given rows (right-click "Updates ▸ Install"); empty ⇒ all rows.</summary>
    public Task InstallSelectedAsync(IReadOnlyList<Computer> rows) =>
        RunPatchSweepAsync(rows.Count > 0 ? rows : [.. Computers], (c, ct) => InstallRowAsync(c, ct), "Install",
            perHostTimeout: System.Threading.Timeout.InfiniteTimeSpan);

    /// <summary>Registers a one-time SYSTEM task on each row to install at <paramref name="at"/>
    /// (instead of now). Populates the "Scheduled task" columns; the monitor clears them once the
    /// time passes.</summary>
    public Task ScheduleInstallSelectedAsync(IReadOnlyList<Computer> rows, DateTime at) =>
        RunPatchSweepAsync(rows.Count > 0 ? rows : [.. Computers], (c, ct) => InstallRowAsync(c, ct, at), "Schedule");

    // --- Software deploy (push a package + run it as SYSTEM) ---

    /// <summary>
    /// Stages an admin-supplied package to the given rows (right-click ▸ Stage software…). The source +
    /// destination are chosen in the Stage window; this runs a per-row copy through
    /// <see cref="IDeploymentService"/> (SMB, else WinRM) using the bounded patch sweep (a payload copy
    /// is heavy on the wire). Nothing is installed — the files are just delivered for the admin to run.
    /// </summary>
    /// <param name="sourcePath">The file or folder on this machine to copy.</param>
    /// <param name="sourceIsFolder">True when the source is a folder.</param>
    /// <param name="targetPath">The final file/folder path on each target.</param>
    /// <param name="packageName">For the activity log + row status.</param>
    public Task StageSelectedAsync(IReadOnlyList<Computer> rows, string sourcePath, bool sourceIsFolder, string targetPath, string packageName) =>
        // Bounded by the install throttle (a payload copy is heavy on the wire). No operationLabel:
        // staging tracks Command result, not PatchState, so the patch-style completion summary doesn't
        // apply — per-row results + the activity log carry the outcome.
        RunPatchSweepAsync(
            rows.Count > 0 ? rows : [.. Computers],
            (c, ct) => StageRowAsync(c, sourcePath, sourceIsFolder, targetPath, packageName, ct),
            operationLabel: null,
            CurrentInstallThrottle());

    /// <summary>Per-host stage: copy the package to the target and reflect the result on the row's
    /// Command result + the activity log. Nothing is executed on the target.</summary>
    private async Task StageRowAsync(Computer computer, string sourcePath, bool sourceIsFolder, string targetPath, string packageName, CancellationToken token)
    {
        computer.LastError = null;
        computer.CommandResult = $"Staging {packageName}…";
        try
        {
            StageResult result = await _deployment.StageAsync(
                computer.Name, sourcePath, sourceIsFolder, targetPath, CurrentPsCredential(), token);

            if (result.Ok)
            {
                computer.CommandResult = $"{packageName}: staged to {targetPath}";
                _activity.Info(computer.Name, $"Staged '{packageName}' to {targetPath}");
            }
            else
            {
                computer.CommandResult = $"{packageName}: {result.Message}";
                computer.LastError = result.Message;
                _activity.Error(computer.Name, $"Stage '{packageName}' failed — {result.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            computer.CommandResult = $"{packageName}: cancelled";
            throw;
        }
        catch (Exception ex)
        {
            computer.LastError = ex.Message;
            computer.CommandResult = $"{packageName}: failed — {ex.Message}";
            _activity.Error(computer.Name, $"Stage '{packageName}' failed — {ex.Message}");
        }
    }

    /// <summary>
    /// Registers a one-time SYSTEM scheduled task (<c>Vivre_Reboot</c>) on each selected machine that
    /// force-restarts it at <paramref name="at"/>. No agent needed — just <c>shutdown /r /f</c>.
    /// Populates the "Scheduled task" columns; the monitor clears them once the time passes.
    /// </summary>
    public async Task ScheduleRebootSelectedAsync(IReadOnlyList<Computer> rows, DateTime at, CancellationToken token = default)
    {
        // The operator's picked time means THIS host's wall-clock; the task is registered on the
        // remote box, so anchor it to an absolute UTC instant and assign it directly to StartBoundary
        // (a raw string keeps the Z that -At would strip). See ScheduleTimeFormatter.
        string startBoundary = ScheduleTimeFormatter.FormatStartBoundaryUtc(at);
        string script = $$"""
            $trigger   = New-ScheduledTaskTrigger -Once -At (Get-Date)
            $trigger.StartBoundary = '{{startBoundary}}'
            $action    = New-ScheduledTaskAction -Execute 'shutdown.exe' -Argument '/r /f /t 0 /c "Vivre scheduled reboot"'
            $principal = New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -RunLevel Highest
            $settings  = New-ScheduledTaskSettingsSet
            Register-ScheduledTask -TaskName 'Vivre_Reboot' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
            'OK'
            """;

        foreach (Computer computer in (rows.Count > 0 ? rows : [.. SelectedComputers]).ToList())
        {
            computer.LastError = null;
            computer.LastStatus = "Scheduling reboot…";
            try
            {
                PSExecutionResult result = IsLocalHost(computer.Name)
                    ? await _powerShell.RunLocalAsync(script, token)
                    : await _powerShell.RunRemoteAsync(computer.Name, script, CurrentPsCredential(), cancellationToken: token);

                if (result.HadErrors)
                {
                    computer.LastStatus = "Schedule failed";
                    computer.LastError = result.Errors.Count > 0 ? result.Errors[0] : "Register-ScheduledTask failed";
                    _activity.Error(computer.Name, $"Schedule reboot failed — {computer.LastError}");
                }
                else
                {
                    computer.ScheduledAction = "Reboot";
                    computer.ScheduledNextRun = at;
                    computer.LastStatus = $"Reboot scheduled for {at:g} (your time)";
                    computer.UpdateMessage = FormatScheduledMessage("Reboot", at);
                    _scheduledTasks[computer.Name] = at;
                    _activity.Info(computer.Name, $"Reboot scheduled for {at:g} (your time)");
                }
            }
            catch (OperationCanceledException)
            {
                computer.LastStatus = "Cancelled";
                throw;
            }
            catch (Exception ex)
            {
                computer.LastStatus = "Schedule failed";
                computer.LastError = ex.Message;
                _activity.Error(computer.Name, $"Schedule reboot failed — {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Cancels any pending Vivre scheduled task (install or reboot — <c>Vivre_*</c>) on each selected
    /// machine and clears its "Scheduled task" columns. Safe: it only removes pending tasks.
    /// </summary>
    public async Task CancelScheduledTaskSelectedAsync(IReadOnlyList<Computer> rows, CancellationToken token = default)
    {
        // Verify by absence: after the unregister, re-query and report what survived — the chip may
        // only clear on the explicit REMOVED proof. Two layers, BOTH load-bearing (do not remove
        // either): the REMOVED/REMAINING output proves what actually remains on the box, and the
        // !HadErrors gate in Classify catches a failed enumeration too — a suppressed
        // -EA SilentlyContinue error still sets HadErrors, while a genuine wildcard no-match sets
        // nothing (verified against real 5.1), so a broken Task Scheduler can't fake a REMOVED.
        // Unregister keeps its default error action so its failures land in HadErrors/Errors.
        const string script = "Get-ScheduledTask -TaskName 'Vivre_*' -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false; $rem = @(Get-ScheduledTask -TaskName 'Vivre_*' -ErrorAction SilentlyContinue); if ($rem.Count -gt 0) { 'REMAINING: ' + ($rem.TaskName -join ', ') } else { 'REMOVED' }";
        foreach (Computer computer in (rows.Count > 0 ? rows : [.. SelectedComputers]).ToList())
        {
            computer.LastError = null;
            computer.LastStatus = "Cancelling scheduled task…";
            try
            {
                PSExecutionResult result = IsLocalHost(computer.Name)
                    ? await _powerShell.RunLocalAsync(script, token)
                    : await _powerShell.RunRemoteAsync(computer.Name, script, CurrentPsCredential(), cancellationToken: token);

                ScheduledTaskCancelOutcome outcome = ScheduledTaskCancelOutcome.Classify(
                    result.HadErrors,
                    [.. result.Output.Select(static o => o?.BaseObject?.ToString() ?? o?.ToString() ?? string.Empty)],
                    result.Errors);

                if (outcome.Cleared)
                {
                    // Verified: no Vivre_* task remains. Clear the scheduled state but leave a cancel
                    // breadcrumb on the row instead of blanking it: write the SAME text to the Patching
                    // "Windows update message" column as to Fleet Health's "Last status", so both grids
                    // read identically. It stays until the row's next action naturally replaces it.
                    computer.ScheduledAction = null;
                    computer.ScheduledNextRun = null;
                    _scheduledTasks.TryRemove(computer.Name, out _);
                    string cancelStatus = ScheduledTaskMessage.CancelStatus(hadErrors: false);
                    computer.LastStatus = cancelStatus;
                    computer.UpdateMessage = cancelStatus;
                    _activity.Info(computer.Name, "Cancelled pending scheduled task(s).");
                }
                else
                {
                    // The unregister failed or couldn't be verified — a task (worst case Vivre_Reboot)
                    // may still fire, so the Scheduled chip and tracking stay honest (kept; the monitor
                    // still time-clears them at trigger time). The activity log is the durable record:
                    // the monitor nulls LastError on the next pass for an online box.
                    computer.LastStatus = outcome.Status;
                    computer.UpdateMessage = "Cancel failed — task may still fire";
                    computer.LastError = outcome.Detail;
                    _activity.Error(computer.Name, $"Cancel scheduled task failed — {outcome.Detail}");
                }
            }
            catch (OperationCanceledException)
            {
                computer.LastStatus = "Cancelled";
                throw;
            }
            catch (Exception ex)
            {
                computer.LastStatus = "Cancel failed";
                computer.LastError = ex.Message;
                _activity.Error(computer.Name, $"Cancel scheduled task failed — {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Scans only the focused machine — wired to the side-panel "Scan" button that appears inside
    /// the empty-state hint when this scope has never been scanned for the focused row. Distinct
    /// from the toolbar's ScanUpdates (which sweeps every row), so the user can fill in just the
    /// machine they're looking at without re-scanning the whole tab.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanScanFocused))]
    private Task ScanFocusedAsync() =>
        FocusedComputer is { } c ? RunPatchSweepAsync([c], ScanRowAsync, "Scan", _remoteSweepThrottle.Active) : Task.CompletedTask;

    private bool CanScanFocused() => FocusedComputer is { } c && !_heldRows.ContainsKey(c.Name);

    /// <summary>Installs only the ticked updates on the focused machine (the side panel's "Install checked").</summary>
    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanInstallChecked))]
    private Task InstallCheckedAsync() =>
        FocusedComputer is { } c
            ? RunPatchSweepAsync([c], (c, ct) => InstallRowAsync(c, ct), "Install",
                perHostTimeout: System.Threading.Timeout.InfiniteTimeSpan)
            : Task.CompletedTask;

    /// <summary>Whether "Install checked" can run: Applicable scope, a focused machine with a scanned
    /// checklist, not held by a running operation, not patching, and NOT with a reboot pending (a
    /// pending reboot means just-installed updates still read as applicable and installing more would
    /// just be deferred by the agent's boot-busy guard — reboot first). Re-evaluated as boxes toggle /
    /// reboot state flips.</summary>
    public bool CanInstallChecked =>
        !IsInstalledMode && FocusedComputer is { } c && c.ApplicableUpdates.Count > 0
        && !c.IsPatching && c.RebootRequired != true
        && !_heldRows.ContainsKey(c.Name);

    /// <summary>Number of updates in the focused machine's Applicable list that were installed in
    /// this session (i.e. have <see cref="SelectableUpdate.InstalledThisSession"/> set). Zero when
    /// none have been installed or no machine is focused. Used by the summary line in the Updates panel.</summary>
    public int FocusedSessionInstalledCount =>
        FocusedComputer?.ApplicableUpdates.Count(u => u.InstalledThisSession) ?? 0;

    /// <summary>True when at least one session-installed update on the focused machine reported a
    /// reboot as required. Drives the "reboot pending" qualifier in the summary line.</summary>
    public bool FocusedSessionRebootPending =>
        FocusedComputer?.ApplicableUpdates.Any(u => u.InstalledThisSession && u.InstalledThisSessionRebootPending) == true;

    /// <summary>
    /// Uninstalls only the ticked updates on the focused machine. Only enabled in Installed scope
    /// and when at least one ticked update is actually uninstallable. The confirmation dialog lives
    /// in the view's code-behind so the VM doesn't pop UI directly.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanUninstallChecked))]
    public Task UninstallCheckedAsync() =>
        FocusedComputer is { } c
            ? RunPatchSweepAsync([c], UninstallRowAsync, "Uninstall",
                perHostTimeout: System.Threading.Timeout.InfiniteTimeSpan)
            : Task.CompletedTask;

    /// <summary>Whether "Uninstall checked" can run: Installed scope, the focused row not held by a
    /// running operation, and at least one ticked update that's actually removable. Bound by the
    /// button's enable-state (the button uses a Click handler for its confirm dialog) and re-evaluated
    /// live as boxes toggle, so it's disabled before a scan and whenever nothing removable is ticked.</summary>
    public bool CanUninstallChecked =>
        IsInstalledMode && FocusedComputer is { } c && !c.IsPatching
        && !_heldRows.ContainsKey(c.Name)
        && c.InstalledUpdates.Any(u => u.IsSelected && u.IsUninstallable);

    /// <summary>Ticks every update in the focused machine's checklist.</summary>
    [RelayCommand]
    private void SelectAllUpdates() => SetAllUpdatesSelected(true);

    /// <summary>Unticks every update in the focused machine's checklist.</summary>
    [RelayCommand]
    private void SelectNoUpdates() => SetAllUpdatesSelected(false);

    private void SetAllUpdatesSelected(bool selected)
    {
        if (FocusedComputer is not { } c)
        {
            return;
        }

        bool installed = IsInstalledMode;
        ObservableCollection<SelectableUpdate> target = installed ? c.InstalledUpdates : c.ApplicableUpdates;
        foreach (SelectableUpdate update in target)
        {
            // "All" never selects a non-removable update in Installed scope (neither WUA nor DISM
            // can remove it, so ticking it is meaningless), and never re-ticks an update already
            // installed this session in Applicable scope (it can't be re-targeted). "None" always clears.
            bool tickable = installed ? update.IsUninstallable : !update.InstalledThisSession;
            update.IsSelected = selected && tickable;
        }
    }

    // --- checklist enable-state tracking ----------------------------------
    // The Install/Uninstall buttons depend on which updates are ticked, but an individual
    // SelectableUpdate.IsSelected toggle raises no VM-level notification. Track the focused,
    // active-scope checklist and refresh the button enable-state when its items toggle or the
    // collection is repopulated by a scan.

    private ObservableCollection<SelectableUpdate>? _trackedChecklist;

    private void RetrackChecklist()
    {
        if (_trackedChecklist is not null)
        {
            _trackedChecklist.CollectionChanged -= OnChecklistCollectionChanged;
            foreach (SelectableUpdate u in _trackedChecklist)
            {
                u.PropertyChanged -= OnChecklistItemChanged;
            }
        }

        _trackedChecklist = FocusedActiveUpdates;

        if (_trackedChecklist is not null)
        {
            _trackedChecklist.CollectionChanged += OnChecklistCollectionChanged;
            foreach (SelectableUpdate u in _trackedChecklist)
            {
                u.PropertyChanged += OnChecklistItemChanged;
            }
        }

        RefreshChecklistCommandState();
    }

    private void OnChecklistCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A scan repopulates the collection (Clear + Add) — re-hook every item, then refresh.
        if (_trackedChecklist is not null)
        {
            foreach (SelectableUpdate u in _trackedChecklist)
            {
                u.PropertyChanged -= OnChecklistItemChanged;
                u.PropertyChanged += OnChecklistItemChanged;
            }
        }

        RefreshChecklistCommandState();
        // Re-evaluate session-install summary when the list is replaced by a fresh scan.
        OnPropertyChanged(nameof(FocusedSessionInstalledCount));
        OnPropertyChanged(nameof(FocusedSessionRebootPending));
    }

    private void OnChecklistItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectableUpdate.IsSelected))
        {
            RefreshChecklistCommandState();
        }
        else if (e.PropertyName == nameof(SelectableUpdate.InstalledThisSession)
              || e.PropertyName == nameof(SelectableUpdate.InstalledThisSessionRebootPending))
        {
            OnPropertyChanged(nameof(FocusedSessionInstalledCount));
            OnPropertyChanged(nameof(FocusedSessionRebootPending));
        }
    }

    private void RefreshChecklistCommandState()
    {
        InstallCheckedCommand.NotifyCanExecuteChanged();
        UninstallCheckedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanInstallChecked));
        OnPropertyChanged(nameof(CanUninstallChecked));
    }

    /// <summary>Cancels the running sweep and halts continuous monitoring (the Monitor toggle restarts it).</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        // Cancel every running operation (there may be several overlapping — e.g. an install on one
        // machine and a scan on another). BeginStop in the PS host makes each cancel non-blocking.
        foreach (CancellationTokenSource cts in _activeCts.ToArray())
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Finished between the snapshot and here — nothing to cancel.
            }
        }

        IsMonitoring = false;

        // Recover any ORPHANED in-flight rows — a transient working state with no live operation holding
        // them (e.g. stranded by a prior fault). Rows a running op still holds are left to that op's own
        // cancellation (it sets "Cancelled"); these stragglers would otherwise sit in "Scanning" forever,
        // making restart the only escape. This is what makes Stop a genuine recovery, not just a cancel.
        foreach (Computer c in Computers)
        {
            if (!_heldRows.ContainsKey(c.Name) &&
                c.PatchState is PatchState.Scanning or PatchState.Downloading or PatchState.Installing or PatchState.Uninstalling)
            {
                c.UpdatePhase = PatchPhase.Idle.ToString();
                c.UpdateMessage = "Stopped";
                c.IsPatching = false; // clear the independent latch too, or the row stays locked out of scan/install
            }
        }

        StopCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Registers a new running operation: tracks its CTS (so Stop cancels it), optionally
    /// registers target rows in the row registry, and flips busy on. IsBusy stays on until the last
    /// concurrent op ends.</summary>
    /// <param name="label">Short human label for the sweep, e.g. "Checking vitals" (used by <see cref="SweepStatus"/>).</param>
    /// <param name="eligibleRows">The rows this operation will run on (already eligibility-filtered).</param>
    /// <param name="registerRows">When false (passive mode), rows are NOT added to <see cref="_heldRows"/> —
    /// the operation participates in narration and is Stop-cancellable, but it does not block any other op.</param>
    private (CancellationTokenSource Cts, OperationRecord Record) BeginOperation(string? label, IReadOnlyList<Computer> eligibleRows, bool registerRows = true)
    {
        var cts = new CancellationTokenSource();
        _activeCts.Add(cts);

        var record = new OperationRecord(label ?? string.Empty, eligibleRows.Count);
        _activeOps.Add(record);

        // Register each eligible row in the held-rows dictionary so concurrent ops can detect conflicts.
        // Passive operations skip this — they must not block or be blocked by other sweeps.
        if (registerRows)
        {
            foreach (Computer row in eligibleRows)
            {
                _heldRows[row.Name] = record;
            }
        }

        // Start the narration timer on the first op; subsequent ops just join the existing tick.
        if (_activeCts.Count == 1)
        {
            // 1 s ticker: re-evaluates SweepStatus (elapsed + counter) and the fleet band elapsed, so the
            // clock advances smoothly and the N/M counter keeps pace with completing rows.
            _sweepNarrationTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _sweepNarrationTimer.Tick -= OnSweepNarrationTick;
            _sweepNarrationTimer.Tick += OnSweepNarrationTick;
            _sweepNarrationTimer.Start();
        }

        IsBusy = true;

        // Notify commands that depend on free-row availability (a new op may have consumed the last free rows).
        RaiseCanExecuteForSweepCommands();

        return (cts, record);
    }

    /// <summary>Increments the per-row completion counter for the given operation record. Call from any thread (uses Interlocked).</summary>
    private static void IncrementSweepCompleted(OperationRecord record)
    {
        Interlocked.Increment(ref record._completed);
    }

    private void OnSweepNarrationTick(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SweepStatus));
        OnPropertyChanged(nameof(IsVitalsSweepRunning));
        OnPropertyChanged(nameof(FleetElapsed));
        OnPropertyChanged(nameof(FleetNofM));
    }

    /// <summary>Retires an operation; releases its held rows, clears busy and sweep narration when none remain.</summary>
    private void EndOperation(CancellationTokenSource cts, OperationRecord record)
    {
        int idx = _activeCts.IndexOf(cts);
        if (idx >= 0)
        {
            _activeCts.RemoveAt(idx);
            _activeOps.RemoveAt(idx);
        }

        cts.Dispose();

        // Release every row this operation held (in finally — all exit paths call EndOperation).
        foreach (KeyValuePair<string, OperationRecord> kv in _heldRows.ToList())
        {
            if (ReferenceEquals(kv.Value, record))
            {
                _heldRows.Remove(kv.Key);
            }
        }

        // Notify commands that free rows are available again.
        RaiseCanExecuteForSweepCommands();

        if (_activeCts.Count == 0)
        {
            // Stop narration timer and clear sweep state.
            if (_sweepNarrationTimer is { } st)
            {
                st.Stop();
                st.Tick -= OnSweepNarrationTick;
            }

            // M11: arm the 3-second hold-open so the fleet band lingers after completion.
            _fleetBandHeld = true;
            _fleetBandHoldTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _fleetBandHoldTimer.Stop();
            _fleetBandHoldTimer.Tick -= OnFleetBandHoldTick;
            _fleetBandHoldTimer.Tick += OnFleetBandHoldTick;
            _fleetBandHoldTimer.Start();

            IsBusy = false;

            // Refresh all narration/fleet properties now that the last op ended.
            OnPropertyChanged(nameof(SweepStatus));
            OnPropertyChanged(nameof(IsVitalsSweepRunning));
            OnPropertyChanged(nameof(FleetElapsed));
            OnPropertyChanged(nameof(FleetNofM));
            OnPropertyChanged(nameof(IsPatchOperationOrFleetHeld));
            OnPropertyChanged(nameof(StopTooltip));
        }
        else
        {
            // Still busy — refresh narration for the ops that remain.
            OnPropertyChanged(nameof(SweepStatus));
            OnPropertyChanged(nameof(IsVitalsSweepRunning));
            OnPropertyChanged(nameof(FleetElapsed));
            OnPropertyChanged(nameof(FleetNofM));
            OnPropertyChanged(nameof(StopTooltip));
        }
    }

    /// <summary>Raises CanExecuteChanged for every command that depends on free-row availability,
    /// and re-evaluates the computed CanInstallAll / StopTooltip properties.</summary>
    private void RaiseCanExecuteForSweepCommands()
    {
        PingAllCommand.NotifyCanExecuteChanged();
        PingOfflineCommand.NotifyCanExecuteChanged();
        CheckAllCommand.NotifyCanExecuteChanged();
        CheckVitalsCommand.NotifyCanExecuteChanged();
        ScanUpdatesCommand.NotifyCanExecuteChanged();
        InstallUpdatesCommand.NotifyCanExecuteChanged();
        InstallCheckedCommand.NotifyCanExecuteChanged();
        UninstallCheckedCommand.NotifyCanExecuteChanged();
        ScanFocusedCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ScanTargetCommand.NotifyCanExecuteChanged();
        InstallTargetCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanInstallAll));
        OnPropertyChanged(nameof(CanInstallChecked));
        OnPropertyChanged(nameof(CanUninstallChecked));
        OnPropertyChanged(nameof(CanActOnSelection));
        OnPropertyChanged(nameof(StopTooltip));
    }

    private void OnFleetBandHoldTick(object? sender, EventArgs e)
    {
        _fleetBandHoldTimer?.Stop();
        _fleetBandHeld = false;
        OnPropertyChanged(nameof(IsPatchOperationOrFleetHeld));
    }

    private bool _disposed;

    /// <summary>Releases the tab's background work when it's closed (and on app shutdown): cancels every
    /// in-flight operation and the continuous monitor loop, and disposes the monitor cancellation source.
    /// The scan/install/vitals SemaphoreSlim throttles are intentionally NOT disposed — a sweep just
    /// cancelled here may still call Release() as it unwinds, and they hold no unmanaged handle (we only use
    /// WaitAsync/Release, never AvailableWaitHandle), so the GC reclaims them safely once the work finishes.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();                 // cancels every active operation and flips IsMonitoring off (disposing _monitorCts)
        _monitorCts?.Dispose(); // belt-and-suspenders: covers the case where monitoring was already off
        _monitorCts = null;
        ((INotifyCollectionChanged)_computersView).CollectionChanged -= OnVisibleRowsChanged;

        if (_sweepNarrationTimer is { } st)
        {
            st.Stop();
            st.Tick -= OnSweepNarrationTick;
            _sweepNarrationTimer = null;
        }

        if (_fleetBandHoldTimer is { } fbt)
        {
            fbt.Stop();
            fbt.Tick -= OnFleetBandHoldTick;
            _fleetBandHoldTimer = null;
        }

        if (_fleetRecomputeTimer is { } frt)
        {
            frt.Stop();
            frt.Tick -= OnFleetRecomputeTick;
            _fleetRecomputeTimer = null;
        }

        // Defensive: drop the collection, per-row, focus, and checklist subscriptions wired up in the
        // constructor and as rows/focus change. These are tab-local cycles the GC can already reclaim once
        // the tab is unreferenced, so they aren't a live leak — but unhooking them here is the same hygiene
        // as the view/timer unsubscribes above, and mirrors RetrackChecklist's own teardown.
        Computers.CollectionChanged -= OnComputersChanged;
        foreach (Computer c in Computers)
        {
            c.PropertyChanged -= OnComputerStateChanged;
        }

        if (FocusedComputer is { } focused)
        {
            focused.PropertyChanged -= OnFocusedComputerPropertyChanged;
        }

        if (_trackedChecklist is not null)
        {
            _trackedChecklist.CollectionChanged -= OnChecklistCollectionChanged;
            foreach (SelectableUpdate u in _trackedChecklist)
            {
                u.PropertyChanged -= OnChecklistItemChanged;
            }

            _trackedChecklist = null;
        }
    }

    /// <summary>Drops the offline rows from the grid.</summary>
    [RelayCommand]
    private void RemoveOffline()
    {
        var removed = Computers.Where(c => c.IsOnline == false).ToList();
        foreach (Computer computer in removed)
        {
            Computers.Remove(computer);
            SelectedComputers.Remove(computer);
            ForgetHostState(computer.Name);
        }

        if (FocusedComputer is { } focused && !Computers.Contains(focused))
        {
            FocusedComputer = null;
        }

        if (removed.Count > 0)
        {
            _activity.Info(null, $"Removed {removed.Count} offline machine(s) from this tab.");
        }

        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(HasSelection));
    }

    /// <summary>Clears the Command result column on every row.</summary>
    [RelayCommand]
    private void ClearResults()
    {
        foreach (Computer computer in Computers)
        {
            computer.CommandResult = null;
        }
    }

    /// <param name="passive">When true the eligibility filter and skip messages are skipped entirely —
    /// ALL rows in <paramref name="rows"/> participate regardless of the held-rows registry — and rows
    /// are NOT registered in <see cref="_heldRows"/> (so concurrent operations are never blocked by this
    /// sweep). A CTS is still created (Stop cancels it) and an <see cref="OperationRecord"/> is still
    /// registered (narration / N-of-M stays honest). Used for the custom-column fill, which is purely
    /// read-only and must not block or be blocked by any patch/scan/vitals operation on the same rows.</param>
    private async Task RunSweepAsync(IReadOnlyList<Computer> rows, Func<Computer, CancellationToken, Task> operation, string? label = null, bool passive = false)
    {
        // No targets at all (e.g. Ping All on an empty tab) → silent no-op, exactly as before the
        // registry existed. The zero-ELIGIBLE message below is for "all targets busy" only.
        if (rows.Count == 0) return;

        IReadOnlyList<Computer> eligible;

        if (passive)
        {
            // Passive mode: run on every supplied row without consulting or updating the registry.
            eligible = rows;
            _activity.Info(null, $"Started {label ?? "sweep"} on {eligible.Count} (passive)");
        }
        else
        {
            // Eligibility filter: exclude rows held by any currently-running operation.
            var eligibleList = new List<Computer>();
            var skipped = new List<(Computer Row, string BlockingLabel)>();
            foreach (Computer row in rows)
            {
                if (_heldRows.TryGetValue(row.Name, out OperationRecord? holder))
                {
                    skipped.Add((row, holder.Label));
                }
                else
                {
                    eligibleList.Add(row);
                }
            }

            // Write skip messages BEFORE BeginOperation so the skipped rows are not in the new op's set.
            foreach ((Computer row, string blockingLabel) in skipped)
            {
                // Vitals/health/ping skip → CommandResult column.
                row.CommandResult = $"Skipped — busy ({blockingLabel} running)";
            }

            // Nothing eligible → log and return without starting an op at all.
            if (eligibleList.Count == 0)
            {
                string msg = rows.Count == 1
                    ? $"Nothing started — the targeted machine is busy"
                    : $"Nothing started — all {rows.Count} targeted machines are busy";
                _activity.Warn(null, msg);
                return;
            }

            // Log the launch line (includes skip count when some were skipped).
            string startMsg = skipped.Count > 0
                ? $"Started {label ?? "sweep"} on {eligibleList.Count} ({skipped.Count} skipped — busy)"
                : $"Started {label ?? "sweep"} on {eligibleList.Count}";
            _activity.Info(null, startMsg);

            eligible = eligibleList;
        }

        (CancellationTokenSource cts, OperationRecord record) = BeginOperation(label, eligible, registerRows: !passive);
        try
        {
            Task work = Task.WhenAll(eligible.Select(row => WrapWithCompletion(operation, row, record, cts.Token)));

            // Stop must free the UI immediately, even if a row's remote call is still unwinding
            // (e.g. a rebooting host on a WinRM connect). Race the work against cancellation
            // rather than blocking on it.
            Task cancelled = Task.Delay(Timeout.Infinite, cts.Token);
            if (await Task.WhenAny(work, cancelled) == work)
            {
                await work; // completed normally — observe results/exceptions
            }
            else
            {
                // Cancelled: stop waiting and let the orphaned work tear down on its own without
                // surfacing as an unobserved fault.
                _ = work.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException)
        {
            // User pressed Stop — finished rows keep their results.
        }
        finally
        {
            EndOperation(cts, record);
        }
    }

    /// <summary>Wraps a per-row sweep task so the completion counter is incremented after it finishes
    /// (success or handled failure). The increment is thread-safe via <see cref="IncrementSweepCompleted"/>.</summary>
    private async Task WrapWithCompletion(Func<Computer, CancellationToken, Task> operation, Computer row, OperationRecord record, CancellationToken token)
    {
        try
        {
            await operation(row, token);
        }
        finally
        {
            IncrementSweepCompleted(record);
        }
    }

    /// <summary>
    /// Like <see cref="RunSweepAsync"/> (shares Stop / IsBusy / the cancellation race) but
    /// bounded — installs are heavy, so each host runs under a <see cref="SemaphoreSlim"/>
    /// throttle and a per-host timeout so one stuck box never holds up the grid.
    /// </summary>
    /// <summary>Raised once when a Scan/Install/Uninstall sweep finishes (not on cancel).
    /// Carries the human summary AND the severity computed from real counts — the shell shows it as
    /// a tray balloon (unfocused) or the in-window completion bar (focused).</summary>
    public event Action<string, OperationSeverity>? OperationCompleted;

    /// <summary>Returns the semaphore to use as the install throttle, swapping to a fresh one if the
    /// operator has changed "Max simultaneous installs" in Settings since the last sweep. Called on the
    /// UI thread at sweep-start, so reference assignment is safe and no lock is needed.
    /// SAFE: in-flight sweeps captured the OLD semaphore at their start and keep using it — we never
    /// resize a live semaphore, so no in-flight slot accounting is disturbed. A brief overlap of an
    /// old sweep at the old cap and a new sweep at the new cap is acceptable; they use independent
    /// semaphores and the total concurrent load stays bounded.</summary>
    private SemaphoreSlim CurrentInstallThrottle()
    {
        int cap = ClampInstallCap(_appSettings.Load().MaxSimultaneousInstalls);
        if (cap != _patchThrottleCap)
        {
            // Operator changed "Max simultaneous installs". Swap to a fresh semaphore at the new cap.
            // SAFE: in-flight sweeps captured the OLD semaphore (it's passed BY REFERENCE into
            // RunPatchSweepAsync at their start) and keep using it — we never resize a live semaphore,
            // so no in-flight slot accounting is disturbed. Sweeps started AFTER the change use the new
            // cap. Reference assignment is atomic and this runs on the UI thread at sweep-start, so no
            // torn read. (A brief overlap of an old sweep at the old cap + a new sweep at the new cap is
            // acceptable and bounded — they use independent semaphores.)
            _patchThrottle = new SemaphoreSlim(cap);
            _patchThrottleCap = cap;
        }

        return _patchThrottle;
    }

    private async Task RunPatchSweepAsync(
        IReadOnlyList<Computer> rows,
        Func<Computer, CancellationToken, Task> operation,
        string? operationLabel = null,
        SemaphoreSlim? throttle = null,
        TimeSpan? perHostTimeout = null)
    {
        // Scans pass the high scan throttle; install/uninstall default to the current install cap.
        throttle ??= CurrentInstallThrottle();
        // Per-host timeout by sweep type: Install/Uninstall and 2016 Clean up pass INFINITE (the lane's
        // silence watchdog is their safety net — see InstallUpdatesAsync's remarks); the Reboot Wave passes
        // its own hard cap; Scan and the rest default to the shared PatchOptions timeout so a hung box
        // can't pin the grid.
        TimeSpan hostTimeout = perHostTimeout ?? _patchOptions.PerHostTimeout;

        // Derive a sweep narration label from the operation type so the ProgressRing isn't silent.
        string? narrationLabel = operationLabel switch
        {
            "Scan" => "Scanning for updates",
            "Install" => "Installing updates",
            "Uninstall" => "Uninstalling updates",
            "Schedule" => "Scheduling install",
            _ => operationLabel,
        };

        // No targets at all → silent no-op (mirrors RunSweepAsync; the zero-eligible message
        // below is reserved for "all targets busy").
        if (rows.Count == 0) return;

        // Eligibility filter: exclude rows held by any currently-running operation.
        var eligible = new List<Computer>();
        var skipped = new List<(Computer Row, string BlockingLabel)>();
        foreach (Computer row in rows)
        {
            if (_heldRows.TryGetValue(row.Name, out OperationRecord? holder))
            {
                skipped.Add((row, holder.Label));
            }
            else
            {
                eligible.Add(row);
            }
        }

        // Write skip messages to the patch-lane column (UpdateMessage) BEFORE BeginOperation.
        foreach ((Computer row, string blockingLabel) in skipped)
        {
            row.UpdateMessage = $"Skipped — busy ({blockingLabel} running)";
        }

        // Nothing eligible → log and return without starting an op at all.
        if (eligible.Count == 0)
        {
            string msg = rows.Count == 1
                ? $"Nothing started — the targeted machine is busy"
                : $"Nothing started — all {rows.Count} targeted machines are busy";
            _activity.Warn(null, msg);
            return;
        }

        // Log the launch line (includes skip count when some were skipped).
        string startMsg = skipped.Count > 0
            ? $"Started {operationLabel ?? "operation"} on {eligible.Count} ({skipped.Count} skipped — busy)"
            : $"Started {operationLabel ?? "operation"} on {eligible.Count}";
        _activity.Info(null, startMsg);

        (CancellationTokenSource cts, OperationRecord record) = BeginOperation(narrationLabel, eligible);
        bool cancelled = false;
        try
        {
            Task work = Task.WhenAll(eligible.Select(row => RunOnePatchHostAsync(row, operation, throttle, record, hostTimeout, cts.Token)));

            Task cancelledTask = Task.Delay(Timeout.Infinite, cts.Token);
            if (await Task.WhenAny(work, cancelledTask) == work)
            {
                await work;
            }
            else
            {
                cancelled = true;
                _ = work.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException)
        {
            // Stop pressed — finished/in-flight rows keep their results.
            cancelled = true;
        }
        finally
        {
            EndOperation(cts, record);
        }

        // One completion signal per sweep (not per row), only for a real finish.
        if (!cancelled && operationLabel is not null)
        {
            (string summary, OperationSeverity severity) = BuildCompletionSummary(operationLabel, eligible);
            OperationCompleted?.Invoke(summary, severity);
        }
    }

    private static (string Summary, OperationSeverity Severity) BuildCompletionSummary(string label, IReadOnlyList<Computer> rows)
    {
        if (string.Equals(label, "Schedule", StringComparison.Ordinal))
        {
            int scheduled = rows.Count(r => r.ScheduledNextRun is not null);
            int errs = rows.Count(r => r.PatchState == PatchState.Error);
            string s = $"Scheduled {scheduled} machine(s)";
            if (errs > 0) s += $", {errs} failed — see Activity log";
            // All scheduled = Success; any error = Warning (partial).
            OperationSeverity sev = errs == 0 ? OperationSeverity.Success : OperationSeverity.Warning;
            return (s, sev);
        }

        if (string.Equals(label, "Scan", StringComparison.Ordinal))
        {
            // A scan leaves EVERY machine in PatchState.Available (that's just "scan done") — so
            // count the ones that actually found updates by their update count, not the state.
            int avail = rows.Count(r => r.UpdatesAvailable > 0);
            int errs = rows.Count(r => r.PatchState == PatchState.Error);
            string s = $"Scan finished — {rows.Count} machine(s)";
            if (avail > 0) s += $", {avail} with updates";
            if (errs > 0) s += $", {errs} failed — see Activity log";
            OperationSeverity sev = errs == 0 ? OperationSeverity.Success
                : errs == rows.Count ? OperationSeverity.Error
                : OperationSeverity.Warning;
            return (s, sev);
        }

        // 2016 lane — Stage: the two-bucket result the operator asked for. A staged box ends amber
        // (RebootPending); an already-current box ends green (Done). Both are successes — only Error is red.
        if (string.Equals(label, "Stage", StringComparison.Ordinal))
        {
            int staged = rows.Count(r => r.PatchState == PatchState.RebootPending);
            int current = rows.Count(r => r.PatchState == PatchState.Done);
            int errs = rows.Count(r => r.PatchState == PatchState.Error);
            var bits = new List<string>(3);
            if (staged > 0) bits.Add($"{staged} staged — run Reboot Wave");
            if (current > 0) bits.Add($"{current} already current");
            if (errs > 0) bits.Add($"{errs} failed");
            string s = bits.Count > 0 ? $"Stage finished — {string.Join(", ", bits)}" : "Stage finished";
            if (errs > 0) s += " — see Activity log";
            OperationSeverity sev = errs == 0 ? OperationSeverity.Success
                : (staged > 0 || current > 0) ? OperationSeverity.Warning
                : OperationSeverity.Error;
            return (s, sev);
        }

        // 2016 lane — Clean up / Reboot Wave / Verify: a simple done/failed tally (reboot-pending rows are
        // still mid-commit during a wave, so they count as neither yet).
        if (label is "Clean up" or "Reboot Wave" or "Verify")
        {
            int ok = rows.Count(r => r.PatchState == PatchState.Done);
            int errs = rows.Count(r => r.PatchState == PatchState.Error);
            string s = $"{label} finished — {ok} ok";
            if (errs > 0) s += $", {errs} failed — see Activity log";
            OperationSeverity sev = errs == 0 ? OperationSeverity.Success
                : ok > 0 ? OperationSeverity.Warning
                : OperationSeverity.Error;
            return (s, sev);
        }

        // Install / Uninstall: compute real counts to drive severity. In a mixed "Install all" the reboot
        // bucket splits — a 2016 box at RebootPending was STAGED (awaiting the Reboot Wave), a non-2016 box
        // genuinely needs a reboot — so the operator sees the two outcomes distinctly (installed vs staged).
        int done = rows.Count(r => r.PatchState == PatchState.Done);
        int staged2016 = rows.Count(r => r.PatchState == PatchState.RebootPending && LcuRouting.Is2016(r.OsBuild));
        int needReboot = rows.Count(r => r.PatchState == PatchState.RebootPending && !LcuRouting.Is2016(r.OsBuild));
        int failed = rows.Count(r => r.PatchState == PatchState.Error);
        var parts = new List<string> { $"{done} installed" };
        if (staged2016 > 0) parts.Add($"{staged2016} staged — run Reboot Wave");
        if (needReboot > 0) parts.Add($"{needReboot} need reboot");
        if (failed > 0) parts.Add($"{failed} failed");
        string summary = $"{label} finished — " + string.Join(", ", parts);
        if (failed > 0) summary += " — see Activity log";

        // Any failure at all = Error severity unless something also succeeded (partial = Warning); all
        // failed with nothing installed/staged/reboot = Error.
        OperationSeverity severity;
        if (failed == 0)
        {
            severity = OperationSeverity.Success;
        }
        else if (done > 0 || staged2016 > 0 || needReboot > 0)
        {
            severity = OperationSeverity.Warning; // partial failure
        }
        else
        {
            severity = OperationSeverity.Error; // all failed
        }

        return (summary, severity);
    }

    private async Task RunOnePatchHostAsync(
        Computer row,
        Func<Computer, CancellationToken, Task> operation,
        SemaphoreSlim throttle,
        OperationRecord record,
        TimeSpan hostTimeout,
        CancellationToken token)
    {
        // No ConfigureAwait(false): the per-row operation writes data-bound Computer state (PatchState is a
        // live-filtering grid property), so its continuations MUST resume on the captured UI context.
        // Stripping the SynchronizationContext here — as ConfigureAwait(false) did — made ApplyStatus run on
        // a thread-pool thread under throttle contention (a large fleet), which threw "the calling thread
        // cannot access this object" on the live CollectionView and orphaned the rest of the sweep.
        await throttle.WaitAsync(token);
        try
        {
            using var perHost = CancellationTokenSource.CreateLinkedTokenSource(token);
            perHost.CancelAfter(hostTimeout);
            try
            {
                await operation(row, perHost.Token);
            }
            catch (OperationCanceledException) when (perHost.IsCancellationRequested && !token.IsCancellationRequested)
            {
                // Per-host timeout (not a user Stop): surface it, release the slot, and let the rest of the
                // sweep continue — a box never stays stuck in its transient (e.g. Scanning) state.
                row.UpdateError = $"Timed out after {FormatDownDuration(hostTimeout)}";
                row.UpdateMessage = "Timed out";
                row.UpdatePhase = PatchPhase.Error.ToString();
                _activity.Error(row.Name, row.UpdateError);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A per-row operation threw unexpectedly (transport/threading fault, a crashing callback).
                // NEVER let it fault the whole Task.WhenAll or strand this box in a transient state: mark it
                // failed and move on, so one bad box can't orphan the other 100+ in the sweep.
                row.UpdateError = ex.Message;
                row.UpdateMessage = "Failed";
                row.UpdatePhase = PatchPhase.Error.ToString();
                _activity.Error(row.Name, $"{(string.IsNullOrEmpty(record.Label) ? "Operation" : record.Label)} failed on {row.Name} — {ex.Message}");
            }
        }
        finally
        {
            throttle.Release();
            IncrementSweepCompleted(record); // count this host done regardless of outcome

            // Release THIS row from the held set the moment its own work finishes — don't wait for the
            // whole sweep's EndOperation (which only fires after Task.WhenAll, i.e. behind the SLOWEST box).
            // So a box that finishes early is immediately eligible for Reboot & verify or a new scan, and
            // the fleet's Scan/Install commands re-enable (CanStartSweep → HasFreeRows) as rows free instead
            // of staying disabled behind the slowest box. Safe: this finally runs on the UI thread (no
            // ConfigureAwait(false) on the awaits above — same invariant ApplyStatus relies on), so it never
            // races EndOperation (also UI-thread). ReferenceEquals guards against releasing a row that some
            // LATER operation now holds (the eligibility filter prevents that today, but it keeps the release
            // correct regardless). EndOperation's batch sweep stays as the safety-net for rows that never ran
            // (e.g. a sweep cancelled before a queued row started).
            if (_heldRows.TryGetValue(row.Name, out OperationRecord? holder) && ReferenceEquals(holder, record))
            {
                _heldRows.Remove(row.Name);
                RaiseCanExecuteForSweepCommands();
            }
        }
    }

    /// <summary>The calm "retrying" row state shown between transient WU reach retries — deliberately a
    /// quiet working state, NOT an error, so the operator sees nothing alarming while the silent
    /// re-dispatch runs. (<see cref="TransientRetryRunner"/> calls this before each backoff pause.)</summary>
    private static void SetTransientRetryingState(Computer computer, int retryNumber)
    {
        // CALLED OFF THE UI THREAD. This is the onRetrying callback TransientRetryRunner.RunAsync invokes
        // AFTER `await attempt(...).ConfigureAwait(false)`, so it runs on a thread-pool thread — NOT on the
        // sweep's UI continuation. UpdatePhase is a live-filtered grid property (PatchState derives from it),
        // and writing it off the UI thread re-shapes the live CollectionView on the writing thread → throws
        // "the calling thread cannot access this object" (this was the live v1.13.0 crash on a transient
        // retry, install AND scan). Unlike progress.Report — which the UI-built Progress<T> marshals for us —
        // a direct callback does NOT marshal, so we marshal here. Inline when already on the UI thread (or no
        // Application, e.g. unit tests).
        Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyTransientRetryingState(computer, retryNumber);
        }
        else
        {
            dispatcher.InvokeAsync(() => ApplyTransientRetryingState(computer, retryNumber));
        }
    }

    private static void ApplyTransientRetryingState(Computer computer, int retryNumber)
    {
        computer.UpdateError = null;
        computer.UpdateProgress = null;
        computer.UpdatePhase = PatchPhase.Scanning.ToString(); // blue "working" — never the red Error pill
        computer.UpdateMessage = $"Couldn't reach Windows Update — retrying ({retryNumber}/{MaxTransientRetries})…";
    }

    /// <summary>The honest, actionable message for an exhausted transient reach failure: names the HRESULT
    /// and the try count, never a bare code, and NEVER reads as "up to date".</summary>
    private static string BuildUnreachableMessage(string lastTransientMessage)
    {
        string code = TransientWuaError.FirstTransientToken(lastTransientMessage) ?? "transient network error";
        int tries = MaxTransientRetries + 1;
        return $"Couldn't reach Windows Update ({code}) after {tries} tries — likely a transient network issue, try again.";
    }

    private async Task ScanRowAsync(Computer computer, CancellationToken token)
    {
        // Never scan a row that's mid-install/uninstall — a scan would overwrite its live phase,
        // message and progress and clear its checklist. Leave the in-flight operation untouched.
        if (computer.IsPatching)
        {
            _activity.Info(computer.Name, "Scan skipped — an update operation is in progress on this machine.");
            return;
        }

        // Reachability gate: a WUA scan over WinRM/SMB against a box that's simply offline (powered off /
        // ping-down) only yields a scary "Can't reach over WinRM or SMB" remoting error. Short-circuit to a
        // calm "Offline" — for an explicit operator scan too (an offline box is offline either way). A
        // reachable box (even ping-only/unmanageable) is NOT short-circuited: its scan runs and surfaces the
        // honest remoting error. IsOnline null (never probed) → probe first so a null can neither slip a
        // doomed attempt through nor skip a scan without evidence the box is actually down.
        bool? reachable = computer.IsOnline;
        if (reachable is null)
        {
            (bool online, _) = await ProbeReachabilityAsync(computer, token);
            reachable = online;
            computer.IsOnline = online;
            computer.LastStatus = online ? "Online" : "Offline";
        }
        if (ReachabilityGating.ScanShouldShortCircuitOffline(reachable))
        {
            computer.UpdateError = null;
            computer.UpdatePhase = PatchPhase.Idle.ToString();
            computer.UpdateMessage = "Offline";
            return;
        }

        // A new operation supersedes any lingering past-event reboot notice (it has no other clearer, so it
        // would otherwise linger in the Reboot-message column across this unrelated op). Current-state notices
        // ("Offline since…" / "WinRM temporarily unavailable…") keep their own clearers — see RebootMessageText.
        if (RebootMessageText.IsTransientRebootNotice(computer.RebootMessage)) { computer.RebootMessage = null; }
        computer.UpdateError = null;
        computer.UpdateProgress = null;
        computer.UpdatePhase = PatchPhase.Scanning.ToString();
        computer.UpdateMessage = "Scanning…";
        // Capture the scope at scan start so a mid-scan scope toggle doesn't route the result into
        // the wrong per-scope cache.
        UpdateScope scopeAtScan = _patchOptions.Scope;
        try
        {
            // A transient WU reach hiccup (the proven SLS 0x80072EE2 timeout) re-runs the whole scan
            // silently, up to the retry budget; an exhausted reach failure resolves to an honest
            // "couldn't reach WU" state (never a false "up to date"), and a terminal error surfaces at once.
            HostPatchStatus status = await TransientRetryRunner.RunAsync(
                attempt: async ct =>
                {
                    // Each attempt gets its OWN fresh 5-min budget (linked to the operation token), so a
                    // slow/hung attempt is bounded independently rather than eating one shared budget across
                    // all attempts + backoffs. A per-attempt timeout (NOT a user Stop) is a reach failure →
                    // surface it as transient (HRESULT 0x80240438) so the runner retries; if every attempt
                    // times out it resolves to the honest "Can't reach WU" state — never a silent give-up.
                    using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    attemptCts.CancelAfter(TimeSpan.FromSeconds(ScanAttemptTimeoutSeconds));
                    try
                    {
                        return await _patch.ScanAsync(computer.Name, _patchOptions, _credentials.Current, attemptCts.Token);
                    }
                    catch (OperationCanceledException) when (attemptCts.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        return HostPatchStatus.Failed(
                            $"Windows Update scan didn't respond within {ScanAttemptTimeoutSeconds}s (HRESULT 0x80240438) — the update source wasn't fully reached.");
                    }
                },
                maxRetries: MaxTransientRetries,
                delay: TransientBackoffDelayAsync,
                // INVARIANT: a callback handed to a Core runner (onRetrying/buildExhausted/attempt) runs on
                // the runner's post-ConfigureAwait(false) context — a thread-pool thread, NOT this UI sweep
                // continuation — so it must NOT write UI-bound/live-filtered state directly; route via the
                // UI-built IProgress or marshal. SetTransientRetryingState marshals to the Dispatcher itself.
                onRetrying: n => SetTransientRetryingState(computer, n),
                buildExhausted: msg => HostPatchStatus.Unreachable(BuildUnreachableMessage(msg)),
                token);
            ApplyStatus(computer, status, scopeAtScan);
            // A scan that got a real answer proves the box's OS responded over WinRM/SMB (managed) — as
            // opposed to the "Can't reach over WinRM or SMB" both-transports-down failure (Phase.Error).
            // Mark it so a later reboot/drop shows the "waiting for it to come back" tracking (case 2).
            if (status.Phase is not PatchPhase.Error) { computer.WasConfirmedOnline = true; }
        }
        catch (OperationCanceledException)
        {
            // Clear any transient phase (Scanning/Installing/…) so a user Stop never leaves the row stuck
            // spinning — it resolves to Idle, or to amber RebootPending if a reboot is still flagged.
            computer.UpdatePhase = PatchPhase.Idle.ToString();
            computer.UpdateMessage = "Cancelled";
            throw;
        }
        catch (Exception ex)
        {
            computer.UpdateError = ex.Message;
            computer.UpdateMessage = "Scan failed";
            computer.UpdatePhase = PatchPhase.Error.ToString();
            _activity.Error(computer.Name, $"Scan failed — {ex.Message}");
        }
    }

    /// <summary>
    /// Per-host uninstall — same shape as <see cref="InstallRowAsync"/> but only ever runs when
    /// there's an explicit selection of uninstallable updates (no "uninstall everything by default"
    /// fallback). Per-host clone of the shared options carries the KB list and Scope=Installed.
    /// </summary>
    private async Task UninstallRowAsync(Computer computer, CancellationToken token)
    {
        if (computer.IsPatching)
        {
            return;
        }

        if (computer.InstalledUpdates.Count == 0
            || !computer.InstalledUpdates.Any(u => u.IsSelected && u.IsUninstallable))
        {
            computer.UpdateError = null;
            computer.UpdateProgress = null;
            computer.UpdatePhase = PatchPhase.Idle.ToString();
            computer.UpdateMessage = "No uninstallable updates selected";
            return;
        }

        string[] selectedKbs = [.. computer.InstalledUpdates
            .Where(u => u.IsSelected && u.IsUninstallable && !string.IsNullOrWhiteSpace(u.Kb))
            .Select(u => u.Kb!)];
        if (selectedKbs.Length == 0)
        {
            computer.UpdateError = null;
            computer.UpdateProgress = null;
            computer.UpdatePhase = PatchPhase.Idle.ToString();
            computer.UpdateMessage = "Selected updates have no KB id to target";
            return;
        }

        PatchOptions options = _patchOptions.Clone();
        options.Scope = UpdateScope.Installed;
        options.IncludeKbArticleIds = selectedKbs;

        // A new operation supersedes any lingering past-event reboot notice (see RebootMessageText / ScanRowAsync).
        if (RebootMessageText.IsTransientRebootNotice(computer.RebootMessage)) { computer.RebootMessage = null; }
        computer.IsPatching = true;
        computer.UpdateError = null;
        computer.UpdateProgress = 0;
        computer.UpdatePhase = PatchPhase.Scanning.ToString();
        computer.UpdateMessage = "Starting uninstall…";

        // Same late-line gate as InstallRowAsync: a line draining from the remote pipeline after the
        // operation resolved must not overwrite the terminal row state (flag stays on the UI context).
        bool operationEnded = false;
        var progress = new Progress<HostPatchStatus>(s =>
        {
            if (operationEnded)
            {
                return;
            }

            ApplyStatus(computer, s);
        });
        try
        {
            HostPatchStatus final = await _patch.UninstallAsync(computer.Name, options, _credentials.Current, progress, token);
            // Completion: a finished uninstall with any failure is NOT a success — force the red Error pill.
            ApplyStatus(computer, final, failuresAreErrors: true);
        }
        catch (OperationCanceledException)
        {
            // Clear any transient phase (Scanning/Installing/…) so a user Stop never leaves the row stuck
            // spinning — it resolves to Idle, or to amber RebootPending if a reboot is still flagged.
            computer.UpdatePhase = PatchPhase.Idle.ToString();
            computer.UpdateMessage = "Cancelled";
            throw;
        }
        catch (Exception ex)
        {
            computer.UpdateError = ex.Message;
            computer.UpdateMessage = "Uninstall failed";
            computer.UpdatePhase = PatchPhase.Error.ToString();
            _activity.Error(computer.Name, $"Uninstall failed — {ex.Message}");
        }
        finally
        {
            operationEnded = true;
            computer.IsPatching = false;
        }
    }

    /// <param name="minorOnly">True for the decision dialog's "Install minor updates only" branch: a flagged 2016
    /// box installs its non-CU updates via WUA, with the cumulative update KB explicitly excluded so the broken
    /// Express-delta CU is never pushed through WUA. Requires a scan (to know what to exclude).</param>
    private async Task InstallRowAsync(Computer computer, CancellationToken token, DateTime? scheduleAt = null, bool minorOnly = false)
    {
        // Don't start a second operation on a row already installing/uninstalling.
        if (computer.IsPatching)
        {
            return;
        }

        // Per-box routing by OS build (the single decision; LcuRouting.Is2016 is the same predicate the
        // self-populating 2016 panel uses). Confirmed Server 2016 → the full-package CU lane (the broken
        // Express-delta WUA pipeline is exactly what fails its monthly CU). An unread box is deliberately
        // NOT guessed onto either lane — a vitals check classifies it first. Everything else → WUA.
        // [DECISION SURFACED TO OPERATOR: unread → skip-with-nudge, not silent-fallback-to-WUA. Flip this
        //  one block to fall through if you'd rather an unclassified box just take the WUA path.]
        if (computer.OsBuild is null)
        {
            computer.UpdatePhase = PatchPhase.Idle.ToString();
            computer.UpdateMessage = "Unknown OS build — run Check Vitals first so Vivre can pick the right update lane.";
            return;
        }

        if (LcuRouting.Is2016(computer.OsBuild))
        {
            // The full-package DISM lane is only for boxes the operator has FLAGGED for staged patching (WUA's
            // Express-delta CU fails them). A NON-flagged 2016 box patches via normal Windows Update like a
            // 2019/2022 box, so it falls through to the WUA path below.
            if (computer.RequiresStagedPatching && !minorOnly)
            {
                // Flagged box: never auto-stage and never WUA-install the OS CU from here — the decision dialog,
                // shown up front by the View, owns that choice. Resolve the no-prompt cases; otherwise skip with
                // guidance so a path that reaches this row directly can't silently do the wrong thing.
                if (StagePreconditions.IsAlreadyStaged(computer.RebootRequired == true, computer.StagedThisSession))
                {
                    computer.UpdateMessage = "CU staged — run Reboot Wave before installing other updates.";
                    return;
                }

                // Same predicate the View's decision dialog uses (single source of truth) — blocks ONLY when an OS
                // CU actually needs staging: a scan that shows a Server 2016 OS CU, or an unscanned box we can't
                // clear. A freshly-scanned flagged box with NO OS CU (e.g. OS already current, only minor updates
                // like Office/Defender pending) has nothing to stage, so it falls through to the normal WUA install
                // of its ticked minor updates instead of being dead-ended here.
                if (StagedInstallPlanner.NeedsStageDecision(computer))
                {
                    computer.UpdatePhase = PatchPhase.Idle.ToString();
                    computer.UpdateMessage = "Needs CU staging — use the Install button in the toolbar to choose Stage or minor-only.";
                    return;
                }
                // else: nothing to stage (no OS CU in scan), or the CU is already verified/committed this session →
                // its remaining minor updates go via WUA below.
            }
            // minorOnly (operator chose "Install minor updates only") falls through to WUA with the CU excluded.
        }

        // "Install minor updates only" (decision dialog): exclude the OS cumulative update so the broken
        // Express-delta CU is never pushed through WUA. Needs a scan to know what to exclude — without one we
        // can't safely separate the CU from the rest, so skip rather than risk WUA-installing the CU.
        var cuExclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (minorOnly)
        {
            if (computer.ApplicableUpdates.Count == 0)
            {
                computer.UpdatePhase = PatchPhase.Idle.ToString();
                computer.UpdateMessage = "Scan first — minor-only install needs the scan to exclude the cumulative update.";
                return;
            }

            // SAFETY: the OS CU must never reach WUA on a flagged box. Require this month's CU to be set in
            // Settings (the same gate the Stage lane uses) so cuExclude always has the operator-declared KB as a
            // floor, then ALSO exclude EVERY CU-titled update the scan shows (CuKbs — not the single-confident
            // FindCuKb) so an ambiguous scan listing two CU KBs can't let one slip through.
            string? settingsCu = _appSettings.Load().MonthlyCu?.Kb;
            if (string.IsNullOrWhiteSpace(settingsCu))
            {
                computer.UpdatePhase = PatchPhase.Idle.ToString();
                computer.UpdateMessage = "Set this month's CU (KB) in Settings before installing minor updates only.";
                return;
            }

            cuExclude.Add(Lcu2016CuMatcher.NormalizeKb(settingsCu));
            cuExclude.UnionWith(Lcu2016CuMatcher.CuKbs(computer.ApplicableUpdates.Select(u => (u.Title, u.Kb))));
        }

        // Honor the per-machine checklist. Clone the shared options (concurrent hosts read it) and
        // scope this host's install to its ticked KBs: never scanned ⇒ no checklist ⇒ install all
        // (not-excluded, unchanged); scanned but nothing ticked ⇒ skip without launching a task.
        PatchOptions options = _patchOptions.Clone();
        if (scheduleAt is { } scheduledTime)
        {
            options.RunBehavior = RunBehavior.ScheduleAt;
            options.ScheduleAt = scheduledTime;
        }

        // Snapshot the target set now (before IsPatching is set) so we know which rows to mark
        // after a zero-failure install.  Must mirror the IncludeKbArticleIds computation below
        // EXACTLY (ticked AND has a KB id, minus the excluded CU in minor-only mode) — a ticked
        // KB-less update is silently not targeted by the agent, so marking it "Installed" would lie.
        // If no checklist exists (never scanned) the snapshot is empty — no rows to mark, but the
        // partial-failure banner still applies.
        bool IsTarget(SelectableUpdate u) =>
            u.IsSelected && !string.IsNullOrWhiteSpace(u.Kb)
            && (!minorOnly || !cuExclude.Contains(Lcu2016CuMatcher.NormalizeKb(u.Kb!)));

        SelectableUpdate[] targetSnapshot = computer.ApplicableUpdates.Count > 0
            ? [.. computer.ApplicableUpdates.Where(IsTarget)]
            : [];

        if (computer.ApplicableUpdates.Count > 0)
        {
            if (!computer.ApplicableUpdates.Any(u => u.IsSelected))
            {
                computer.UpdateError = null;
                computer.UpdateProgress = null;
                computer.UpdatePhase = PatchPhase.Idle.ToString();
                computer.UpdateMessage = "No updates selected";
                return;
            }

            string[] selectedKbs = [.. computer.ApplicableUpdates.Where(IsTarget).Select(u => u.Kb!)];
            if (selectedKbs.Length == 0)
            {
                computer.UpdateError = null;
                computer.UpdateProgress = null;
                computer.UpdatePhase = PatchPhase.Idle.ToString();
                computer.UpdateMessage = minorOnly
                    ? "No minor updates to install — only the cumulative update is pending. Use Stage CU first."
                    : "Selected updates have no KB id to target";
                return;
            }

            options.IncludeKbArticleIds = selectedKbs;
        }

        // A new operation supersedes any lingering past-event reboot notice (see RebootMessageText / ScanRowAsync).
        if (RebootMessageText.IsTransientRebootNotice(computer.RebootMessage)) { computer.RebootMessage = null; }
        computer.IsPatching = true;
        computer.UpdateError = null;
        computer.UpdateProgress = 0;
        computer.UpdatePhase = PatchPhase.Scanning.ToString();
        computer.UpdateMessage = scheduleAt is null ? "Starting…" : "Scheduling…";

        // Progress<T> marshals callbacks to the captured (UI) context; WPF also auto-marshals
        // the scalar property updates, so the grid stays current as the SYSTEM task reports in.
        // Suppress a TRANSIENT reach error mid-stream so the row never flashes red — the runner will
        // retry it silently. Normal progress (Scanning/Downloading/Installing) still applies live.
        // Lines can still drain from the remote pipeline after the operation has resolved (the pipeline
        // stop is asynchronous) — a late-arriving line must never overwrite the terminal row state. Both
        // the flag write (finally) and this read run on the captured UI context, so no marshalling needed.
        bool operationEnded = false;
        var progress = new Progress<HostPatchStatus>(s =>
        {
            if (operationEnded)
            {
                return;
            }

            if (s.Phase == PatchPhase.Error && TransientWuaError.IsTransient(s.Message))
            {
                return;
            }

            ApplyStatus(computer, s);
        });
        // Latch "install began" PRODUCER-SIDE (synchronously, on the streaming thread) rather than in
        // the posted Progress callback above: on retry attempts the re-entry guard below reads the flag
        // on a thread-pool continuation, and it raced the UI-posted write (the audit MED) — the queued
        // post could still be undrained when the guard read, re-running an install that had begun.
        var install = new InstallBeganLatch(progress);
        try
        {
            // Wrap the WHOLE install (service-registration → search → download → install): a transient
            // WU reach hiccup re-dispatches the entire operation silently, up to the retry budget; an
            // exhausted reach failure resolves to an honest "couldn't reach WU" state (never a false
            // "installed"/"up to date"), and a real install failure surfaces at once with no retry.
            HostPatchStatus final = await TransientRetryRunner.RunAsync(
                attempt: async ct =>
                {
                    HostPatchStatus r = await _patch.InstallAsync(computer.Name, options, _credentials.Current, install, ct);

                    // RE-ENTRY GUARD: once install has BEGUN, a late transient must NOT trigger a re-run —
                    // the re-search would find 0 applicable (already installed) and report a false "up to
                    // date"/zero, silently dropping the installed count and reboot-pending. Surface it as a
                    // terminal error (no transient HRESULT in the text, so the runner won't retry) telling
                    // the operator to re-scan. Search/download transients (install never began) still retry.
                    if (install.Began && r.Phase == PatchPhase.Error && TransientWuaError.IsTransient(r.Message))
                    {
                        return HostPatchStatus.Failed(
                            $"Install was interrupted after it began on {computer.Name} — some updates may have installed. Re-scan to confirm; not retried, to avoid dropping the installed count.");
                    }

                    return r;
                },
                maxRetries: MaxTransientRetries,
                delay: TransientBackoffDelayAsync,
                // INVARIANT: a callback handed to a Core runner (onRetrying/buildExhausted/attempt) runs on
                // the runner's post-ConfigureAwait(false) context — a thread-pool thread, NOT this UI sweep
                // continuation — so it must NOT write UI-bound/live-filtered state directly; route via the
                // UI-built IProgress or marshal. SetTransientRetryingState marshals to the Dispatcher itself.
                // The re-entry guard's began-flag is safe to read on the pool thread — InstallBeganLatch
                // latches it synchronously on the producing thread, so no UI post is in its path.
                onRetrying: n => SetTransientRetryingState(computer, n),
                buildExhausted: msg => HostPatchStatus.Unreachable(BuildUnreachableMessage(msg)),
                token);
            // Completion: a finished install with any failure is NOT a success — force the red Error pill.
            ApplyStatus(computer, final, failuresAreErrors: true);
            // An install that got a real answer reached the box's OS over WinRM/SMB (managed), unlike a
            // both-transports-down "Can't reach" failure (Phase.Error). Mark it so a subsequent reboot/drop
            // keeps its "waiting for it to come back" tracking (case 2).
            if (final.Phase is not PatchPhase.Error) { computer.WasConfirmedOnline = true; }
            // Stamp the post-reboot outcome counts ONLY for a real install outcome: a
            // failed/unreachable/deferred attempt or a ScheduleAt registration must not clobber the
            // counts of the install that actually set up the pending reboot, and an
            // installed-nothing pass has nothing to report ("installed 0" was the bug).
            if (scheduleAt is null
                && final.Phase is PatchPhase.Done or PatchPhase.PendingReboot
                && (final.InstalledCount > 0 || final.FailedCount > 0))
            {
                computer.LastInstallInstalledCount = final.InstalledCount;
                computer.LastInstallFailedCount = final.FailedCount;
            }

            // Scheduled (not run now): record the schedule and surface it as the row message. An exhausted
            // reach failure (Unreachable) is NOT a successful schedule — exclude it alongside Error.
            if (scheduleAt is { } when && final.Phase is not (PatchPhase.Error or PatchPhase.Unreachable))
            {
                computer.ScheduledAction = "Install updates";
                computer.ScheduledNextRun = when;
                computer.UpdateMessage = FormatScheduledMessage(computer.ScheduledAction, when);
                _scheduledTasks[computer.Name] = when;
            }

            // Reflect the install result in the per-machine Updates panel (display-only; no
            // install logic changes).  The protocol doesn't tell us which individual updates
            // succeeded vs failed, so:
            //   • Zero failures + ≥1 installed  → mark every item in the target snapshot
            //     as "installed this session" (greyed/struck chip); untick them so they are
            //     excluded from any future InstallChecked without re-selection.
            //   • Any failures                  → leave per-row state untouched; show an
            //     honest banner on the Computer instead.
            if (final.Phase is PatchPhase.Done or PatchPhase.PendingReboot
                && final.InstalledCount > 0
                && final.FailedCount == 0
                && scheduleAt is null)
            {
                bool rebootPending = final.RebootPending || final.Phase == PatchPhase.PendingReboot;
                foreach (SelectableUpdate item in targetSnapshot)
                {
                    item.InstalledThisSession = true;
                    item.InstalledThisSessionRebootPending = rebootPending;
                    // Untick so InstallChecked doesn't re-target already-installed items;
                    // the user would need to re-tick deliberately to re-install.
                    item.IsSelected = false;
                }
            }
            else if (final.Phase is PatchPhase.Done or PatchPhase.PendingReboot
                     && final.FailedCount > 0
                     && scheduleAt is null)
            {
                computer.LastInstallNote =
                    $"Install completed with {final.FailedCount} failure(s) — rescan after reboot for exact state.";
            }
        }
        catch (OperationCanceledException)
        {
            // Clear any transient phase (Scanning/Installing/…) so a user Stop never leaves the row stuck
            // spinning — it resolves to Idle, or to amber RebootPending if a reboot is still flagged.
            computer.UpdatePhase = PatchPhase.Idle.ToString();
            computer.UpdateMessage = "Cancelled";
            throw;
        }
        catch (Exception ex)
        {
            computer.UpdateError = ex.Message;
            computer.UpdateMessage = "Install failed";
            computer.UpdatePhase = PatchPhase.Error.ToString();
            _activity.Error(computer.Name, $"Install failed — {ex.Message}");
        }
        finally
        {
            operationEnded = true;
            computer.IsPatching = false;
        }
    }

    // --- Server 2016 full-package CU lane (the self-populating 2016 panel + "Install all" routing) ---

    /// <summary>This cycle's Server 2016 CU, e.g. "KB5094122 / 9234" — shown in the 2016 panel, set in
    /// Settings. The lane stages this KB and Verify checks the box reaches this UBR.</summary>
    public string MonthlyCuDisplay => _appSettings.Load().MonthlyCu?.Display ?? "(set in Settings)";

    /// <summary>The folder the operator drops the monthly CU <c>.msu</c> into (read-only display for the panel).</summary>
    public string LcuPackagesFolder => _appSettings.Load().LcuPackagesFolder;

    /// <summary>How many confirmed Server 2016 boxes are in this tab — drives the panel's count + visibility.
    /// Unread boxes (no OS build yet) don't count until a vitals check classifies them.</summary>
    public int Server2016Count => Computers.Count(c => LcuRouting.Is2016(c.OsBuild));

    /// <summary>True when this tab has at least one confirmed 2016 box (the panel/buttons are meaningful).</summary>
    public bool HasServer2016 => Server2016Count > 0;

    /// <summary>True when at least one 2016 box in this tab is flagged for staged patching — drives the grid's
    /// "Staged" pill column visibility (the whole column hides when nothing is flagged).</summary>
    public bool HasStagedServer2016 => StagePreconditions.HasAnyStageTarget(Computers);

    /// <summary>The rows the panel's Stage / Verify act on: the selected FLAGGED 2016 rows, or every
    /// flagged 2016 row when none are selected. A non-flagged 2016 box patches via Windows Update, so the DISM
    /// staging lane never touches it; non-2016 selections are ignored. (Clean up is decoupled from staged-state
    /// — see <see cref="Clean2016Targets"/>.)</summary>
    private IReadOnlyList<Computer> Server2016Targets()
    {
        // Staged patching is opt-in per box: the panel's Stage / Verify act ONLY on flagged 2016 boxes
        // (StagePreconditions.IsStageTarget). A non-flagged 2016 box patches via Windows Update and is never
        // touched by the DISM staging lane.
        var selected = SelectedComputers.Where(StagePreconditions.IsStageTarget).ToList();
        return selected.Count > 0 ? selected : [.. Computers.Where(StagePreconditions.IsStageTarget)];
    }

    /// <summary>The rows the panel's Clean up acts on: selection-driven and independent of staged-state — the
    /// selected 2016 boxes, or every 2016 box in the tab when none are selected. DISM component cleanup is
    /// self-contained and reboot-free, so it runs on ANY 2016 box (flagged or not) to reclaim WinSxS space and
    /// speed up normal Windows Update. Non-2016 selections are excluded. See <see cref="ComponentCleanupTargets"/>.</summary>
    private IReadOnlyList<Computer> Clean2016Targets() => ComponentCleanupTargets.Select(SelectedComputers, Computers);

    /// <summary>The 2016 Stage targets not yet scanned this session — the View blocks Stage and lists these
    /// until they're scanned. (A post-reboot rescan sets LastScannedApplicable and satisfies this gate.)</summary>
    public IReadOnlyList<string> UnscannedStageTargets() => StagePreconditions.UnscannedThisSession(Server2016Targets());

    // --- Staged-patching decision (the "Server 2016 staged update required" dialog) ---

    /// <summary>Public accessor for the panel's Stage / Clean up / Verify target set (flagged 2016 boxes), so the
    /// View's stage workflow can scope to exactly the boxes the panel buttons act on.</summary>
    public IReadOnlyList<Computer> Server2016ActionTargets() => Server2016Targets();

    /// <summary>Box-scoped variant of <see cref="UnscannedStageTargets"/>: the given Stage targets not yet scanned
    /// this session. Used by the decision dialog's "Stage CU first" branch (which acts on a specific set).</summary>
    public IReadOnlyList<string> UnscannedStageTargetsFor(IReadOnlyList<Computer> boxes) =>
        StagePreconditions.UnscannedThisSession(boxes);

    /// <summary>The staged-patching decision plan for an Install / Install-all target set: which flagged 2016 boxes
    /// still need their CU staged (the dialog set) versus which proceed via the normal install, plus any
    /// Settings-vs-scan CU KB mismatches. Pure — no host is contacted.</summary>
    public StagedInstallPlan PlanStagedInstall(IReadOnlyList<Computer> targets) =>
        StagedInstallPlanner.Plan(targets, _appSettings.Load().MonthlyCu?.Kb);

    /// <summary>Stage this month's CU on the given flagged 2016 boxes (decision dialog "Stage CU first"). Same
    /// per-host stage as the panel button, scoped to a specific set.</summary>
    public Task StageLcuForAsync(IReadOnlyList<Computer> boxes) =>
        RunPatchSweepAsync(boxes, (c, ct) => StageLcuRowAsync(c, null, ct), "Stage", CurrentInstallThrottle());

    /// <summary>Install only the NON-cumulative updates on the given flagged 2016 boxes via WUA (decision dialog
    /// "Install minor updates only"). <see cref="InstallRowAsync"/> excludes the OS CU per row, so the broken
    /// Express-delta CU is never pushed through WUA; a box with no scan — or whose only pending update IS the CU —
    /// is skipped with a per-row note.</summary>
    public Task InstallMinorOnlyAsync(IReadOnlyList<Computer> boxes) =>
        RunPatchSweepAsync(boxes, (c, ct) => InstallRowAsync(c, ct, null, minorOnly: true), "Install",
            perHostTimeout: System.Threading.Timeout.InfiniteTimeSpan);

    /// <summary>Pre-dialog "already current this cycle" check for the staged-update decision. For each flagged
    /// 2016 box not yet staged/verified, read its UBR over the SAME path the pre-stage check uses
    /// (<see cref="IPatchService.VerifyLcuAsync"/> → DcomLcuBuildReader) and, when it's already at this month's
    /// target UBR, mark it verified-this-session (and log the skip to the activity log) so the planner drops it from
    /// the dialog and it installs its minor updates via WUA like a non-flagged box; the follow-on install stamps the
    /// prominent "Windows update message" column with the real outcome. FAIL-OPEN: a null/unreadable UBR
    /// (Unreachable), a WrongBuild, or any error leaves the box in the dialog set — never skip a box we couldn't
    /// confirm. Reads run concurrently bounded by the shared remote-read throttle; the reader self-times-out (8s)
    /// so a dead box can't hang the prompt.</summary>
    public async Task ResolveAlreadyCurrentAsync(IReadOnlyList<Computer> flaggedBoxes, CancellationToken token = default)
    {
        if (flaggedBoxes.Count == 0)
        {
            return;
        }

        int targetUbr = _appSettings.Load().MonthlyCu?.TargetUbr ?? 0;
        var outcomes = new ConcurrentDictionary<string, LcuVerifyOutcome>(StringComparer.OrdinalIgnoreCase);

        async Task ReadOneAsync(Computer box)
        {
            await _remoteSweepThrottle.Active.WaitAsync(token);
            try
            {
                LcuVerifyResult result = await _patch.VerifyLcuAsync(box.Name, targetUbr, token);
                outcomes[box.Name] = result.Outcome;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Couldn't read the UBR — fail open: no outcome recorded ⇒ the box stays in the dialog set.
                _activity.Warn(box.Name, $"Pre-dialog currency check couldn't read the build — box stays in the staged-update dialog. {ex.Message}");
            }
            finally
            {
                _remoteSweepThrottle.Active.Release();
            }
        }

        // A box already verified this session needs no read — PartitionByCurrency excludes it on that flag alone.
        await Task.WhenAll(flaggedBoxes.Where(b => !b.LcuVerifiedThisSession).Select(ReadOneAsync));

        (IReadOnlyList<Computer> alreadyCurrent, _) = StagedInstallPlanner.PartitionByCurrency(
            flaggedBoxes, c => outcomes.TryGetValue(c.Name, out LcuVerifyOutcome o) ? o : null);

        foreach (Computer box in alreadyCurrent)
        {
            box.LcuVerifiedThisSession = true; // confirmed at target UBR this cycle → drops from the dialog, WUA minor
            // Don't stamp the prominent "Windows update message" column here — the normal install that follows
            // sets the real outcome (Installed N / Up to date). The routing detail stays in the activity log only.
            _activity.Info(box.Name, "CU already current — skipping the staged-update prompt; minor updates install via Windows Update.");
        }
    }

    /// <summary>Marks or unmarks a Server 2016 box for staged patching: flips the live per-row flag (routing and
    /// the grid's Staged column react immediately) AND persists the host in <see cref="AppSettings.StagedHosts"/>
    /// so the choice survives restarts and seeds future row loads. No-op for a non-2016 box — the flag is only
    /// meaningful there.</summary>
    public void SetStagedPatching(Computer computer, bool staged)
    {
        if (computer is null || !LcuRouting.Is2016(computer.OsBuild))
        {
            return;
        }

        computer.RequiresStagedPatching = staged;

        AppSettings settings = _appSettings.Load();
        if (staged)
        {
            settings.StagedHosts.Add(computer.Name);
        }
        else
        {
            settings.StagedHosts.Remove(computer.Name);
        }

        _appSettings.Save(settings);
    }

    private static LcuTarget BuildLcuTarget(AppSettings s) =>
        new(s.MonthlyCu.Kb, s.MonthlyCu.Arch, TargetUbr: s.MonthlyCu.TargetUbr);

    /// <summary>Read-only precheck for the panel's Stage button: is this month's CU <c>.msu</c> present +
    /// correct in the package folder? The View calls this BEFORE staging — when not Ready it shows the guided
    /// "drop the file here" prompt instead of touching any box. (No host is contacted.)</summary>
    public LcuStageReadiness CheckLcuStageReadiness()
    {
        AppSettings s = _appSettings.Load();
        string kb = s.MonthlyCu?.Kb?.Trim() ?? string.Empty;

        // No KB configured yet — guide to Settings instead of resolving (the resolver requires a KB). This is
        // the first thing a fresh hand-off will hit if they haven't set the month's CU.
        if (kb.Length == 0)
        {
            return new LcuStageReadiness(
                Ready: false,
                Kb: "(not set)",
                Arch: s.MonthlyCu?.Arch ?? "x64",
                Folder: s.LcuPackagesFolder,
                CatalogUrl: "https://www.catalog.update.microsoft.com",
                Problem: "This month's CU isn't set yet. Open Settings ▸ \"Server 2016 cumulative update\" and enter the KB (e.g. KB5094122) and target UBR first.");
        }

        LcuTarget target = BuildLcuTarget(s);
        LcuPackageResolution r = _patch.CheckLcuPackage(s.LcuPackagesFolder, target);
        return new LcuStageReadiness(
            Ready: r.Status == LcuPackageStatus.Found,
            Kb: kb,
            Arch: s.MonthlyCu?.Arch ?? "x64",
            Folder: s.LcuPackagesFolder,
            CatalogUrl: r.CatalogUrl,
            Problem: r.Message);
    }

    /// <summary>Panel button: free component-store space on the selected 2016 boxes — or every 2016 box in the tab
    /// when none are selected (<see cref="Clean2016Targets"/>) — via DISM cleanup as SYSTEM. Selection-driven and
    /// independent of staged-state: it speeds up normal Windows Update on any 2016 box and makes room for a CU.
    /// Safe to run any time — the agent refuses if a reboot is pending.</summary>
    /// <remarks>Cleanup runs with an INFINITE per-host timeout so the 3-hour <see cref="PatchOptions.PerHostTimeout"/>
    /// never cancels-the-watch + tears down + deletes the progress file mid-cleanup. A backlogged 2016 component
    /// store can take many hours to reclaim; the watch stays alive the whole time. A genuinely dead agent is still
    /// caught — the agent emits a terminal Error on a real DISM failure, and the lane's silence-watchdog
    /// (<see cref="PatchOptions.NoResponseTimeout"/>) still trips on total silence (the agent's 10s heartbeat +
    /// ~20s "Cleaning" lines keep a WORKING-or-STALLED cleanup from tripping it). Past the 8-hour ceiling the row
    /// gets a display-only "still going, check the box" flag — never a teardown.</remarks>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task CleanUp2016Async() =>
        RunPatchSweepAsync(Clean2016Targets(), ComponentCleanupLcuRowAsync, "Clean up", CurrentInstallThrottle(),
            // Infinite per-host timeout: RunOnePatchHostAsync's CancelAfter(hostTimeout) never fires for cleanup.
            System.Threading.Timeout.InfiniteTimeSpan);

    // Panel button "Stage" routes through the View's RunStageWorkflowAsync (scan-gate + package-readiness loop)
    // and StageLcuForAsync — the same shared workflow the decision dialog's "Stage CU first" uses.

    /// <summary>Fleet-wide reboot + verify: reboots ALL selected machines (graceful first, forced after the
    /// go-offline window), then tracks each until it is confirmed back online. 2016 boxes verify by build/UBR;
    /// others verify by re-scan. Long-running — the View must confirm first (production reboot). Acts ONLY on
    /// the explicit selection; never reboots the whole fleet by default.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task RebootAndVerifyAsync()
    {
        var selected = SelectedComputers.ToList();
        if (selected.Count == 0)
        {
            _activity.Warn(null, "Reboot & verify: select the machine(s) to reboot first.");
            return Task.CompletedTask;
        }

        // One shared gate per wave invocation: limits how many reboots are ISSUED simultaneously
        // (burst protection for DCs/DNS/auth), but never holds a slot through the offline watch —
        // so all boxes watch + verify in parallel, independent of each other.
        var gate = new RebootTriggerGate(_rebootTriggerThrottle, jitterMs: 500);

        return RunPatchSweepAsync(selected, (c, ct) => RebootWaveRowAsync(c, gate, ct), "Reboot & verify", _waveThrottle,
            // The wave self-bounds at its own hard cap; give the per-host watchdog a margin beyond it so it
            // never cuts a legitimately-still-committing box short. Standalone Verify is the net past this.
            RebootWaveOptions.Default.HardCap + TimeSpan.FromMinutes(15));
    }

    /// <summary>Panel button: read each targeted 2016 box's build/UBR and confirm the CU committed. Read-only;
    /// a box that can't be read yet is "not back up yet" (re-run later), never a failure.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task Verify2016Async() =>
        RunPatchSweepAsync(Server2016Targets(), VerifyLcuRowAsync, "Verify", _remoteSweepThrottle.Active);

    /// <summary>Per-host stage for the 2016 lane: resolve + deliver + DISM-add this month's CU, then map the
    /// terminal status to a glanceable row state — amber "staged — run Reboot Wave" (the action-needed state,
    /// distinct from green-done and red-failed), green "already current", or red on failure.</summary>
    private async Task StageLcuRowAsync(Computer computer, DateTime? scheduleAt, CancellationToken token)
    {
        if (computer.IsPatching)
        {
            return;
        }

        // Defense-in-depth: the 2016 DISM lane is only for FLAGGED boxes. A non-flagged 2016 box patches via
        // Windows Update — never stage it. (The panel's targets are already flagged-only; this guards any direct
        // call, e.g. a future code path, from staging a box the operator chose to keep on the WUA lane.)
        if (LcuRouting.Is2016(computer.OsBuild) && !computer.RequiresStagedPatching)
        {
            computer.UpdatePhase = PatchPhase.Idle.ToString();
            computer.UpdateMessage = "Not a staged box — patches via Windows Update. Mark it for staged patching first.";
            return;
        }

        // Already staged this session (staged + reboot-pending) — skip; the operator runs the Reboot Wave.
        if (StagePreconditions.IsAlreadyStaged(computer.RebootRequired == true, computer.StagedThisSession))
        {
            computer.UpdateMessage = "Already staged — run Reboot Wave";
            return;
        }

        // The 2016 lane is stage-and-stop — it has no scheduled-install mode (the operator commits via the
        // Reboot Wave). Say so rather than silently staging now on a scheduled request.
        if (scheduleAt is not null)
        {
            computer.UpdatePhase = PatchPhase.Idle.ToString();
            computer.UpdateMessage = "Server 2016 uses Stage + Reboot Wave — scheduled install isn't available here.";
            return;
        }

        AppSettings settings = _appSettings.Load();
        if (string.IsNullOrWhiteSpace(settings.MonthlyCu?.Kb))
        {
            computer.UpdatePhase = PatchPhase.Idle.ToString();
            computer.UpdateMessage = "Set this month's CU (KB + UBR) in Settings before staging 2016 boxes.";
            return;
        }

        // Already current — read the box's UBR (same call Verify makes) and skip Stage if it already
        // matches the target. FAIL-OPEN: a null/unreadable read or any error proceeds to Stage.
        int targetUbr = settings.MonthlyCu?.TargetUbr ?? 0;
        try
        {
            LcuVerifyResult current = await _patch.VerifyLcuAsync(computer.Name, targetUbr, token);
            if (StagePreconditions.IsAlreadyCurrent(current.Outcome))
            {
                computer.UpdateMessage = "Already current — skipped";
                return;
            }
        }
        catch (OperationCanceledException)
        {
            throw; // user Stop — propagate
        }
        catch
        {
            // Couldn't read the UBR — fail open, proceed to Stage (DISM will catch an already-current box).
        }

        LcuTarget target = BuildLcuTarget(settings);

        computer.IsPatching = true;
        computer.UpdateError = null;
        computer.UpdateProgress = 0;
        computer.UpdatePhase = PatchPhase.Staging.ToString();
        computer.UpdateMessage = "Checking the update package…";

        // Same mapping on every progress tick AND on the terminal return, so a late progress post can't
        // clobber the final row state (they converge to the same values regardless of order).
        var progress = new Progress<HostPatchStatus>(s => ApplyLcuStageStatus(computer, s, target.Kb));
        try
        {
            // No ConfigureAwait(false): the result-application below mutates data-bound Computer state
            // (live-filtered properties), so the continuation must resume on the captured (UI) context.
            HostPatchStatus final = await _patch
                .StageLcuAsync(computer.Name, settings.LcuPackagesFolder, target, _patchOptions, progress, token);
            ApplyLcuStageStatus(computer, final, target.Kb);

            switch (final.Phase)
            {
                case PatchPhase.PendingReboot:
                    _activity.Info(computer.Name, $"Staged {target.Kb} — reboot-ready (run the Reboot Wave to commit).");
                    break;
                case PatchPhase.Deferred:
                    // A servicing-busy refusal — NOT a stage. Say "couldn't stage, reboot first", never "Staged …".
                    _activity.Warn(computer.Name, $"Couldn't stage {target.Kb} — a reboot is already pending. Reboot to clear the pending state first, then re-stage.");
                    break;
                case PatchPhase.Done:
                    _activity.Info(computer.Name, $"{computer.Name} is already current for {target.Kb}.");
                    break;
                case PatchPhase.Error:
                    _activity.Error(computer.Name, $"Stage {target.Kb} failed — {final.Message}");
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Clear any transient phase (Scanning/Installing/…) so a user Stop never leaves the row stuck
            // spinning — it resolves to Idle, or to amber RebootPending if a reboot is still flagged.
            computer.UpdatePhase = PatchPhase.Idle.ToString();
            computer.UpdateMessage = "Cancelled";
            throw;
        }
        catch (Exception ex)
        {
            computer.UpdateError = ex.Message;
            computer.UpdateMessage = "Stage failed";
            computer.UpdatePhase = PatchPhase.Error.ToString();
            _activity.Error(computer.Name, $"Stage failed — {ex.Message}");
        }
        finally
        {
            computer.IsPatching = false;
        }
    }

    /// <summary>Maps a stage <see cref="HostPatchStatus"/> onto a row via the pure
    /// <see cref="Lcu2016RowState.MapStageTerminal"/> decision (tested in Vivre.Core.Tests). A real stage
    /// (PendingReboot) forces the amber reboot-pending state AND sets <see cref="Computer.StagedThisSession"/>;
    /// a Deferred servicing-busy refusal is reboot-pending too (amber) but is NEVER staged — the operator must
    /// reboot first. <see cref="Computer.RebootRequired"/> is only ever forced true here (never cleared, so a
    /// true set elsewhere survives), matching the prior behaviour. Non-terminal progress ticks fall through to
    /// the live-progress default.</summary>
    private static void ApplyLcuStageStatus(Computer computer, HostPatchStatus status, string kb)
    {
        switch (status.Phase)
        {
            case PatchPhase.PendingReboot:
            case PatchPhase.Deferred:
            case PatchPhase.Done:
            case PatchPhase.Error:
                Lcu2016RowState.StageRowOutcome outcome = Lcu2016RowState.MapStageTerminal(status.Phase, kb, status.Message);
                // Only force RebootRequired true — never clear a true set elsewhere (matches prior behaviour).
                if (outcome.RebootRequired)
                {
                    computer.RebootRequired = true;
                }
                computer.StagedThisSession = outcome.Staged; // Verify uses this (not RebootRequired) to tell rollback from never-staged
                if (outcome.Staged)
                {
                    // A real (re)stage supersedes any prior verified state; a Deferred refusal must NOT touch it.
                    computer.LcuVerifiedThisSession = false;
                }
                computer.UpdatePhase = outcome.Phase;
                // A clean terminal (staged/current) sits at 100%; an error/deferral leaves the bar as-is.
                if (status.Phase is PatchPhase.PendingReboot or PatchPhase.Done)
                {
                    computer.UpdateProgress = 100;
                }
                // Surface the agent's message as the row error only on a real failure (not a deferral, which is
                // a clean "reboot first" outcome, and not a successful stage/current).
                computer.UpdateError = status.Phase == PatchPhase.Error ? status.Message : null;
                computer.UpdateMessage = outcome.Message;
                break;
            default:
                computer.UpdatePhase = status.Phase.ToString();
                computer.UpdateProgress = status.Percent;
                computer.UpdateMessage = status.Message;
                break;
        }
    }

    /// <summary>Per-host component-store cleanup (DISM /StartComponentCleanup as SYSTEM) for the 2016 lane.
    /// Keeps a LIVE host-side "Cleaning — {elapsed}" readout so the row never looks frozen even when DISM's
    /// percent stalls (the elapsed is independent of the agent's %); the agent's last-known % + "looks
    /// stalled" hint decorate it, and past the 8-hour ceiling a "still going, check the box" flag is appended.
    /// On a terminal status the live readout stops and the per-box terminal label
    /// (<see cref="Lcu2016RowState.MapCleanupTerminal"/>) wins: "Cleaned — ready to Stage" (green) /
    /// "Cleaned — reboot-pending (reboot before Stage)" (amber) / "Deferred" (servicing-busy refusal).</summary>
    private async Task ComponentCleanupLcuRowAsync(Computer computer, CancellationToken token)
    {
        if (computer.IsPatching)
        {
            return;
        }

        computer.IsPatching = true;
        computer.UpdateError = null;
        computer.UpdateProgress = 0;
        computer.UpdatePhase = PatchPhase.Cleaning.ToString();
        computer.UpdateMessage = "Cleaning the component store…";

        // Host-side liveness state. Both the progress callback and the elapsed-updater tick run on the
        // captured UI context (no ConfigureAwait(false) anywhere in this method's path), so the single-
        // threaded UI context serializes every read/write of these — no locking needed. The progress
        // callback only RECORDS the agent's latest % + stalled hint (and forwards to ApplyStatus for the
        // reboot-pending/error bookkeeping); the updater RENDERS the live "Cleaning — {elapsed}" label.
        DateTime startedUtc = DateTime.UtcNow;
        int? latestPercent = null;
        bool latestStalled = false;
        bool terminalSeen = false;

        var progress = new Progress<HostPatchStatus>(s =>
        {
            if (s.Phase == PatchPhase.Cleaning)
            {
                // A non-terminal "Cleaning" display line: capture its % + stalled hint for the updater, but
                // do NOT write the message here (the updater owns the live label so it never looks frozen).
                latestPercent = s.Percent;
                latestStalled = s.Message?.Contains("looks stalled", StringComparison.OrdinalIgnoreCase) == true;
                return;
            }

            // Any non-Cleaning status (terminal, or a transient Scanning) goes through the shared bookkeeping
            // (RebootRequired on 3010, error recording, etc.). The terminal label is applied below the await.
            terminalSeen = s.Phase is PatchPhase.Done or PatchPhase.PendingReboot or PatchPhase.Deferred or PatchPhase.Error;
            ApplyStatus(computer, s);
        });

        // The elapsed-updater loop: refreshes the live "Cleaning — {elapsed}" label every ~2.5s until the
        // cleanup completes. Cancelled via updaterCts when the cleanup terminates (success, error, or Stop).
        using var updaterCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task updater = RunCleanupElapsedUpdaterAsync(computer, startedUtc, () => latestPercent, () => latestStalled, () => terminalSeen, updaterCts.Token);

        try
        {
            // No ConfigureAwait(false): the result-application below mutates data-bound Computer state
            // (live-filtered properties), so the continuation must resume on the captured (UI) context.
            HostPatchStatus final = await _patch
                .ComponentCleanupLcuAsync(computer.Name, _patchOptions, progress, token);

            // Stop the live readout before applying the terminal label so it can't overwrite it on a late tick.
            updaterCts.Cancel();
            try { await updater; } catch (OperationCanceledException) { /* expected on cancel */ }

            // Shared bookkeeping (RebootRequired on the 3010 PendingReboot case, error recording) …
            ApplyStatus(computer, final);

            // The access-denied (locked-files) cleanup is a SUCCESS-WITH-CAVEAT, not a failure: the agent
            // emits raw facts, the classifier decides. Render it as the neutral "Cleaned" state (which
            // DerivePatchState maps to green Done), never red — the AV/EDR caveat lives in the activity log.
            ComponentCleanupClassification? classified = final.CleanupFacts is { } facts
                ? ComponentCleanupClassifier.Classify(facts.ExitCode, facts.AnalyzeOk, facts.ReclaimablePackages)
                : null;

            if (classified is { Outcome: ComponentCleanupOutcome.CleanedFilesLocked } locked)
            {
                computer.UpdatePhase = "Cleaned";
                computer.UpdateMessage = locked.ShortStatus!;
                _activity.Info(computer.Name, locked.Detail!);
            }
            else
            {
                // … the per-box terminal label wins over ApplyStatus's generic message.
                (string phase, string message) = Lcu2016RowState.MapCleanupTerminal(final.Phase, final.Message);
                computer.UpdatePhase = phase;
                computer.UpdateMessage = message;
            }
        }
        catch (OperationCanceledException)
        {
            // Clear any transient phase (Scanning/Cleaning/…) so a user Stop never leaves the row stuck
            // spinning — it resolves to Idle, or to amber RebootPending if a reboot is still flagged.
            computer.UpdatePhase = PatchPhase.Idle.ToString();
            computer.UpdateMessage = "Cancelled";
            throw;
        }
        catch (Exception ex)
        {
            computer.UpdateError = ex.Message;
            computer.UpdateMessage = "Cleanup failed";
            computer.UpdatePhase = PatchPhase.Error.ToString();
            _activity.Error(computer.Name, $"Component cleanup failed — {ex.Message}");
        }
        finally
        {
            // Belt-and-braces: ensure the updater is stopped on every exit path (cancel/error too).
            updaterCts.Cancel();
            try { await updater; } catch { /* updater already faulted/cancelled — nothing to surface */ }
            computer.IsPatching = false;
        }
    }

    /// <summary>The live host-side elapsed readout for a running cleanup. Refreshes the row's
    /// <see cref="Computer.UpdateMessage"/> (and % bar) every ~2.5s with <see cref="Lcu2016RowState.BuildCleanupProgressLabel"/>
    /// so the row never looks frozen even while DISM's percent sits still — the elapsed is the real liveness.
    /// Runs on the captured UI context (no ConfigureAwait(false)) so it never races the data-bound writes the
    /// rest of the method makes. Stops the moment a terminal status is seen or the token is cancelled; the
    /// caller then applies the terminal label.</summary>
    private static async Task RunCleanupElapsedUpdaterAsync(
        Computer computer, DateTime startedUtc, Func<int?> percent, Func<bool> stalled, Func<bool> terminalSeen, CancellationToken token)
    {
        while (!token.IsCancellationRequested && !terminalSeen())
        {
            TimeSpan elapsed = DateTime.UtcNow - startedUtc;
            bool pastCeiling = Lcu2016RowState.IsPastCleanupCeiling(elapsed, Lcu2016RowState.CleanupCeiling);
            computer.UpdateMessage = Lcu2016RowState.BuildCleanupProgressLabel(elapsed, percent(), stalled(), pastCeiling);
            if (percent() is int p)
            {
                computer.UpdateProgress = p;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2.5), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Per-host Reboot Wave: routes to the 2016 or WUA lane based on the host's OS build, then runs
    /// a shared post-reboot rescan to confirm the outcome and set the row's final message.
    /// <list type="bullet">
    ///   <item><description><see cref="RebootVerifyLane.Lcu2016"/>: UBR-confirmed full-package CU lane —
    ///   the wave itself declares success; the rescan supplements the UBR Done message.</description></item>
    ///   <item><description><see cref="RebootVerifyLane.Wua"/>: WUA lane — "Done" means the box is back
    ///   and OS-queryable; the actual verify comes from the post-reboot Applicable rescan.</description></item>
    /// </list>
    /// </summary>
    private async Task RebootWaveRowAsync(Computer computer, IRebootGate gate, CancellationToken token)
    {
        if (computer.IsPatching)
        {
            return;
        }

        // Override-aware: a non-flagged 2016 box patched via WUA verifies via the WUA lane, not UBR.
        RebootVerifyLane lane = LcuRouting.RebootVerifyLaneFor(computer.OsBuild, computer.RequiresStagedPatching);

        computer.IsPatching = true;
        computer.UpdateError = null;
        computer.UpdatePhase = PatchPhase.Rebooting.ToString();
        computer.UpdateMessage = "Starting reboot wave…";

        var progress = new Progress<HostPatchStatus>(s => ApplyStatus(computer, s));
        try
        {
            // No ConfigureAwait(false): the result-application below mutates data-bound Computer state
            // (live-filtered properties), so the continuation must resume on the captured (UI) context.
            HostPatchStatus final;
            if (lane == RebootVerifyLane.Lcu2016)
            {
                int targetUbr = _appSettings.Load().MonthlyCu?.TargetUbr ?? 0;
                // 2016 staged box: it commits the CU slowly on shutdown and can hold the network up for
                // 15–20+ min, so use the longer go-offline windows (ForSlowCommit) — the 8-min default
                // false-failed these as "the reboot isn't taking" while they were genuinely committing.
                final = await _patch
                    .RebootWaveLcuAsync(computer.Name, targetUbr, RebootWaveOptions.ForSlowCommit, progress, token, gate);
            }
            else
            {
                final = await _patch
                    .RebootWaveWuaAsync(computer.Name, RebootWaveOptions.Default, progress, token, gate);
            }

            ApplyStatus(computer, final);
            if (final.Phase == PatchPhase.Done)
            {
                computer.RebootRequired = false; // committed — clear the staged/reboot-pending flag (→ green)
                computer.StagedThisSession = false; // committed — no longer a pending stage
                if (lane == RebootVerifyLane.Lcu2016)
                {
                    computer.LcuVerifiedThisSession = true; // 2016 CU committed → remaining minor updates go via WUA
                }

                _activity.Info(computer.Name, final.Message);

                // Post-reboot rescan runs AFTER the wave returns Done — ORDERING GUARANTEE.
                // For 2016 the UBR check always precedes this; for WUA this is the primary verify.
                await ReportPostRebootOutcomeAsync(computer, is2016: lane == RebootVerifyLane.Lcu2016, token);
            }
            else if (final.Phase == PatchPhase.Error)
            {
                _activity.Error(computer.Name, $"Reboot wave — {final.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            // Clear any transient phase (Scanning/Installing/…) so a user Stop never leaves the row stuck
            // spinning — it resolves to Idle, or to amber RebootPending if a reboot is still flagged.
            computer.UpdatePhase = PatchPhase.Idle.ToString();
            computer.UpdateMessage = "Cancelled";
            throw;
        }
        catch (Exception ex)
        {
            computer.UpdateError = ex.Message;
            computer.UpdateMessage = "Reboot wave failed";
            computer.UpdatePhase = PatchPhase.Error.ToString();
            _activity.Error(computer.Name, $"Reboot wave failed — {ex.Message}");
        }
        finally
        {
            computer.IsPatching = false;
        }
    }

    /// <summary>
    /// Shared post-reboot outcome step: runs an Applicable rescan, probes for a lingering reboot,
    /// then writes a final outcome string to <see cref="Computer.UpdateMessage"/>.
    /// <para>
    /// For the WUA lane this is the primary verify (the wave only confirms the box is responsive).
    /// For the 2016 lane the UBR Done message is kept as the primary; this appends a supplementary note.
    /// A rescan or probe failure is surfaced honestly — it never produces a false "up to date" result.
    /// This method is pure outcome-reporting: it never triggers a reboot, install, or uninstall.
    /// </para>
    /// </summary>
    private async Task ReportPostRebootOutcomeAsync(Computer computer, bool is2016, CancellationToken token)
    {
        string name = computer.Name;

        // The reboot wave's success message (e.g. "Back online — rebooted. (committed in ~0 min)") is the
        // row's message right now. Capture it BEFORE the rescan's ApplyStatus below overwrites it, so the
        // 2016 outcome keeps the commit note as its base — and so we never read it back as "Up to date"
        // (reading the overwritten value is what produced the doubled "Up to date · up to date").
        string committedMessage = computer.UpdateMessage ?? string.Empty;

        // Make the AUTOMATIC post-reboot recheck VISIBLE while the rescan runs — operators were manually
        // rescanning because the row looked finished. The rescan below is awaited, so this is what shows
        // during it; the final outcome (further below) then replaces it. Keep the commit/elapsed note alongside.
        computer.UpdateMessage = committedMessage.Length > 0
            ? $"{committedMessage} · rechecking for updates…"
            : "Back online — rechecking for updates…";

        // ── a) Applicable rescan (read-only: ScanAsync only, never Install/Uninstall/Reboot) ──────
        // The box JUST rebooted (boot-time-confirmed) but may still be settling; a transient unreachable
        // ("network name no longer available") mid-settle is NOT a terminal failure — wait briefly and
        // retry. A genuinely failed rescan is surfaced as an honest "couldn't rescan" (below), and is NEVER
        // stamped onto the row's phase — that is what previously left a recovering box stuck on a red Error.
        bool scanFailed = false;
        for (int attempt = 1; ; attempt++)
        {
            HostPatchStatus status;
            try
            {
                PatchOptions applicableOptions = _patchOptions.Clone();
                applicableOptions.Scope = UpdateScope.Applicable;
                status = await _patch.ScanAsync(name, applicableOptions, _credentials.Current, token);
            }
            catch (OperationCanceledException)
            {
                throw; // Propagate cancellation — don't swallow it.
            }
            catch
            {
                status = HostPatchStatus.Failed("rescan threw");
            }

            if (status.Phase != PatchPhase.Error)
            {
                ApplyStatus(computer, status, UpdateScope.Applicable);
                // Read-only readiness: the post-reboot rescan surfaces what's STILL applicable. Auto-select
                // those updates for THIS box so the operator can one-click Install them — the same readiness
                // a fresh scan gives. (ApplyStatus → ReplaceUpdatesForScope preserves prior selection, which
                // here inherits the just-completed install's untick on a re-found still-applicable update,
                // leaving it surfaced-but-unchecked so the checked-updates-only Install finds nothing.) This
                // ONLY selects — it never installs and never reboots; the operator still clicks Install.
                computer.SelectAllApplicableUpdates();
                break;
            }

            // The rescan couldn't reach the box (still settling after the reboot). Do NOT ApplyStatus the
            // Error (that stamped a stuck red on a recovering box). If attempts remain, wait and retry.
            if (attempt >= PostRebootRescanAttempts)
            {
                scanFailed = true;
                break;
            }

            await Task.Delay(PostRebootRescanRetryDelay, token);
        }

        // ── b) remaining after rescan ─────────────────────────────────────────────────────────────
        int remaining = computer.ApplicableCount ?? 0;

        // ── c) reboot-still-pending probe (best-effort; never PendingFileRenameOperations) ───────
        // Bounded like the monitor's probe (HIGH-2): a wedged CCM provider must cost ~120s and an
        // honest "couldn't confirm", not the wave's 4h45m per-host cap. Declared before the try so
        // the catch filter below can see it; the linked token goes INTO the probe so the deadline
        // reaches the WinRM invoke. background stays default (false): operator priority on the gate.
        using var perCall = CancellationTokenSource.CreateLinkedTokenSource(token);
        perCall.CancelAfter(TimeSpan.FromSeconds(RebootProbeTimeoutSeconds));
        bool? rebootStillPending = null; // null = probe couldn't answer — never rendered clean
        try
        {
            rebootStillPending = await _rebootProbe.IsRebootPendingAsync(name, CurrentPsCredential(), perCall.Token);
        }
        catch (OperationCanceledException) when (perCall.IsCancellationRequested && !token.IsCancellationRequested)
        {
            // Probe timeout, not a Stop — unknown. MUST NOT rethrow: an OCE from here would read as
            // a wave cancel at RebootWaveRowAsync's catch and mark the whole sweep cancelled. This
            // is a one-shot operator action, not the monitor loop: no degraded backoff either.
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Kerberos, WinRM, or any other probe failure — unknown, never clean/pending.
        }

        // ── d) install counts from the last meaningful install, consumed once ─────────────────────
        int? installed = computer.LastInstallInstalledCount;
        int? failed    = computer.LastInstallFailedCount;
        // Consume: this wave reports them once; a second wave must not re-claim them. (Every
        // cancellation path above throws BEFORE this point, so a cancelled wave keeps the counts
        // for a retry wave.)
        computer.LastInstallInstalledCount = null;
        computer.LastInstallFailedCount = null;

        // ── e/f) write outcome ────────────────────────────────────────────────────────────────────
        if (!is2016)
        {
            // WUA lane: outcome string IS the primary UpdateMessage.
            string outcome = RebootOutcomeSelector.Select(installed, failed, remaining, rebootStillPending, scanFailed);
            computer.UpdateMessage = outcome;
            computer.RebootRequired = rebootStillPending;
            _activity.Info(name, outcome);
        }
        else
        {
            // 2016 lane. The rescan's ApplyStatus above already wrote the scan conclusion onto the row, so
            // appending a supplement that restates it printed the same thing twice ("Up to date · up to
            // date"). Compose a single clear message instead — DISPLAY only; the rescan still runs once.
            if (scanFailed)
            {
                // The rescan errored, so ApplyStatus did NOT overwrite the row — keep the wave's commit
                // message (captured above) and flag that the automatic recheck didn't complete.
                computer.UpdateMessage = committedMessage.Length > 0
                    ? $"{committedMessage} · couldn't rescan — re-check"
                    : "Couldn't rescan after reboot — re-check";
                _activity.Info(name, $"{name}: couldn't rescan — re-check");
            }
            else if (remaining > 0)
            {
                computer.UpdateMessage = $"{remaining} update(s) still applicable — run a WUA pass";
                _activity.Info(name, $"{name}: {remaining} update(s) still applicable — run a WUA pass");
            }
            else
            {
                computer.UpdateMessage = "Up to date";
                _activity.Info(name, $"{name}: up to date");
            }
        }
    }

    /// <summary>Per-host Verify: read the box's build/UBR and decide whether the staged CU committed. Read-only
    /// (no IsPatching claim) so it can run any time. Verified clears reboot-pending (→ green); wrong build is
    /// red; "can't read it yet" just leaves a re-check message (never a failure).</summary>
    private async Task VerifyLcuRowAsync(Computer computer, CancellationToken token)
    {
        int targetUbr = _appSettings.Load().MonthlyCu?.TargetUbr ?? 0;

        computer.UpdateError = null;
        computer.UpdateMessage = "Verifying build…";
        try
        {
            // No ConfigureAwait(false): the result writes below mutate data-bound Computer state
            // (live-filtered properties), so the continuation must resume on the captured (UI) context.
            LcuVerifyResult result = await _patch.VerifyLcuAsync(computer.Name, targetUbr, token);
            switch (result.Outcome)
            {
                case LcuVerifyOutcome.Verified:
                    computer.RebootRequired = false;
                    computer.StagedThisSession = false; // committed — no longer a pending stage
                    computer.LcuVerifiedThisSession = true; // CU confirmed at target UBR → minor updates now go via WUA
                    computer.UpdatePhase = PatchPhase.Done.ToString();
                    computer.UpdateMessage = result.CurrentBuild is { } vBuild && result.Ubr is { } vUbr
                        ? $"Verified · now at {vBuild}.{vUbr}"
                        : result.Message;
                    break;
                case LcuVerifyOutcome.WrongBuild:
                {
                    // Distinguish "never staged, just unpatched" from "staged + rebooted but rolled back".
                    // Use StagedThisSession, NOT RebootRequired: the latter is also set by the health refresh,
                    // the reboot-pending probe, and any reboot-required scan, so it's true for unrelated
                    // pending reboots and would mislabel a never-staged box as "rolled back".
                    bool wasStaged = computer.StagedThisSession;
                    string at = result.CurrentBuild is { } b && result.Ubr is { } u
                        ? $"{b}.{u}"
                        : "an unexpected build";
                    string message = wasStaged
                        ? $"Rolled back — at {at}, expected .{targetUbr}"
                        : $"Not patched — at {at}, expected .{targetUbr} · run Stage first";
                    computer.UpdatePhase = PatchPhase.Error.ToString();
                    computer.UpdateError = message;
                    computer.UpdateMessage = message;
                    if (wasStaged)
                    {
                        _activity.Error(computer.Name, message);
                    }
                    else
                    {
                        _activity.Warn(computer.Name, message); // unpatched isn't a failure — it's "stage it"
                    }

                    break;
                }
                case LcuVerifyOutcome.Unreachable:
                    // Pingable-but-still-coming-up, or unreachable — a retry, not a failure. Leave the row's
                    // existing state (it may still be staged/amber) and just surface the re-check hint.
                    computer.UpdateMessage = result.Message;
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Clear any transient phase (Scanning/Installing/…) so a user Stop never leaves the row stuck
            // spinning — it resolves to Idle, or to amber RebootPending if a reboot is still flagged.
            computer.UpdatePhase = PatchPhase.Idle.ToString();
            computer.UpdateMessage = "Cancelled";
            throw;
        }
        catch (Exception ex)
        {
            computer.UpdateError = ex.Message;
            computer.UpdateMessage = "Verify failed";
            _activity.Error(computer.Name, $"Verify failed — {ex.Message}");
        }
    }

    private static string FormatScheduledMessage(string? action, DateTime when)
    {
        string verb = action switch
        {
            "Reboot" => "Reboot",
            "Install updates" => "Install",
            _ => "Task",
        };
        // {when} is the operator's host-local pick; the "(your time)" tag makes that explicit so a
        // remote box in another zone can't be misread (the task fires at the same absolute instant).
        return $"{verb} scheduled for {when:g} (your time)";
    }

    /// <summary>Writes a <see cref="HostPatchStatus"/> snapshot onto a row, logging phase transitions only.
    /// <paramref name="scopeForScan"/> is set by <see cref="ScanRowAsync"/> so the scan result lands in the
    /// right per-scope cache on <see cref="Computer"/>; null (the Progress&lt;T&gt; callback path) uses the
    /// shared options' current scope, which only matters for the Phase.Available branch.
    /// <paramref name="failuresAreErrors"/> is set ONLY by the install/uninstall COMPLETION callers: when true
    /// and the status carries <see cref="HostPatchStatus.FailedCount"/> &gt; 0, the row is forced to the Error
    /// phase (red pill) so a partial/total failure can never read green "Up to date" or hide behind amber
    /// reboot-pending. Default false so scan / cleanup / reboot-verify (which share this method) are never
    /// painted red by an unrelated count.</summary>
    private void ApplyStatus(Computer computer, HostPatchStatus status, UpdateScope? scopeForScan = null, bool failuresAreErrors = false)
    {
        UpdateScope scope = scopeForScan ?? _patchOptions.Scope;

        // A scan that finds zero APPLICABLE updates means the box is up to date — show the green "Done"
        // state (which the Done filter includes), not "Available" (which implies updates exist and would
        // wrongly exclude a current box). Applicable scope only — an Installed-scope scan with no rows means
        // "nothing installed", a different thing. The Available bookkeeping below still runs, so the
        // (now-empty) update list, count, and scan timestamp are refreshed for the row.
        bool upToDate = status.Phase == PatchPhase.Available
            && status.AvailableCount == 0
            && scope == UpdateScope.Applicable;

        string phase = (upToDate ? PatchPhase.Done : status.Phase).ToString();

        // ERROR > REBOOT-PENDING > UP-TO-DATE: a completed install/uninstall with ANY failure is NOT a
        // success — force the red Error pill so a partial or total failure can never read green "Up to date"
        // or hide behind an amber reboot-pending. DerivePatchState maps PatchPhase.Error → PatchState.Error
        // regardless of RebootRequired (Error beats reboot-pending), and RebootRequired is still set below, so
        // the reboot DOT stays lit alongside the Error pill. Gated to the install/uninstall COMPLETION callers
        // via failuresAreErrors, so a scan / cleanup / reboot-verify that shares ApplyStatus can never be
        // painted red by an unrelated count. The honest "Installed N, M failed" message text is preserved.
        if (failuresAreErrors && status.FailedCount > 0)
        {
            phase = PatchPhase.Error.ToString();
        }

        string message = upToDate ? "Up to date" : status.Message;
        bool phaseChanged = !string.Equals(computer.UpdatePhase, phase, StringComparison.Ordinal);

        computer.UpdatePhase = phase;
        computer.UpdateMessage = message;
        computer.UpdateProgress = status.Percent;

        // A scheduled box shows its schedule as the salient fact (matches the "Scheduled" pill). This wins
        // over the scan/idle message; cleared automatically once the monitor clears ScheduledNextRun.
        if (computer.ScheduledNextRun is { } schedWhen)
        {
            computer.UpdateMessage = FormatScheduledMessage(computer.ScheduledAction, schedWhen);
        }

        if (status.Phase == PatchPhase.Available)
        {
            computer.UpdatesAvailable = status.AvailableCount;
            ReplaceUpdatesForScope(computer, scope, status.Updates);
            // Fill the real download sizes from the Microsoft Update Catalog (async, cached per KB). The grid
            // shows a dash / WUA-definite size until the lookup answers, then upgrades to the catalog size.
            // Fire-and-forget — display-only, never blocks or gates the scan.
            _ = ResolveCatalogSizesAsync(computer, scope);
            // Cache per-scope so toggling between Applicable / Installed preserves the data.
            if (scope == UpdateScope.Installed)
            {
                computer.InstalledMessage = status.Message;
                computer.InstalledCount = status.AvailableCount;
                computer.LastScannedInstalled = DateTime.Now;
            }
            else
            {
                computer.ApplicableMessage = status.Message;
                computer.ApplicableCount = status.AvailableCount;
                computer.LastScannedApplicable = DateTime.Now;
                // A new Applicable scan supersedes any prior session-install state: the fresh scan
                // is the new source of truth, so clear the partial-failure banner.  Per-row
                // InstalledThisSession flags are reset inside ReplaceUpdatesForScope (new items).
                computer.LastInstallNote = null;
            }
        }

        if (status.RebootPending)
        {
            computer.RebootRequired = true;
        }

        // Unreachable (transient retries exhausted) is a failure too — record it as the row error and log
        // it as an error, same as a hard Error, so the Errors filter and error column reflect it.
        if (status.Phase is PatchPhase.Error or PatchPhase.Unreachable)
        {
            computer.UpdateError = status.Message;
        }

        if (phaseChanged && status.Phase != PatchPhase.Idle)
        {
            if (status.Phase is PatchPhase.Error or PatchPhase.Unreachable)
            {
                _activity.Error(computer.Name, status.Message);
            }
            else if (status.Phase == PatchPhase.Deferred)
            {
                // A servicing-busy refusal (reboot already pending) — not a failure, but not a success
                // either. Warn so the operator sees the "reboot first" message stand out, while the chip
                // reads amber RebootPending (never green) via DerivePatchState.
                _activity.Warn(computer.Name, message);
            }
            else
            {
                _activity.Info(computer.Name, $"{phase}: {message}");
            }
        }
    }

    /// <summary>
    /// Repopulates the per-scope checklist on <paramref name="computer"/> from a fresh scan,
    /// preserving the user's prior unticks by KB (fallback title) so a re-scan in the same scope
    /// doesn't silently re-select updates they chose to skip. Routes into either
    /// <see cref="Computer.ApplicableUpdates"/> or <see cref="Computer.InstalledUpdates"/> based
    /// on the scope that was active when the scan ran.
    /// </summary>
    private static void ReplaceUpdatesForScope(Computer computer, UpdateScope scope, IReadOnlyList<SoftwareUpdate> updates)
    {
        ObservableCollection<SelectableUpdate> target = scope == UpdateScope.Installed
            ? computer.InstalledUpdates
            : computer.ApplicableUpdates;

        var prior = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (SelectableUpdate existing in target)
        {
            prior[existing.Kb ?? existing.Title] = existing.IsSelected;
        }

        // Install (Applicable) defaults ticked — you opt out of the few you don't want.
        // Uninstall (Installed) defaults UNticked — removal is opt-in, never accidental.
        bool defaultSelected = scope != UpdateScope.Installed;

        // Installed scope: show most-recently-installed first by default (the user can still
        // click the "Installed" column to re-sort). Updates whose install date couldn't be matched
        // from WUA history (null InstalledAt) sort to the bottom. Applicable scope keeps scan order.
        IEnumerable<SoftwareUpdate> ordered = scope == UpdateScope.Installed
            ? updates.OrderByDescending(u => u.InstalledAt ?? DateTime.MinValue)
            : updates;

        target.Clear();
        foreach (SoftwareUpdate update in ordered)
        {
            bool selected = prior.TryGetValue(update.ArticleId ?? update.Title, out bool wasSelected)
                ? wasSelected
                : defaultSelected;
            // Never pre-tick a non-removable update in Installed scope (can't be removed by any engine).
            if (scope == UpdateScope.Installed && !update.IsUninstallable)
            {
                selected = false;
            }

            target.Add(new SelectableUpdate(update, selected));
        }
    }

    /// <summary>
    /// Fills <see cref="SelectableUpdate.CatalogSizeBytes"/> from the Microsoft Update Catalog, but ONLY for the
    /// rows whose WUA <c>MaxDownloadSize</c> is implausibly large — the inflated express-CU case
    /// (<see cref="UpdateSizeResolver.NeedsCatalogLookup"/>). Every other row already shows its real WUA size with
    /// no network call, so the catalog (and its jump-box TLS dependency) is touched only for the handful of rows
    /// that need it. One lookup per unique KB+architecture (the shared service caches across machines and tabs);
    /// the result is applied to whatever rows currently carry that KB, so a re-scan that rebuilt the list still
    /// gets the cached size. Display-only — touches no install/reboot path. The service swallows catalog failures
    /// to null (→ dash); this wrapper guards the unexpected so a fire-and-forget never crashes the app.
    /// </summary>
    private async Task ResolveCatalogSizesAsync(Computer computer, UpdateScope scope)
    {
        try
        {
            ObservableCollection<SelectableUpdate> target = scope == UpdateScope.Installed
                ? computer.InstalledUpdates
                : computer.ApplicableUpdates;

            // Distinct (KB, arch) pairs to look up — ONLY rows with an absurd MaxDownloadSize (express CUs);
            // arch is derived best-effort from the update title. No absurd rows ⇒ no catalog traffic at all.
            var lookups = target
                .Where(u => !string.IsNullOrWhiteSpace(u.Kb)
                            && UpdateSizeResolver.NeedsCatalogLookup(u.Update.MaxDownloadSizeBytes))
                .Select(u => (Kb: u.Kb!, Arch: UpdateSizeResolver.ArchFromTitle(u.Title)))
                .Distinct()
                .ToList();

            foreach ((string kb, string? arch) in lookups)
            {
                // No ConfigureAwait(false): ApplyStatus (our caller) runs on the UI thread, so the continuation
                // resumes there and setting the observable below is thread-safe — the same invariant ApplyStatus
                // already relies on when it writes bound properties directly.
                long? bytes = await _catalogSize.GetSizeBytesAsync(kb, arch);
                if (bytes is null)
                {
                    continue;
                }

                // Apply to the CURRENT rows for this KB+arch — the list may have been replaced by a newer scan
                // while the lookup was in flight (the collection instance is stable; only its contents change).
                foreach (SelectableUpdate u in target)
                {
                    if (string.Equals(u.Kb, kb, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(UpdateSizeResolver.ArchFromTitle(u.Title), arch, StringComparison.Ordinal))
                    {
                        u.CatalogSizeBytes = bytes;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Catalog sizing is a display nicety; never let a fire-and-forget failure escape.
            _activity.Warn(computer.Name, $"Couldn't resolve catalog update sizes. {ex.Message}");
        }
    }

    private async Task PingRowAsync(Computer computer, CancellationToken token)
    {
        computer.LastError = null;
        computer.LastStatus = "Pinging…";
        try
        {
            (bool online, string? error) = await ProbeReachabilityAsync(computer, token);
            computer.IsOnline = online;
            computer.LastStatus = online ? "Online" : "Offline";
            computer.LastError = online ? null : error;
            if (online)
            {
                // If the monitor was stopped while this box was down, a stale "Offline since …" reboot
                // message can linger; a confirmed-online ping clears it so it doesn't contradict the green dot.
                if (computer.RebootMessage is { } rm && rm.StartsWith("Offline since", StringComparison.Ordinal))
                {
                    computer.RebootMessage = null;
                }

                _activity.Info(computer.Name, "Ping: online");
            }
            else
            {
                _activity.Warn(computer.Name, $"Ping: offline — {error}");
            }
        }
        catch (OperationCanceledException)
        {
            computer.LastStatus = "Cancelled";
            throw;
        }
    }

    /// <summary>
    /// Determines reachability: ICMP first, then — only if ICMP fails AND explicit credentials
    /// are stored — an authenticated WMI/DCOM probe (many servers block ping but answer WMI).
    /// Returns whether the host is online and, when offline, the reason. Only cancellation throws.
    /// </summary>
    private async Task<(bool Online, string? Error)> ProbeReachabilityAsync(Computer computer, CancellationToken token)
    {
        PingResult ping = await _pinger.PingAsync(computer.Name, PingTimeoutMs, token);
        if (ping.IsOnline)
        {
            return (true, null);
        }

        if (_credentials.Current is null)
        {
            return (false, ping.Error);
        }

        try
        {
            ProbeResult probe = await _hostProbe.CanReachAsync(computer.Name, _credentials.Current, token);
            return probe.Reachable ? (true, null) : (false, probe.Error ?? ping.Error);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // --- continuous monitoring (IsMonitoring) ---

    private async Task MonitorLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Don't fight a manual sweep (Ping All / Check All) — pause while one runs.
                if (!IsBusy && Computers.Count > 0)
                {
                    await MonitorRowsAsync([.. Computers], token);
                }

                await Task.Delay(TimeSpan.FromSeconds(MonitorIntervalSeconds), token);
            }
        }
        catch (OperationCanceledException)
        {
            // Monitoring was turned off (Stop / toggle / tab closed) — just exit the loop.
        }
        catch (Exception ex)
        {
            // A non-cancellation fault would otherwise kill the monitor silently while the toggle still reads
            // "on". Surface it and flip the toggle off so the operator knows to restart it. (The loop's awaits
            // don't ConfigureAwait(false), so this runs on the UI context — touching IsMonitoring is safe.)
            _activity.Error(null, $"Monitoring stopped unexpectedly — {ex.Message}");
            IsMonitoring = false;
        }
    }

    private async Task MonitorRowsAsync(IReadOnlyList<Computer> rows, CancellationToken token)
    {
        try
        {
            await Task.WhenAll(rows.Select(row => MonitorRowAsync(row, token)));
        }
        catch (OperationCanceledException)
        {
            // Monitoring stopped mid-pass.
        }
    }

    /// <summary>
    /// One quiet reachability check used by the monitor: updates the online dot every pass but
    /// only rewrites the status / logs on a state <em>change</em>, so it doesn't spam the activity
    /// log or clobber a richer status (e.g. a Check All health summary) while nothing changes.
    /// </summary>
    private async Task MonitorRowAsync(Computer computer, CancellationToken token)
    {
        // Clear a scheduled-install row once its trigger time has passed (client-side — the task has
        // fired by now; a later scan shows the result). No remote call.
        if (_scheduledTasks.TryGetValue(computer.Name, out DateTime scheduledFor) && DateTime.Now >= scheduledFor)
        {
            _scheduledTasks.TryRemove(computer.Name, out _);
            computer.ScheduledAction = null;
            computer.ScheduledNextRun = null;
        }

        bool? previous = computer.IsOnline;
        // Throttle the reachability fan-out so a full list doesn't launch one ping/DCOM probe per row at
        // once every pass (the manual Ping All path is separate). The reboot-pending probe further down
        // holds its own throttle, so releasing here first keeps the two caps independent.
        bool online;
        string? error;
        await _monitorThrottle.WaitAsync(token);
        try
        {
            (online, error) = await ProbeReachabilityAsync(computer, token);
        }
        finally
        {
            _monitorThrottle.Release();
        }

        // Confirm an offline before believing it. A single failed reachability probe under load (a dropped
        // ping / busy WMI) is transient; only OfflineConfirmThreshold consecutive failures flip a
        // previously-online box. This suppresses the false "Went offline → Back online" blips.
        int consecutiveFailures = online
            ? 0
            : _consecutiveProbeFailures.AddOrUpdate(computer.Name, 1, (_, n) => n + 1);
        if (online)
        {
            _consecutiveProbeFailures.TryRemove(computer.Name, out _);
        }
        bool effectiveOnline = ReachabilityConfirmation.ConfirmEffectiveOnline(previous, online, consecutiveFailures, OfflineConfirmThreshold);

        computer.IsOnline = effectiveOnline;
        computer.LastError = online
            ? null
            : effectiveOnline
                ? "probe timed out (busy)"   // unconfirmed single failure — soft note, NOT an offline state
                : error;                     // confirmed offline — the real reason

        // A host that just came back online (offline→online) may have actually rebooted: clear any
        // "degraded WinRM" flag so we probe it again, and open a short re-probe window (a just-booted
        // box transiently still reports reboot-pending, so a single probe could strand the amber dot).
        if (online && previous == false)
        {
            _degradedHosts.TryRemove(computer.Name, out _);
            _rebootRecheckBudget[computer.Name] = PostBootRebootRechecks;
        }

        // While the Windows Update view is up, keep the Pending Reboot column live — a small
        // registry/SCCM-aggregated probe over WinRM, throttled so a large fleet doesn't open dozens
        // of runspaces at once. DON'T re-probe on every 20s pass: that churns a fresh WinRM shell per
        // online box each pass (which can poison a degraded target). Instead probe every box — pending
        // or not — on a single slow cadence (RebootPendingRecheckInterval, ~5 min); the 20s loop above
        // does only the cheap ping. The brief post-boot recheck window and a degraded-retry still probe
        // promptly every pass as overrides. A degraded host is backed off (re-tested only every
        // DegradedRetryInterval) but DOES get retried so we notice it recovered.
        bool degraded = _degradedHosts.TryGetValue(computer.Name, out DateTime retryAt);
        bool backoffActive = degraded && DateTime.UtcNow < retryAt;
        bool degradedRetryDue = degraded && !backoffActive;
        // A Kerberos-rejected box can never answer a WinRM probe (its SPN is broken by design) — don't
        // probe it at all, or it spams "Reboot probe failing" every cycle. Its reboot state comes from
        // the 2016 lane's DCOM Verify instead.
        bool winRmUnsupported = _winRmRebootProbeUnsupported.ContainsKey(computer.Name);
        if (online && IsUpdateMode && !backoffActive && !winRmUnsupported)
        {
            bool recheck = _rebootRecheckBudget.TryGetValue(computer.Name, out int budget) && budget > 0;
            // Every box — pending or not — is re-probed on a single slow cadence (RebootPendingRecheckInterval,
            // ~5 min) rather than on every 20s pass. A not-pending box notices it became pending; a pending box
            // self-clears if it rebooted out-of-band — both without churning a fresh WinRM shell each pass.
            bool cadenceDue = !_lastRebootProbeAt.TryGetValue(computer.Name, out DateTime lastProbe)
                || DateTime.UtcNow - lastProbe >= RebootPendingRecheckInterval;
            // The post-boot recheck window and a degraded-retry are prompt overrides — they probe every pass
            // (the latter's job is to re-test WinRM) until they resolve, regardless of the slow cadence.
            if (recheck || degradedRetryDue || cadenceDue)
            {
                // No ConfigureAwait(false): this method mutates data-bound Computer state after the
                // await (and below), so keep the continuation on the captured UI context.
                await ProbeRebootPendingAsync(computer, token);
                _lastRebootProbeAt[computer.Name] = DateTime.UtcNow;

                if (recheck)
                {
                    // Stop the post-boot rechecks once we get a clean (not-pending) read; otherwise
                    // spend down the budget so a stuck-pending box doesn't get probed forever. When the
                    // last recheck is spent (budget would hit 0), drop the entry rather than leaving a
                    // stale zero in the map — a genuine reboot reopens the window via the offline→online
                    // reset above.
                    if (computer.RebootRequired == false || budget <= 1)
                    {
                        _rebootRecheckBudget.TryRemove(computer.Name, out _);
                    }
                    else
                    {
                        _rebootRecheckBudget[computer.Name] = budget - 1;
                    }
                }
            }
        }

        if (previous == effectiveOnline)
        {
            return; // unchanged — leave LastStatus as-is
        }

        computer.LastStatus = effectiveOnline ? "Online" : "Offline";
        if (effectiveOnline)
        {
            // Came back from a known-offline state (a reboot/shutdown) — the BatchPatch-style
            // "it's back" signal. Include the down-time when we caught the moment it went down;
            // otherwise still announce the return (don't depend on having seen the down start).
            if (previous == false)
            {
                // Don't bake the reboot-pending state into this static string — right after boot
                // the probe often still reports "pending" for a moment, and the text would then go
                // stale once it clears. The live Pending-Reboot dot + the amber status chip show
                // pending state; this message just reports the return (with down-time if we caught
                // the moment it went down).
                string back = computer.WentOfflineAt is { } downAt
                    ? $"Back online {DateTime.Now:HH:mm} (down {FormatDownDuration(DateTime.Now - downAt)})"
                    : $"Back online {DateTime.Now:HH:mm}";

                computer.RebootMessage = back;
                _activity.Info(computer.Name, back);
            }
            else
            {
                _activity.Info(computer.Name, previous is null ? "Online" : "Came online");
            }

            computer.WentOfflineAt = null;
        }
        else
        {
            // Was up, now unreachable — most often a reboot/shutdown. Start the "waiting" clock so the
            // return trip can be timed. Gate on WasConfirmedOnline so a box that only ever answered ICMP
            // ping (a powered-off server's BMC/iDRAC) reads a calm "Offline" — not a false went-offline
            // event — while a genuinely-managed box that dropped (a reboot for patching) keeps its tracking.
            bool trackReturn = ReachabilityGating.ShouldTrackOfflineReturn(previous, computer.WasConfirmedOnline);
            if (trackReturn)
            {
                computer.WentOfflineAt = DateTime.Now;
                computer.RebootMessage = $"Offline since {DateTime.Now:HH:mm} — waiting for it to come back…";
            }

            _activity.Warn(computer.Name, trackReturn ? $"Went offline — {error}" : $"Offline — {error}");
        }
    }

    /// <summary>Compact down-time for the reboot message: "45s", "3m 12s", "1h 4m".</summary>
    private static string FormatDownDuration(TimeSpan d)
    {
        if (d.TotalMinutes < 1)
        {
            return $"{(int)d.TotalSeconds}s";
        }

        if (d.TotalHours < 1)
        {
            return $"{(int)d.TotalMinutes}m {d.Seconds}s";
        }

        return $"{(int)d.TotalHours}h {d.Minutes}m";
    }

    /// <summary>
    /// One pending-reboot pass against an online row, throttled so a big fleet doesn't open
    /// dozens of runspaces at once. Best-effort: a probe failure leaves the row's previous
    /// <see cref="Computer.RebootRequired"/> in place and the next tick will retry.
    /// </summary>
    private async Task ProbeRebootPendingAsync(Computer computer, CancellationToken token)
    {
        // No ConfigureAwait(false) on either await: the result-application below mutates data-bound
        // Computer state (RebootRequired/RebootMessage/UpdateMessage/WentOfflineAt), so the
        // continuation must stay on the captured UI context (losing it at the throttle wait would
        // also strand the probe continuation off-thread).
        await _rebootProbeThrottle.WaitAsync(token);
        // Per-host deadline: declared BEFORE the try so the timeout catch's `when` filter below can see
        // it. The linked token goes INTO the probe (not just around the await) so the deadline reaches
        // the WinRM invoke itself — that is what unblocks a hung CCM provider and releases the gate slots.
        using var perHost = CancellationTokenSource.CreateLinkedTokenSource(token);
        perHost.CancelAfter(TimeSpan.FromSeconds(RebootProbeTimeoutSeconds));
        try
        {
            bool? was = computer.RebootRequired;
            // background: true — this is the monitor's low-priority reboot-pending poll, so it yields
            // to operator actions on the per-host WinRM shell gate. The operator-triggered reboot-verify
            // (ReportPostRebootOutcomeAsync) calls IsRebootPendingAsync directly at the default
            // (background: false) so it keeps operator priority.
            bool? pending = await _rebootProbe.IsRebootPendingAsync(computer.Name, CurrentPsCredential(), perHost.Token, background: true);
            computer.WasConfirmedOnline = true; // reaching here means WinRM answered — genuinely managed

            // We got here with no shell-init failure → WinRM is healthy. If this host was flagged
            // degraded, it has recovered: clear the flag + the stale "WinRM temporarily unavailable"
            // message so the user sees it's working again (this is the path that self-heals once the
            // probe starts answering again).
            if (_degradedHosts.TryRemove(computer.Name, out _))
            {
                if (computer.RebootMessage is { } stale && stale.StartsWith("WinRM temporarily unavailable", StringComparison.Ordinal))
                {
                    computer.RebootMessage = null;
                }

                _activity.Info(computer.Name, "WinRM healthy again — resuming reboot probes.");
            }

            if (pending.HasValue)
            {
                computer.RebootRequired = pending.Value;

                // Reboot just resolved (was pending, now clear) — the reliable "it's back, reboot
                // done" signal (this transition is what turns the dot green, so it always runs even
                // if the monitor never caught the brief offline window). Narrate it and strip the
                // now-stale reboot-required tail the install left on the update message.
                if (was == true && !pending.Value)
                {
                    computer.RebootMessage = $"Reboot complete — back online {DateTime.Now:HH:mm}";
                    computer.WentOfflineAt = null;

                    // Separator-agnostic: the agent writes the tail with a middot separator, older builds
                    // used a comma - remove whichever so the message stops contradicting the green pill.
                    computer.UpdateMessage = UpdateMessageText.WithoutRebootRequiredTail(computer.UpdateMessage);

                    _activity.Info(computer.Name, "Reboot complete — back online, no reboot pending");
                }
            }
        }
        catch (OperationCanceledException) when (perHost.IsCancellationRequested && !token.IsCancellationRequested)
        {
            // Per-host timeout, not a Stop (!token at filter time proves it). Quiet backoff, swallowed —
            // it must never abort the monitor pass. No row-state write: RebootRequired keeps its
            // last-known value per this method's contract, and a merely-slow box is never painted failed.
            bool firstTime = !_degradedHosts.ContainsKey(computer.Name);
            _degradedHosts[computer.Name] = DateTime.UtcNow + DegradedRetryInterval;
            if (firstTime)
            {
                _activity.Warn(computer.Name,
                    $"Reboot probe timed out after {RebootProbeTimeoutSeconds}s — backing off (retry every {DegradedRetryInterval.TotalMinutes:N0} min).");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (KerberosWrongPrincipalException)
        {
            // 0x80090322 — this box rejects Kerberos by design (its http SPN is the SSRS service account),
            // so a WinRM reboot probe will NEVER succeed. Mark it unsupported and stop probing it this
            // session (logged once); the 2016 lane's DCOM Verify covers its reboot state. This is what
            // ends the "Reboot probe failing every 5 min" spam on the Vision boxes.
            if (_winRmRebootProbeUnsupported.TryAdd(computer.Name, 0))
            {
                _activity.Info(computer.Name, "WinRM reboot probe not supported here (Kerberos) — using the 2016 lane's Verify instead; stopping reboot probes for this host.");
            }
        }
        catch (Exception ex)
        {
            // ANY remote failure means this host's probe isn't working — including a lost session or
            // a persistent error, which previously fell into a bare catch and left no trace at all.
            // Back off (don't hammer a sick box with fresh shells every tick) but keep retrying every
            // DegradedRetryInterval so we notice recovery, and surface it ONCE so a stuck monitor is
            // diagnosable instead of silently dark. The actionable reboot-pending case gets a specific
            // row message; other failures just get the back-off + a single activity-log line.
            bool firstTime = !_degradedHosts.ContainsKey(computer.Name);
            _degradedHosts[computer.Name] = DateTime.UtcNow + DegradedRetryInterval;
            if (firstTime)
            {
                if (ex is RemoteShellInitException)
                {
                    // A shell-init failure is usually a transient WinRM hiccup (a busy box / too many open
                    // shells under load), NOT proof of a pending reboot — so we never tell the user to
                    // "reboot the target" (the old message did, even on healthy boxes — that was the noise).
                    // Consult the KNOWN reboot state only to note an already-pending box; neither branch
                    // prescribes a reboot. Both start with "WinRM temporarily unavailable" so the recovery
                    // path above clears whichever was set.
                    computer.RebootMessage = computer.RebootRequired == true
                        ? $"WinRM temporarily unavailable on {computer.Name} (reboot still pending) — backing off."
                        : $"WinRM temporarily unavailable on {computer.Name} — backing off, will retry.";
                }

                _activity.Warn(computer.Name, $"Reboot probe failing — backing off (retry every {DegradedRetryInterval.TotalMinutes:N0} min). {ex.Message}");
            }
        }
        finally
        {
            _rebootProbeThrottle.Release();
        }
    }

    /// <summary>Standalone "Check All" per-row work: pings + pulls SCCM client health under one shared-throttle
    /// slot. The combined Check Vitals calls <see cref="CheckRowCoreAsync"/> directly so it can hold a single
    /// slot across both health and vitals.</summary>
    private async Task CheckRowAsync(Computer computer, CancellationToken token)
    {
        computer.LastStatus = "Checking…"; // immediate pending state before queueing, so a waiting row never looks idle
        await _remoteSweepThrottle.Active.WaitAsync(token);
        try
        {
            await CheckRowCoreAsync(computer, token);
        }
        finally
        {
            _remoteSweepThrottle.Active.Release();
        }
    }

    /// <summary>Fresh per-sweep reachability verdict for the Health-grid doomed-probe skip: true when the host
    /// is unreachable by BOTH ICMP ping AND an ambient DCOM/WMI probe (the identity/channel the DCOM vitals
    /// fallback uses). Ambient (credential: null), NOT IsOnline/ProbeReachabilityAsync — whose DCOM leg is
    /// credential-gated and would false-negative a DCOM-reachable box on the ambient login. Recomputed each
    /// call — never latched — so a box that answers on a later sweep recovers.</summary>
    private async Task<bool> IsGenuinelyOfflineAsync(string host, CancellationToken token)
    {
        bool ping;
        try
        {
            ping = (await _pinger.PingAsync(host, PingTimeoutMs, token)).IsOnline;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            ping = false;
        }

        if (ping)
        {
            return false;
        }

        bool dcom = (await _hostProbe.CanReachAsync(host, credential: null, cancellationToken: token)).Reachable;
        return ReachabilityGating.ShouldSkipAsOffline(pingReachable: ping, dcomReachable: dcom);
    }

    // Ping + SCCM client-health pull for one row — the un-throttled core: the CALLER owns the shared
    // _remoteSweepThrottle slot (so Check Vitals holds one slot across health+vitals, and Check All holds one
    // per row). Clears stale fields only once the work actually starts, so a queued row keeps its last-known
    // values until it's refreshed.
    private async Task<bool> CheckRowCoreAsync(Computer computer, CancellationToken token)
    {
        computer.LastError = null;
        computer.SiteCode = null;
        computer.AgentVersion = null;
        computer.RebootRequired = null;
        computer.MissingUpdates = null;
        computer.RunningUpdates = null;
        computer.UserLoggedOn = null;
        computer.LastBootTime = null;
        computer.LastStatus = "Checking…";

        bool pingOnline;
        try
        {
            pingOnline = (await _pinger.PingAsync(computer.Name, PingTimeoutMs, token)).IsOnline;
        }
        catch (OperationCanceledException)
        {
            computer.IsOnline = false;
            computer.LastStatus = "Cancelled";
            throw;
        }
        catch
        {
            pingOnline = false;
        }

        // Skip the doomed WinRM health + vitals on a box unreachable by BOTH ping AND an AMBIENT DCOM/WMI
        // probe (the same identity the DCOM vitals fallback uses). Such a box would only burn a ~20s WinRM
        // open-timeout (then a vitals DCOM-fallback timeout) before failing — mark it Offline and skip. A box
        // reachable by ping OR ambient DCOM (e.g. a Kerberos-broken box still readable over DCOM) is NOT
        // skipped, so it still gets its health/vitals. Ambient (credential: null), NOT IsOnline (whose DCOM
        // leg is credential-gated and would false-negative a DCOM-reachable box on the ambient login).
        // Recomputed every sweep, so a box that answers on a later pass recovers.
        if (!pingOnline)
        {
            bool dcomReachable = (await _hostProbe.CanReachAsync(computer.Name, credential: null, cancellationToken: token)).Reachable;
            if (ReachabilityGating.ShouldSkipAsOffline(pingReachable: pingOnline, dcomReachable: dcomReachable))
            {
                ResetVitals(computer);   // drop any stale vitals numbers a prior sweep left in the cells
                computer.IsOnline = false;
                computer.VitalityScore = null;
                computer.VitalityBand = VitalityBand.Offline;
                computer.VitalityReasons = ["Offline"];
                computer.LastStatus = "Offline";
                return false;            // tell the caller (CheckHealthAndVitalsRowAsync) to skip the vitals half
            }
        }

        // Always attempt health, regardless of ping — many servers block ICMP but
        // answer WinRM. "Online" means it responded (ICMP or WinRM), using the
        // active credential.
        using var healthPerHost = CancellationTokenSource.CreateLinkedTokenSource(token);
        healthPerHost.CancelAfter(TimeSpan.FromSeconds(HealthPerHostTimeoutSeconds));
        try
        {
            SccmClientInfo health = await _configMgr.GetClientHealthAsync(computer.Name, CurrentPsCredential(), healthPerHost.Token);
            computer.IsOnline = true;
            computer.WasConfirmedOnline = true; // WinRM/ConfigMgr answered — genuinely managed, not just pingable
            computer.SiteCode = health.SiteCode;
            computer.AgentVersion = health.ClientVersion;
            computer.RebootRequired = health.RebootRequired;
            if (health.ClientSdkFailed)
            {
                // The ClientSDK namespace didn't answer (corrupt/denied WMI): Missing/Running
                // updates are UNKNOWN, not compliant — null renders the grey "?" instead of a
                // false green check on exactly the damaged client this check exists to catch.
                // RebootRequired above still carries the registry CBS/WU legs (the same
                // degradation the reboot probe has on a broken ClientSDK).
                computer.MissingUpdates = null;
                computer.RunningUpdates = null;
                computer.UserLoggedOn = health.UserLoggedOn;
                computer.LastBootTime = health.LastBootTime;
                computer.LastStatus = "SCCM ClientSDK unavailable — updates state unknown";
                computer.LastError = "SCCM ClientSDK didn't answer — Missing/Running updates unknown; the client likely needs repair.";
                _activity.Warn(computer.Name, computer.LastError);
            }
            else
            {
                computer.MissingUpdates = health.MissingUpdates;
                computer.RunningUpdates = health.RunningUpdates;
                computer.UserLoggedOn = health.UserLoggedOn;
                computer.LastBootTime = health.LastBootTime;
                computer.LastStatus = SummarizeHealth(health);
                _activity.Info(computer.Name, $"Health: {computer.LastStatus}");
            }
        }
        catch (OperationCanceledException) when (healthPerHost.IsCancellationRequested && !token.IsCancellationRequested)
        {
            // Per-host timeout (not a user Stop): leave IsOnline as the ping result and let
            // the combined pass proceed to the vitals half — a box with hung SCCM WMI may
            // still answer OS vitals (which has its own 120s cap).
            computer.IsOnline = pingOnline;
            computer.LastError = $"Health check timed out after {HealthPerHostTimeoutSeconds}s";
            computer.LastStatus = "Health timed out";
            _activity.Warn(computer.Name, computer.LastError);
        }
        catch (OperationCanceledException)
        {
            computer.IsOnline = false;
            computer.LastStatus = "Cancelled";
            throw;
        }
        catch (SccmQueryException ex)
        {
            // WinRM reached the box but it isn't a ConfigMgr client (or the query was
            // denied) — it's still up.
            computer.IsOnline = true;
            computer.WasConfirmedOnline = true; // WinRM answered — genuinely managed, not just pingable
            computer.LastStatus = "Online · no ConfigMgr client";
            computer.LastError = ex.Message;
            _activity.Warn(computer.Name, $"No ConfigMgr client — {ex.Message}");
        }
        catch (Exception ex) when (ex.IsWinRmUnavailable())
        {
            // WinRM is broken on this box (Kerberos/SPN or service down). The ConfigMgr health read can't
            // run over WinRM here — keep the ping reachability and show a plain message instead of raw SSPI
            // text. (Vitals still reads over DCOM; the detail Connection callout explains the specifics.)
            computer.IsOnline = pingOnline;
            computer.LastStatus = pingOnline ? "Online · WinRM unavailable" : "Offline";
            string swHint = ex is KerberosWrongPrincipalException ? WinRmDeadEnd.SoftwareRedirect : string.Empty;
            computer.LastError = "WinRM is broken on this box, so the health check can't run remotely here." + swHint;
            _activity.Warn(computer.Name, $"Health check skipped — WinRM unavailable on this box.{swHint}");
        }
        catch (Exception ex)
        {
            // Couldn't reach it over WinRM — fall back to the ping result.
            computer.IsOnline = pingOnline;
            computer.LastStatus = pingOnline ? "Online · health unavailable" : "Offline";
            computer.LastError = ex.Message;
            _activity.Warn(computer.Name, $"Check failed — {ex.Message}");
        }

        return true; // reachable by ping or ambient DCOM — the caller runs the vitals half
    }

    /// <summary>Standalone vitals triage per-row work (right-click ▸ Triage): reads vitals under one
    /// shared-throttle slot. The combined Check Vitals calls <see cref="CheckVitalsCoreAsync"/> directly
    /// so it can hold a single slot across both health and vitals.</summary>
    private async Task CheckVitalsRowAsync(Computer computer, CancellationToken token)
    {
        computer.LastStatus = "Checking…"; // immediate pending state before queueing, so a waiting row never looks idle
        await _remoteSweepThrottle.Active.WaitAsync(token);
        try
        {
            await CheckVitalsCoreAsync(computer, token);
        }
        finally
        {
            _remoteSweepThrottle.Active.Release();
        }
    }

    // Reads deep OS vitals for one row and scores it — the un-throttled core: the CALLER owns the shared
    // _remoteSweepThrottle slot. Mirrors CheckRowCoreAsync's clear→pull→catch-ladder shape; stays on the UI
    // context (no ConfigureAwait(false)) so the row-property writes that drive the grid are marshalled
    // correctly. A per-host timeout stops a hung box stalling it.
    private async Task CheckVitalsCoreAsync(Computer computer, CancellationToken token)
    {
        ResetVitals(computer);
        computer.LastStatus = "Reading vitals…";

        using var perHost = CancellationTokenSource.CreateLinkedTokenSource(token);
        perHost.CancelAfter(TimeSpan.FromSeconds(VitalsPerHostTimeoutSeconds));
        try
        {
            MachineVitals v = await _vitals.GetVitalsAsync(computer.Name, CurrentPsCredential(), perHost.Token);
            ApplyVitals(computer, v);
        }
        catch (OperationCanceledException) when (perHost.IsCancellationRequested && !token.IsCancellationRequested)
        {
            // Per-host timeout (not a user Stop): mark it unknown and let the rest of the sweep run.
            computer.LastError = $"Vitals read timed out after {VitalsPerHostTimeoutSeconds}s";
            computer.LastStatus = "Vitals timed out";
            computer.VitalityScore = null;
            computer.VitalityBand = VitalityBand.Unknown;
            _activity.Warn(computer.Name, computer.LastError);
        }
        catch (OperationCanceledException)
        {
            computer.LastStatus = "Cancelled";
            throw;
        }
        catch (VitalsProbeException ex)
        {
            // Reached (or tried) the box but couldn't read it — score Offline if ping says down,
            // else Unknown. Surface the reason; never an empty catch.
            computer.VitalityScore = null;
            computer.VitalityBand = computer.IsOnline == false ? VitalityBand.Offline : VitalityBand.Unknown;
            computer.VitalityReasons = [ex.Message];
            computer.LastError = ex.Message;
            computer.LastStatus = "Vitals unavailable";
            _activity.Warn(computer.Name, $"Vitals unavailable — {ex.Message}");
        }
        catch (Exception ex)
        {
            computer.VitalityScore = null;
            computer.VitalityBand = VitalityBand.Unknown;
            computer.LastError = ex.Message;
            computer.LastStatus = "Vitals failed";
            _activity.Warn(computer.Name, $"Vitals failed — {ex.Message}");
        }
    }

    /// <summary>Clears a row's vitals before a (re-)read so a failure can't leave stale numbers showing.</summary>
    private static void ResetVitals(Computer computer)
    {
        computer.LastError = null;
        computer.SystemDriveFreePercent = null;
        computer.MemoryUsedPercent = null;
        computer.CpuLoadPercent = null;
        computer.StoppedAutoServiceCount = null;
        computer.Vitals = null;
        computer.VitalityReasons = [];
    }

    /// <summary>Copies a vitals snapshot onto the row and runs the scorer (the one source of truth).</summary>
    private void ApplyVitals(Computer computer, MachineVitals v)
    {
        // Mark the row reached/managed ONLY when the snapshot actually carried data. A blank flagged
        // snapshot (WinRM rejected Kerberos AND the DCOM fallback also failed → IsEmpty) is a FAILED read,
        // not a reach — marking it online/managed off an empty read wrongly re-triggers the monitor's
        // "Offline since… waiting" message on a genuinely-offline box. A partial DCOM read (some data) still
        // counts as a reach. The rest of this method runs regardless, so a Kerberos-broken box still
        // surfaces its degraded state (WinRmDegraded/caption below).
        if (v.IsGenuineReach)
        {
            computer.IsOnline = true; // it answered the remoting pull
            computer.WasConfirmedOnline = true; // genuinely reached over remoting, not just pingable
        }

        computer.SystemDriveFreePercent = v.SystemDriveFreePercent;
        computer.MemoryUsedPercent = v.MemoryUsedPercent;
        computer.CpuLoadPercent = v.CpuLoadPercent;
        computer.StoppedAutoServiceCount = v.StoppedAutoServiceCount;
        // Promote the logged-on signal too (the grid's "Users Online" column), guarded so a partial
        // DCOM-fallback read that couldn't see it doesn't blank a value a health check confirmed.
        if (v.UserLoggedOn is { } user)
        {
            computer.UserLoggedOn = user;
        }

        if (v.LastBootTime is { } boot)
        {
            computer.LastBootTime = boot;
        }

        if (v.RebootPending is { } pending)
        {
            computer.RebootRequired = pending;
        }

        // OS comes free with the vitals CIM pull — set it now so Details/grid don't lazy-load it later.
        if (!string.IsNullOrWhiteSpace(v.OperatingSystem))
        {
            computer.OperatingSystem = v.OperatingSystem;
        }

        // Classify the OS build (e.g. 14393 = Server 2016) so the 2016 panel self-populates and "Install
        // all" auto-routes. Only update when this read actually carried an OS — a partial read must never
        // clear a build we already confirmed (an unread box stays unclassified, never mis-routed).
        if (LcuRouting.ParseBuild(v.OperatingSystem) is { } osBuild)
        {
            computer.OsBuild = osBuild;
        }

        computer.Vitals = v;
        computer.VitalsCheckedAt = DateTime.Now;

        VitalityResult r = VitalityScorer.Score(v, computer.IsOnline);
        computer.VitalityScore = r.Score;
        computer.VitalityBand = r.Band;
        computer.VitalityReasons = r.Reasons;
        computer.VitalityNeedsAttention = r.NeedsAttention;

        // Connection callout (Machine Details): when WinRM was unusable and we fell back to SMB/DCOM,
        // surface WHAT failed + how to fix. Cleared when WinRM is healthy so the callout hides.
        computer.WinRmStateCaption = WinRmHealthGuidance.Caption(v.WinRmHealth);
        computer.WinRmFix = WinRmHealthGuidance.FixBullets(v.WinRmHealth);
        computer.WinRmFailureDetail = v.WinRmFailureDetail;
        computer.WinRmDegraded = computer.WinRmStateCaption is not null;

        computer.LastStatus = r.Score is { } s
            ? $"Vitality {s} ({r.Band})"
            : r.Band.ToString();
        _activity.Info(computer.Name,
            $"Vitals: {r.Band}{(r.Score is { } sc ? $" {sc}" : string.Empty)}"
            + (r.Reasons.Count > 0 ? " — " + string.Join(", ", r.Reasons) : string.Empty));
    }

    /// <summary>Removes the currently-selected rows from the grid (Delete key / menu).</summary>
    public void RemoveSelected()
    {
        var removed = SelectedComputers.ToList();
        foreach (Computer computer in removed)
        {
            Computers.Remove(computer);
            ForgetHostState(computer.Name);
        }

        SelectedComputers.Clear();

        if (FocusedComputer is { } focused && !Computers.Contains(focused))
        {
            FocusedComputer = null;
        }

        if (removed.Count > 0)
        {
            _activity.Info(null, $"Removed {removed.Count} machine(s) from this tab.");
        }

        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedComputerCount));
        OnPropertyChanged(nameof(ScanButtonLabel));
        OnPropertyChanged(nameof(InstallButtonLabel));
    }

    /// <summary>Drops a removed machine's per-host monitor state so stale name-keyed entries don't
    /// linger for the tab's lifetime (and can't prematurely clear a re-added machine's columns).</summary>
    private void ForgetHostState(string name)
    {
        _degradedHosts.TryRemove(name, out _);
        _rebootRecheckBudget.TryRemove(name, out _);
        _scheduledTasks.TryRemove(name, out _);
        _winRmRebootProbeUnsupported.TryRemove(name, out _);
        _lastRebootProbeAt.TryRemove(name, out _);
        _consecutiveProbeFailures.TryRemove(name, out _);
    }

    /// <summary>Mirrors the grid's selection into <see cref="SelectedComputers"/> (called from code-behind).</summary>
    public void SetSelection(IEnumerable<Computer> selected)
    {
        SelectedComputers.Clear();
        foreach (Computer computer in selected)
        {
            SelectedComputers.Add(computer);
        }

        // Toolbar Scan/Install labels reflect the current target (selected count, or "all").
        OnPropertyChanged(nameof(ScanButtonLabel));
        OnPropertyChanged(nameof(InstallButtonLabel));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedComputerCount));
        OnPropertyChanged(nameof(CanActOnSelection));
    }

    // --- selection-aware toolbar (Scan/Install act on the selection, or all rows when none) ------

    /// <summary>"Scan selected (N)" when rows are selected, else "Scan all".</summary>
    public string ScanButtonLabel =>
        SelectedComputers.Count > 0 ? $"Scan selected ({SelectedComputers.Count})" : "Scan all";

    /// <summary>"Install selected (N)" when rows are selected, else "Install all".</summary>
    public string InstallButtonLabel =>
        SelectedComputers.Count > 0 ? $"Install selected ({SelectedComputers.Count})" : "Install all";

    /// <summary>Toolbar Scan: scoped to the selection, or every row when nothing's selected.
    /// Disabled when the tab has no machines (see <see cref="CanScanOrInstallAll"/>).</summary>
    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanScanOrInstallAll))]
    private Task ScanTarget() =>
        SelectedComputers.Count > 0 ? ScanSelectedAsync([.. SelectedComputers]) : ScanUpdatesAsync();

    /// <summary>Toolbar Install: scoped to the selection, or every row when nothing's selected.
    /// Disabled when the tab has no machines (see <see cref="CanScanOrInstallAll"/>).</summary>
    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanScanOrInstallAll))]
    private Task InstallTarget() =>
        SelectedComputers.Count > 0 ? InstallSelectedAsync([.. SelectedComputers]) : InstallUpdatesAsync();

    /// <summary>
    /// Fires <paramref name="action"/> against every selected row in parallel on the shared WinRM
    /// budget, bounded per box so one hung target can't stall the batch. Source-generated as
    /// <c>TriggerScheduleCommand</c>; bound to each right-click menu item with the action as the
    /// command parameter. Registered as a PASSIVE operation (registerRows: false) so the Stop button
    /// cancels it but it never blocks — or is blocked by — another running sweep.
    /// </summary>
    [RelayCommand]
    private async Task TriggerScheduleAsync(ScheduleAction? action)
    {
        // No ConfigureAwait(false) anywhere in this method or the per-row helper: LastError is a
        // live-filtering grid property, so every continuation must stay on the captured UI context.
        if (action is null)
        {
            return;
        }

        var rows = SelectedComputers.ToList();
        if (rows.Count == 0)
        {
            return;
        }

        (CancellationTokenSource cts, OperationRecord record) = BeginOperation(action.Label, rows, registerRows: false);
        try
        {
            Task work = Task.WhenAll(rows.Select(row => TriggerScheduleRowAsync(row, action, record, cts.Token)));

            // Same Stop race as Enable WinRM: a Stop frees the UI immediately even while a CIM call
            // lingers on its background thread; per-row results already applied are kept.
            Task cancelledTask = Task.Delay(Timeout.Infinite, cts.Token);
            if (await Task.WhenAny(work, cancelledTask) == work)
            {
                await work;
            }
            else
            {
                _ = work.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException)
        {
            // Stop pressed — finished/in-flight rows keep their per-row results.
        }
        finally
        {
            EndOperation(cts, record);
        }
    }

    /// <summary>One client-action trigger against one box, bounded so a hung box can't stall the batch:
    /// the linked CTS drives cancellation into PSRunspaceHost, which stops/abandons and observes its own
    /// task — no belt needed on the WinRM path, unlike the DCOM enabler.</summary>
    private async Task TriggerScheduleRowAsync(Computer computer, ScheduleAction action, OperationRecord record, CancellationToken token)
    {
        using IDisposable slot = await _remoteSweepThrottle.AcquirePassiveAsync(token);
        using var perHost = CancellationTokenSource.CreateLinkedTokenSource(token); // before try — the catch filters need it in scope
        perHost.CancelAfter(TimeSpan.FromSeconds(ClientActionPerHostTimeoutSeconds));
        try
        {
            computer.LastError = null;
            computer.LastStatus = $"{action.Label}…";
            computer.LastStatus = await _configMgr.TriggerScheduleAsync(
                computer.Name, action, CurrentPsCredential(), perHost.Token);
            _activity.Info(computer.Name, computer.LastStatus);
        }
        catch (OperationCanceledException) when (perHost.IsCancellationRequested && !token.IsCancellationRequested)
        {
            computer.LastStatus = "Timed out";
            computer.LastError = $"{action.Label} timed out after {ClientActionPerHostTimeoutSeconds}s";
            _activity.Warn(computer.Name, $"{action.Label} timed out on {computer.Name} after {ClientActionPerHostTimeoutSeconds}s.");
        }
        catch (OperationCanceledException)
        {
            computer.LastStatus = "Cancelled";
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // A Stop racing a failure must read "Cancelled", never "failed" — the remote call may
            // surface cancellation as a wrapped SDK exception, not an OCE (and never spray Error lines).
            computer.LastStatus = "Cancelled";
        }
        catch (SccmQueryException ex)
        {
            computer.LastStatus = "Action failed";
            computer.LastError = ex.Message;
            _activity.Error(computer.Name, $"{action.Label} failed — {ex.Message}");
        }
        catch (RemoteShellInitException ex)
        {
            // Transient shell-init (MaxShellsPerUser / busy box) — calm retry message, Warn not Error.
            computer.LastStatus = "WinRM busy";
            computer.LastError = ex.Message;
            _activity.Warn(computer.Name, $"{action.Label} skipped — {ex.Message}");
        }
        catch (Exception ex) when (ex.IsWinRmUnavailable())
        {
            string swHint = ex is KerberosWrongPrincipalException ? WinRmDeadEnd.SoftwareRedirect : string.Empty;
            computer.LastStatus = "WinRM unavailable";
            computer.LastError = $"WinRM is broken on this box, so {action.Label} can't run remotely here." + swHint;
            _activity.Warn(computer.Name, $"{action.Label} skipped — WinRM unavailable on this box.{swHint}");
        }
        catch (Exception ex)
        {
            // One degraded box must never abort the action on the rest of the selection — this closes
            // the old RemoteShellInitException escape that aborted the whole sweep.
            computer.LastStatus = "Action failed";
            computer.LastError = ex.Message;
            _activity.Error(computer.Name, $"{action.Label} failed — {ex.Message}");
        }
        finally
        {
            IncrementSweepCompleted(record);
        }
    }

    /// <summary>
    /// Enables WinRM/PSRemoting (over DCOM) on the selected machines. Invoked from the
    /// context menu after the user confirms. Runs regardless of ping state — DCOM is a
    /// different channel, and these are exactly the machines WinRM can't reach yet.
    /// Parallel (capped at <see cref="_enableWinRmThrottle"/>) with a per-box time bound, so one
    /// hung box no longer strands the rest of the selection; registered as a PASSIVE operation
    /// (registerRows: false) so the Stop button cancels it but it never blocks — or is blocked
    /// by — another running sweep.
    /// </summary>
    public async Task EnableWinRmSelectedAsync()
    {
        // No ConfigureAwait(false) anywhere in this method or the per-row helper: LastError is a
        // live-filtering grid property, so every continuation must stay on the captured UI context.
        var rows = SelectedComputers.ToList();
        if (rows.Count == 0)
        {
            return;
        }

        (CancellationTokenSource cts, OperationRecord record) = BeginOperation("Enabling WinRM", rows, registerRows: false);
        try
        {
            Task work = Task.WhenAll(rows.Select(row => EnableWinRmRowAsync(row, record, cts.Token)));

            // Same Stop race as the patch sweep: a Stop frees the UI immediately even while a CIM
            // call lingers on its background thread; per-row results already applied are kept.
            Task cancelledTask = Task.Delay(Timeout.Infinite, cts.Token);
            if (await Task.WhenAny(work, cancelledTask) == work)
            {
                await work;
            }
            else
            {
                _ = work.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException)
        {
            // Stop pressed — finished/in-flight rows keep their per-row results.
        }
        finally
        {
            EndOperation(cts, record);
        }
    }

    /// <summary>One Enable WinRM attempt against one box, bounded so a hung DCOM target can't stall
    /// the batch: the enabler carries its own 20s CIM timeouts, and the 25s WaitAsync belt covers the
    /// case CIM's timeout can't (the RPC connect to a fully-offline target).</summary>
    private async Task EnableWinRmRowAsync(Computer computer, OperationRecord record, CancellationToken token)
    {
        await _enableWinRmThrottle.WaitAsync(token);
        try
        {
            computer.LastError = null;
            computer.LastStatus = "Enabling WinRM…";
            Task<string> enable = _winRm.EnableAsync(computer.Name, _credentials.Current, token);
            // The 25s belt below may abandon this task (a Stop, or an RPC connect that outruns CIM's
            // own timeout); observe its eventual fault so an abandoned call can't resurface minutes
            // later as a global "Background task error" line — the same observer idiom as the sweep.
            _ = enable.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
            computer.LastStatus = await enable.WaitAsync(TimeSpan.FromSeconds(25), token);
            _activity.Info(computer.Name, computer.LastStatus);
        }
        catch (OperationCanceledException)
        {
            computer.LastStatus = "Cancelled";
        }
        catch (TimeoutException)
        {
            computer.LastStatus = "Enable WinRM timed out";
            computer.LastError = "No response from DCOM within 25s";
            _activity.Warn(computer.Name, $"Enable WinRM timed out on {computer.Name} — no DCOM response within 25s.");
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // CIM cancellation surfaces as CimException → WinRmEnableException, not OCE — a Stop
            // must read "Cancelled", never "failed" (and never spray Error log lines).
            computer.LastStatus = "Cancelled";
        }
        catch (Exception ex)
        {
            computer.LastStatus = "Enable WinRM failed";
            computer.LastError = ex.Message;
            _activity.Error(computer.Name, $"Enable WinRM failed — {ex.Message}");
        }
        finally
        {
            _enableWinRmThrottle.Release();
            IncrementSweepCompleted(record);
        }
    }

    /// <summary>
    /// Force-reboots the selected machines now (<c>shutdown /r /f /t 5</c> — the small delay lets the
    /// WinRM call return cleanly before the box goes down). The most common Windows-Update follow-up;
    /// invoked from the context menu after the user confirms. The monitor reports each box back online.
    /// </summary>
    public async Task RebootForceSelectedAsync(IReadOnlyList<Computer>? rows = null, CancellationToken token = default)
    {
        const string script = "shutdown.exe /r /f /t 5 /c \"Vivre forced reboot\"";
        foreach (Computer computer in rows ?? SelectedComputers.ToList())
        {
            computer.LastError = null;
            computer.LastStatus = "Rebooting (force)…";
            try
            {
                PSExecutionResult result = IsLocalHost(computer.Name)
                    ? await _powerShell.RunLocalAsync(script, token)
                    : await _powerShell.RunRemoteAsync(computer.Name, script, CurrentPsCredential(), cancellationToken: token);

                if (result.HadErrors)
                {
                    computer.LastStatus = "Reboot command failed";
                    computer.LastError = result.Errors.Count > 0 ? result.Errors[0] : "shutdown reported an error";
                    _activity.Error(computer.Name, $"Force reboot failed — {computer.LastError}");
                }
                else
                {
                    computer.LastStatus = "Reboot forced — going down";
                    computer.WasConfirmedOnline = true; // the reboot ran over WinRM — genuinely managed, so track its return
                    computer.RebootMessage = $"Forced reboot sent {DateTime.Now:HH:mm}";
                    _activity.Info(computer.Name, "Forced reboot (shutdown /r /f /t 5)");
                    // Once the machine comes back online the monitor will re-probe reboot-pending status
                    // (up to PostBootRebootRechecks times) so the Reboot Pending column clears automatically.
                    _rebootRecheckBudget[computer.Name] = PostBootRebootRechecks;
                }
            }
            catch (OperationCanceledException)
            {
                computer.LastStatus = "Cancelled";
                throw;
            }
            catch (Exception ex)
            {
                computer.LastStatus = "Reboot command failed";
                computer.LastError = ex.Message;
                _activity.Error(computer.Name, $"Force reboot failed — {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Sets WhatsUp Gold maintenance mode (enter/exit) for <paramref name="computers"/> in the
    /// background (fire-and-forget from the caller): it runs the WUG set under Windows PowerShell 5.1
    /// (mapping names → WUG DeviceIds server-side — it does NOT run on the target machines) and reports
    /// progress live into each row's <c>Command result</c> column + the activity log, so the caller can
    /// close its dialog and keep working. <paramref name="password"/> is the WhatsUp Gold login, kept
    /// separate from the target/remote credential and never stored; it's turned into plaintext only to
    /// hand to the child process via an environment variable (never logged or put on a command line).
    /// </summary>
    public async Task SetWugMaintenanceAsync(
        IReadOnlyList<Computer> computers,
        bool enable,
        string server,
        string username,
        System.Security.SecureString password,
        string reason,
        CancellationToken token = default)
    {
        if (computers.Count == 0)
        {
            return;
        }

        string mode = enable ? "ON" : "OFF";
        IReadOnlyList<string> names = [.. computers.Select(c => c.Name)];

        // Immediate per-row feedback in the grid + a start line in the activity log.
        foreach (Computer c in computers)
        {
            c.CommandResult = $"WhatsUp Gold: setting maintenance {mode}…";
        }

        _activity.Info(null, $"WhatsUp Gold: setting maintenance {mode} for {names.Count} machine(s)…");

        // Live step updates from the run (constructed here, on the UI thread, so reports marshal back
        // to it). The WUG inventory pull is genuinely slow on a large install, so we show progress and
        // give it a generous cap rather than cutting a working run off at an arbitrary few minutes.
        var progress = new Progress<string>(step => _activity.Info(null, $"WhatsUp Gold: {step}"));

        WugMaintenanceResult result;
        try
        {
            string plain = new System.Net.NetworkCredential(string.Empty, password).Password;
            result = await WugMaintenance.RunAsync(names, enable, server, username, plain, reason, TimeSpan.FromMinutes(10), token, progress);
        }
        catch (OperationCanceledException)
        {
            // The operator cancelled — that is NOT a WUG failure. Leave a neutral per-row note and an info
            // line, rather than stamping every row "failed — The operation was canceled."
            foreach (Computer c in computers)
            {
                c.CommandResult = "WhatsUp Gold: cancelled";
            }

            _activity.Info(null, "WhatsUp Gold maintenance cancelled.");
            return;
        }
        catch (Exception ex)
        {
            foreach (Computer c in computers)
            {
                c.CommandResult = $"WhatsUp Gold: failed — {ex.Message}";
            }

            _activity.Error(null, $"WhatsUp Gold maintenance failed — {ex.Message}");
            return;
        }

        // Per-row outcome: matched rows reflect the set; names with no WUG device are called out.
        var unmatched = new HashSet<string>(result.Unmatched, StringComparer.OrdinalIgnoreCase);
        foreach (Computer c in computers)
        {
            c.CommandResult = unmatched.Contains(c.Name)
                ? "WhatsUp Gold: no matching device (by IP)"
                : result.Ok
                    ? $"WhatsUp Gold: maintenance {mode}"
                    : "WhatsUp Gold: failed";
        }

        if (result.Ok)
        {
            _activity.Info(null, $"WhatsUp Gold maintenance {mode} for {result.DevicesSet} device(s)"
                + (result.Unmatched.Count > 0 ? $" ({result.Unmatched.Count} unmatched)" : string.Empty));
        }
        else
        {
            _activity.Error(null, $"WhatsUp Gold maintenance failed — {result.Error}");
        }
    }

    /// <summary>
    /// Reads the current WhatsUp Gold maintenance state for <paramref name="computers"/> in the
    /// background (fire-and-forget from the caller). READ-ONLY — it never sets maintenance; it writes
    /// each machine's current state into its <c>Command result</c> column and adds one activity-log
    /// summary. <paramref name="password"/> is the WhatsUp Gold login, kept separate from the
    /// target/remote credential and never stored; it's turned into plaintext only inside the reused
    /// wrapper (<see cref="GetWugMaintenanceStateAsync"/>).
    /// </summary>
    public async Task CheckWugStateAsync(
        IReadOnlyList<Computer> computers,
        string server,
        string username,
        System.Security.SecureString password,
        CancellationToken token = default)
    {
        if (computers.Count == 0)
        {
            return;
        }

        // Immediate per-row feedback in the grid + a start line in the activity log.
        foreach (Computer c in computers)
        {
            c.CommandResult = "WhatsUp Gold: checking state…";
        }

        _activity.Info(null, $"WhatsUp Gold: checking maintenance state for {computers.Count} machine(s)…");

        IReadOnlyList<string> names = [.. computers.Select(c => c.Name)];

        Vivre.Core.Wug.WugMaintenanceStateResult result;
        try
        {
            // NO ConfigureAwait(false): the dispatcher continuation is what keeps the post-await
            // CommandResult writes UI-thread-safe (same mechanism as SetWugMaintenanceAsync). The wrapper
            // folds all exceptions (including cancellation) into a failure result, so an
            // OperationCanceledException branch here would be unreachable.
            result = await GetWugMaintenanceStateAsync(names, server, username, password, token);
        }
        catch (Exception ex)
        {
            // Belt-and-suspenders parity with SetWugMaintenanceAsync — the wrapper should never throw.
            foreach (Computer c in computers)
            {
                c.CommandResult = $"WhatsUp Gold: failed — {ex.Message}";
            }

            _activity.Error(null, $"WhatsUp Gold state check failed — {ex.Message}");
            return;
        }

        // Bucket each in-scope machine into exactly one state, counting as we go. A whole-read failure
        // folds into unknown (with the error appended) so it surfaces per-row, not just in the summary.
        int inMaint = 0, notIn = 0, noMatch = 0, unknown = 0;
        var unmatched = new HashSet<string>(result.Unmatched, StringComparer.OrdinalIgnoreCase);
        foreach (Computer c in computers)
        {
            if (unmatched.Contains(c.Name))
            {
                c.CommandResult = "WhatsUp Gold: no matching device (by IP)";
                noMatch++;
            }
            else if (result.ByName.TryGetValue(c.Name, out bool? s) && s == true)
            {
                c.CommandResult = "WhatsUp Gold: in maintenance";
                inMaint++;
            }
            else if (s == false)
            {
                c.CommandResult = "WhatsUp Gold: not in maintenance";
                notIn++;
            }
            else
            {
                c.CommandResult = result.Error is null
                    ? "WhatsUp Gold: state unknown"
                    : $"WhatsUp Gold: state unknown — {result.Error}";
                unknown++;
            }
        }

        if (result.Error is null)
        {
            _activity.Info(null, $"WhatsUp Gold state: {inMaint} in maintenance, {notIn} not, {noMatch} no matching device, {unknown} unknown");
        }
        else
        {
            _activity.Error(null, $"WhatsUp Gold state check failed — {result.Error}");
        }
    }

    /// <summary>
    /// Tests whether the WhatsUpGoldPS module is installed and whether the supplied credentials can
    /// reach <paramref name="server"/>.  The <paramref name="password"/> is converted to plaintext
    /// only via <see cref="System.Net.NetworkCredential"/> and passed to the child process via the
    /// <c>VIVRE_WUG_PASS</c> environment variable — never on a command line, never stored.
    /// </summary>
    public async Task<Vivre.Core.Wug.WugPreflightResult> TestWugConnectionAsync(
        string server,
        string username,
        System.Security.SecureString password,
        CancellationToken token = default)
    {
        try
        {
            string plain = new System.Net.NetworkCredential(string.Empty, password).Password;
            return await Vivre.Core.Wug.WugMaintenance.TestConnectionAsync(server, username, plain, TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new Vivre.Core.Wug.WugPreflightResult(false, false, ex.Message);
        }
    }

    /// <summary>
    /// Reads the current WhatsUp Gold maintenance state for <paramref name="names"/> without changing
    /// anything, keyed back by machine name (<c>true</c> = in maintenance, <c>false</c> = not, <c>null</c>
    /// = unknown).  The <paramref name="password"/> is converted to plaintext only via <see
    /// cref="System.Net.NetworkCredential"/> and passed to the child process via the <c>VIVRE_WUG_PASS</c>
    /// environment variable — never on a command line, never stored.
    /// </summary>
    public async Task<Vivre.Core.Wug.WugMaintenanceStateResult> GetWugMaintenanceStateAsync(
        IReadOnlyList<string> names,
        string server,
        string username,
        System.Security.SecureString password,
        CancellationToken token = default)
    {
        try
        {
            string plain = new System.Net.NetworkCredential(string.Empty, password).Password;
            // Per-device lookup cost is pilot-checked; caps at the same 10 min the set run uses.
            TimeSpan timeout = TimeSpan.FromSeconds(Math.Min(60 + 5 * names.Count, 600));
            return await Vivre.Core.Wug.WugMaintenance.GetMaintenanceStateAsync(names, server, username, plain, timeout, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new Vivre.Core.Wug.WugMaintenanceStateResult(
                new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase), [], ex.Message);
        }
    }

    /// <summary>
    /// Installs the WhatsUpGoldPS module from the PowerShell Gallery for the current user.
    /// Operator-consented; the silent auto-install inside <c>SetWugMaintenanceAsync</c> is separate.
    /// </summary>
    public async Task<(bool Ok, string? Error)> InstallWugModuleAsync(CancellationToken token = default)
    {
        try
        {
            return await Vivre.Core.Wug.WugMaintenance.InstallModuleAsync(TimeSpan.FromMinutes(3), token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Reads the OS caption + build for one machine on demand (e.g. when its detail window opens),
    /// if not already known. Best-effort — a single quick WinRM query; leaves <see
    /// cref="Computer.OperatingSystem"/> as-is on failure.
    /// </summary>
    public async Task FetchOperatingSystemAsync(Computer computer, CancellationToken token = default)
    {
        if (!string.IsNullOrEmpty(computer.OperatingSystem))
        {
            return; // already known
        }

        const string script =
            "$o = Get-CimInstance Win32_OperatingSystem; \"$(($o.Caption -replace '^Microsoft ','').Trim()) — $($o.Version)\"";
        try
        {
            PSExecutionResult result = IsLocalHost(computer.Name)
                ? await _powerShell.RunLocalAsync(script, token)
                : await _powerShell.RunRemoteAsync(computer.Name, script, CurrentPsCredential(), cancellationToken: token);

            string? os = result.Output.Count > 0 ? result.Output[0]?.BaseObject?.ToString()?.Trim() : null;
            if (!string.IsNullOrWhiteSpace(os))
            {
                computer.OperatingSystem = os;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Best-effort — the detail window just shows "—" if the OS can't be read (offline, no WinRM).
        }
    }

    private static bool IsLocalHost(string host) => HostName.IsLocal(host);

    private static string SummarizeHealth(SccmClientInfo health)
    {
        if (health.IsHealthy)
        {
            return "Healthy";
        }

        var issues = new List<string>();
        if (health.RebootRequired)
        {
            issues.Add("reboot required");
        }

        if (health.MissingUpdates)
        {
            issues.Add("updates missing");
        }

        if (health.RunningUpdates)
        {
            issues.Add("install running");
        }

        return string.Join(", ", issues);
    }
}
