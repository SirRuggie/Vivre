using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Management.Automation;
using System.Text;
using System.Windows.Data;
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
using Vivre.Core.Vitals;
using Vivre.Core.Wug;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Vivre.Desktop.ViewModels;

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
}

/// <summary>
/// One independent workspace = one tab: its own computer list, selection, and
/// operations (which can run concurrently with other tabs). Owns the grid and the
/// Ping/Check sweeps. Remote operations use the credential from <see cref="Credentials"/>
/// (app-wide, shared across tabs). Created per tab by <see cref="ShellViewModel"/>.
/// </summary>
public partial class WorkspaceViewModel : ObservableObject, IDisposable
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
    private readonly AppSettingsStore _appSettings = new();

    /// <summary>User-defined custom columns (machine mode), loaded from settings; the view builds a grid
    /// column per entry and the CSV export appends them. Mutated via Add/Remove which persist.</summary>
    public ObservableCollection<CustomColumnSpec> CustomColumns { get; } = [];

    /// <summary>Built-in machine-grid column headers the user has hidden (Name is never hideable); the
    /// view applies these to the grid. Mutated via SetColumnHidden which persists.</summary>
    public ObservableCollection<string> HiddenColumns { get; } = [];
    // Vitals is a read-only multi-CIM pull (heavier than ping, lighter than an install): bound it
    // like the scan, and give each host a modest timeout so a hung WinRM session can't stall the sweep.
    private readonly SemaphoreSlim _vitalsThrottle;
    private const int VitalsPerHostTimeoutSeconds = 120;
    private const int SoftwarePerHostTimeoutSeconds = 60;
    // Shared across tabs so a many-machine fleet can't flood WinRM with reboot probes at once.
    private static readonly SemaphoreSlim _rebootProbeThrottle = new(8);
    // Hosts whose WinRM/PSRP shell init is failing (RemoteShellInitException — pending reboot or
    // MaxShellsPerUser). Value = the next time we'll RE-TEST it: we back off from probing every 20s
    // (hammering a degraded box makes it worse) but still retry every few minutes so we notice when
    // it recovers (a successful probe clears the flag immediately). Concurrent: probes run up to
    // _rebootProbeThrottle-wide.
    private readonly ConcurrentDictionary<string, DateTime> _degradedHosts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan DegradedRetryInterval = TimeSpan.FromMinutes(5);
    // After a host comes back online we re-probe its reboot state a few times: a just-booted box
    // transiently still reports reboot-pending, so a single probe could catch that and strand the
    // amber dot forever (once RebootRequired is true we otherwise stop probing).
    private readonly ConcurrentDictionary<string, int> _rebootRecheckBudget = new(StringComparer.OrdinalIgnoreCase);
    private const int PostBootRebootRechecks = 5;
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
    // Install/uninstall throttle (heavy SYSTEM-task operations — kept low).
    private readonly SemaphoreSlim _patchThrottle;
    // Scan throttle — much higher: a scan is a light, read-only WUA search, so a whole fleet can
    // scan near-simultaneously (BatchPatch-style) instead of in small waves.
    private readonly SemaphoreSlim _scanThrottle;
    private CancellationTokenSource? _monitorCts;
    // The grid's default view, with a live filter (name search + state). Both mode grids bind
    // Computers, so they share this view — filtering once affects whichever grid is showing.
    private readonly ICollectionView _computersView;

    /// <summary>Tab title (editable — double-click the tab header to rename).</summary>
    [ObservableProperty]
    public partial string Title { get; set; } = "New tab";

    /// <summary>Rows shown in the computer grid.</summary>
    public ObservableCollection<Computer> Computers { get; } = [];

    /// <summary>Rows the user has selected (kept in sync from the grid's selection).</summary>
    public ObservableCollection<Computer> SelectedComputers { get; } = [];

    /// <summary>"N selected" for the status bar, or empty when nothing is selected. The main
    /// guardrail for selection-scoped actions (Delete / Scan / Install selected).</summary>
    public string SelectionSummary => SelectedComputers.Count > 0 ? $"{SelectedComputers.Count} selected" : string.Empty;

    /// <summary>True when at least one row is selected (drives the status-bar selection indicator).</summary>
    public bool HasSelection => SelectedComputers.Count > 0;

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
    }

    /// <summary>True when a name filter or a non-All state filter is in effect.</summary>
    public bool IsFilterActive => ActiveFilter != RowFilter.All || !string.IsNullOrWhiteSpace(FilterText);

    /// <summary>"Showing N of M" while filtered, otherwise empty.</summary>
    public string FilterStatus =>
        IsFilterActive ? $"Showing {VisibleRowCount} of {Computers.Count}" : string.Empty;

    /// <summary>Number of rows currently shown (the filtered set) — also what an export/CSV includes.</summary>
    public int VisibleRowCount => _computersView.Cast<Computer>().Count();

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

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

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
    [NotifyPropertyChangedFor(nameof(CanInstallChecked))]
    [NotifyPropertyChangedFor(nameof(CanUninstallChecked))]
    public partial bool IsBusy { get; set; }

    /// <summary>
    /// When true the tab shows the Windows Update grid + patch command bar instead of the
    /// Machines grid (same machine list, different lane). Bound to the per-tab mode toggle.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMachineMode))]
    [NotifyPropertyChangedFor(nameof(CanShowInstallToolbar))]
    public partial bool IsUpdateMode { get; set; }

    /// <summary>Inverse of <see cref="IsUpdateMode"/> — each grid binds its own bool through one converter.</summary>
    public bool IsMachineMode => !IsUpdateMode;

    /// <summary>Flips Machines ↔ Windows Update (bound to Ctrl+M; the segmented switcher uses the property directly).</summary>
    [RelayCommand]
    private void ToggleUpdateMode() => IsUpdateMode = !IsUpdateMode;

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
    /// The main toolbar's "Install" button is shown only when the tab is in Windows Update mode
    /// <em>and</em> the scope is Applicable (uninstalling all installed updates from the toolbar
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
    public WorkspaceViewModel(IHostPinger pinger, IHostProbe hostProbe, IConfigMgrClient configMgr, IWinRmEnabler winRm, CredentialStore credentials, IComputerListStore lists, IActivityLog activity, IScriptLibrary scripts, IPatchService patch, PatchOptions patchOptions, IHostRebootProbe rebootProbe, IPowerShellHost powerShell, IVitalsProbe vitals, IRemediationService remediation, IDeploymentService deployment, ISoftwareProbe software, ICustomColumnProbe customColumns)
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
        _patchThrottle = new SemaphoreSlim(Math.Max(1, patchOptions.MaxConcurrentHosts));
        _scanThrottle = new SemaphoreSlim(Math.Max(1, patchOptions.MaxConcurrentScans));
        _vitalsThrottle = new SemaphoreSlim(Math.Max(1, patchOptions.MaxConcurrentScans));
        _rebootProbe = rebootProbe;
        _vitals = vitals;
        _remediation = remediation;
        _deployment = deployment;
        _software = software;
        _customColumns = customColumns;
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
                 nameof(Computer.UpdatesAvailable), nameof(Computer.MissingUpdates), nameof(Computer.VitalityBand)])
            {
                live.LiveFilteringProperties.Add(prop);
            }

            live.IsLiveFiltering = true;
        }

        // No seeding — the grid starts empty; the user opens a saved list or pastes one.
        IsMonitoring = true; // start watching online/offline straight away
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

        OnPropertyChanged(nameof(OnlineSummary));
        OnPropertyChanged(nameof(HasComputers));
        RaiseFleetChanged();
    }

    private void OnComputerStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Computer.IsOnline):
                OnPropertyChanged(nameof(OnlineSummary));
                break;
            // The fleet tally / band / first-run hint all key off the derived PatchState, which the
            // model raises when UpdatePhase or RebootRequired change. UpdateProgress feeds the band's
            // overall bar. (Cheap: a LINQ group over the in-memory list, only on actual state change.)
            case nameof(Computer.PatchState):
            case nameof(Computer.UpdateProgress):
            case nameof(Computer.VitalityBand):
                RaiseFleetChanged();
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
        OnPropertyChanged(nameof(FleetProgress));
        OnPropertyChanged(nameof(ShowUpdateFirstRunHint));
        // Live filtering can change the shown count as rows change state under an active filter.
        OnPropertyChanged(nameof(FilterStatus));
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
                    case PatchState.Scanning or PatchState.Downloading or PatchState.Installing: working++; break;
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
        Computers.Any(c => c.PatchState is PatchState.Scanning or PatchState.Downloading or PatchState.Installing);

    /// <summary>Overall progress (avg of the rows currently downloading/installing) for the band's bar; 0 when none.</summary>
    public int FleetProgress
    {
        get
        {
            int[] vals = [.. Computers
                .Where(c => c.PatchState is PatchState.Downloading or PatchState.Installing)
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
            c.UpdateMessage = value ? c.InstalledMessage : c.ApplicableMessage;
            c.UpdatesAvailable = value ? c.InstalledCount : c.ApplicableCount;
        }

        // The active checklist collection changed (Applicable ↔ Installed) — re-track it for the
        // Install/Uninstall enable-state.
        RetrackChecklist();
    }

    /// <summary>
    /// When the tab flips into Windows Update mode, kick an immediate monitor pass so the
    /// Pending Reboot column populates straight away instead of waiting for the next 20 s tick.
    /// </summary>
    partial void OnIsUpdateModeChanged(bool value)
    {
        // The first-run hint + fleet band are update-mode-only — refresh their visibility on the flip.
        OnPropertyChanged(nameof(ShowUpdateFirstRunHint));
        OnPropertyChanged(nameof(IsPatchOperationActive));

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

    /// <summary>PowerShell credential for remote ops, or null to use the current Windows login.</summary>
    private PSCredential? CurrentPsCredential() => _credentials.Current?.ToPowerShellCredential();

    /// <summary>Replaces the grid with a fresh set of machines (from the loader or a saved list).</summary>
    public void SetComputers(IEnumerable<string> names)
    {
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
        foreach (string name in names.Select(n => n.Trim()).Where(n => n.Length > 0))
        {
            if (existing.Add(name))
            {
                var computer = new Computer(name) { LastStatus = "Not checked" };
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
            _ = RunSweepAsync(added, CheckHealthAndVitalsRowAsync);
            // Also fill any saved custom columns — their values are runtime-only, so the restored columns
            // would sit blank after a launch/reload without this (no-op when there are no custom columns).
            _ = RunCustomColumnsSelectedAsync(added);
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
        catch
        {
            return true;
        }
    }

    // --- Named machine lists (File menu) ---

    /// <summary>Names of the saved machine lists.</summary>
    public IReadOnlyList<string> SavedLists() => _lists.List();

    /// <summary>Loads a saved list into the grid.</summary>
    public void OpenList(string name) => SetComputers(_lists.Load(name));

    /// <summary>Saves the current grid as a named list.</summary>
    public void SaveCurrentAsList(string name) => _lists.Save(name, Computers.Select(c => c.Name));

    /// <summary>Deletes a saved list.</summary>
    public void DeleteList(string name) => _lists.Delete(name);

    private bool CanStartSweep() => !IsBusy;

    private bool CanStop() => IsBusy || IsMonitoring;

    /// <summary>Pings every row (reachability only — no SCCM health).</summary>
    [RelayCommand(CanExecute = nameof(CanStartSweep))]
    private Task PingAllAsync() => RunSweepAsync([.. Computers], PingRowAsync);

    /// <summary>Re-pings the rows not currently online (offline or never checked).</summary>
    [RelayCommand(CanExecute = nameof(CanStartSweep))]
    private Task PingOfflineAsync() => RunSweepAsync([.. Computers.Where(c => c.IsOnline != true)], PingRowAsync);

    /// <summary>Pings and pulls SCCM client health for every row (health is attempted even if ping fails).</summary>
    [RelayCommand(CanExecute = nameof(CanStartSweep))]
    private Task CheckAllAsync() => RunSweepAsync([.. Computers], CheckRowAsync);

    /// <summary>The single "Check Vitals" button: SCCM client health AND deep OS vitals (disk / memory /
    /// CPU / uptime / stopped services / recent errors, scored 0-100) for every row — one click does
    /// both. Read-only, no confirm. Vitals are gated by <see cref="_vitalsThrottle"/> inside
    /// <see cref="CheckVitalsRowAsync"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanStartSweep))]
    private Task CheckVitalsAsync() => RunSweepAsync([.. Computers], CheckHealthAndVitalsRowAsync);

    /// <summary>Reads vitals for just the given rows (right-click ▸ Triage on a single row); empty ⇒ all.</summary>
    public Task CheckVitalsSelectedAsync(IReadOnlyList<Computer> rows) =>
        RunSweepAsync(rows.Count > 0 ? rows : [.. Computers], CheckVitalsRowAsync);

    /// <summary>Per-row work for the combined "Check Vitals" sweep: SCCM client health, then OS vitals.</summary>
    private async Task CheckHealthAndVitalsRowAsync(Computer computer, CancellationToken token)
    {
        await CheckRowAsync(computer, token);
        await CheckVitalsRowAsync(computer, token);
    }

    /// <summary>Builds the per-machine Details view-model, wired for triage: the remediation service,
    /// the session credential, and a refresh that re-checks just this machine's vitals after an action
    /// (so the score/readings update in place once a service is started, space freed, or a process ended).</summary>
    public ComputerDetailViewModel CreateDetailViewModel(Computer computer) =>
        new(computer, _activity, _remediation, CurrentPsCredential, () => CheckVitalsSelectedAsync([computer]));

    // --- Software check (ad-hoc "is product X installed?" → the Software column) ---

    /// <summary>Checks the given rows for an installed product whose name contains <paramref name="query"/>
    /// (right-click ▸ Check software…); empty ⇒ all rows. When <paramref name="serviceName"/> is given,
    /// also reports whether the matching service is running. Fills each row's Software column with the
    /// match + version (+ service state). Read-only; no confirm.</summary>
    public Task CheckSoftwareSelectedAsync(IReadOnlyList<Computer> rows, string query, string? serviceName = null) =>
        RunSweepAsync(rows.Count > 0 ? rows : [.. Computers], (c, ct) => CheckSoftwareRowAsync(c, query, serviceName, ct));

    // Per-row software check: a light, read-only registry (+ optional service) query, bounded by the
    // scan throttle with a per-host timeout so a hung box can't stall the sweep.
    private async Task CheckSoftwareRowAsync(Computer computer, string query, string? serviceName, CancellationToken token)
    {
        await _scanThrottle.WaitAsync(token);
        try
        {
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
            _scanThrottle.Release();
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
    /// values. One combined call per host (not per column). Read-only; no confirm.</summary>
    public Task RunCustomColumnsSelectedAsync(IReadOnlyList<Computer> rows)
    {
        if (CustomColumns.Count == 0)
        {
            return Task.CompletedTask;
        }

        IReadOnlyList<CustomColumnSpec> specs = [.. CustomColumns];
        return RunSweepAsync(rows.Count > 0 ? rows : [.. Computers], (c, ct) => RunCustomColumnRowAsync(c, specs, ct));
    }

    /// <summary>Runs a single custom column's script across the rows (empty ⇒ all) — used when a column is
    /// added, so only the new column fills and the others' already-fetched values aren't re-run.</summary>
    public Task RunCustomColumnAsync(IReadOnlyList<Computer> rows, CustomColumnSpec spec) =>
        RunSweepAsync(rows.Count > 0 ? rows : [.. Computers], (c, ct) => RunCustomColumnRowAsync(c, [spec], ct));

    // Per-row: one combined custom-column call, bounded by the scan throttle with a per-host timeout.
    private async Task RunCustomColumnRowAsync(Computer computer, IReadOnlyList<CustomColumnSpec> specs, CancellationToken token)
    {
        await _scanThrottle.WaitAsync(token);
        try
        {
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
        finally
        {
            _scanThrottle.Release();
        }
    }

    // --- Windows Update lane (scan / install) ---

    /// <summary>Scans every row for applicable updates from the selected source.</summary>
    [RelayCommand(CanExecute = nameof(CanStartSweep))]
    private Task ScanUpdatesAsync() => RunPatchSweepAsync([.. Computers], ScanRowAsync, "Scan", _scanThrottle);

    /// <summary>Downloads + installs applicable updates on every row (via a one-time SYSTEM task per host).</summary>
    [RelayCommand(CanExecute = nameof(CanStartSweep))]
    private Task InstallUpdatesAsync() => RunPatchSweepAsync([.. Computers], (c, ct) => InstallRowAsync(c, ct), "Install");

    /// <summary>Scans the given rows (right-click "Updates ▸ Scan"); empty ⇒ all rows.</summary>
    public Task ScanSelectedAsync(IReadOnlyList<Computer> rows) =>
        RunPatchSweepAsync(rows.Count > 0 ? rows : [.. Computers], ScanRowAsync, "Scan", _scanThrottle);

    /// <summary>Installs on the given rows (right-click "Updates ▸ Install"); empty ⇒ all rows.</summary>
    public Task InstallSelectedAsync(IReadOnlyList<Computer> rows) =>
        RunPatchSweepAsync(rows.Count > 0 ? rows : [.. Computers], (c, ct) => InstallRowAsync(c, ct), "Install");

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
        // Bounded by the conservative patch throttle (a payload copy is heavy on the wire). No
        // operationLabel: staging tracks Command result, not PatchState, so the patch-style completion
        // summary doesn't apply — per-row results + the activity log carry the outcome.
        RunPatchSweepAsync(
            rows.Count > 0 ? rows : [.. Computers],
            (c, ct) => StageRowAsync(c, sourcePath, sourceIsFolder, targetPath, packageName, ct),
            operationLabel: null,
            _patchThrottle);

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
        string script = $$"""
            $trigger   = New-ScheduledTaskTrigger -Once -At '{{at:s}}'
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
                    computer.LastStatus = $"Reboot scheduled for {at:g}";
                    _scheduledTasks[computer.Name] = at;
                    _activity.Info(computer.Name, $"Reboot scheduled for {at:g}");
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
        const string script = "Get-ScheduledTask -TaskName 'Vivre_*' -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false; 'OK'";
        foreach (Computer computer in (rows.Count > 0 ? rows : [.. SelectedComputers]).ToList())
        {
            computer.LastError = null;
            computer.LastStatus = "Cancelling scheduled task…";
            try
            {
                PSExecutionResult result = IsLocalHost(computer.Name)
                    ? await _powerShell.RunLocalAsync(script, token)
                    : await _powerShell.RunRemoteAsync(computer.Name, script, CurrentPsCredential(), cancellationToken: token);

                // Clear the columns regardless — there's nothing pending from our side now.
                computer.ScheduledAction = null;
                computer.ScheduledNextRun = null;
                _scheduledTasks.TryRemove(computer.Name, out _);
                computer.LastStatus = result.HadErrors ? "Cancel had errors" : "Scheduled task cancelled";
                _activity.Info(computer.Name, "Cancelled pending scheduled task(s).");
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
    [RelayCommand(CanExecute = nameof(CanScanFocused))]
    private Task ScanFocusedAsync() =>
        FocusedComputer is { } c ? RunPatchSweepAsync([c], ScanRowAsync, "Scan", _scanThrottle) : Task.CompletedTask;

    private bool CanScanFocused() => !IsBusy && FocusedComputer is not null;

    /// <summary>Installs only the ticked updates on the focused machine (the side panel's "Install checked").</summary>
    [RelayCommand(CanExecute = nameof(CanInstallChecked))]
    private Task InstallCheckedAsync() =>
        FocusedComputer is { } c ? RunPatchSweepAsync([c], (c, ct) => InstallRowAsync(c, ct), "Install") : Task.CompletedTask;

    /// <summary>Whether "Install checked" can run: Applicable scope, a focused machine with a scanned
    /// checklist, not busy, not patching, and NOT with a reboot pending (a pending reboot means
    /// just-installed updates still read as applicable and installing more would just be deferred by
    /// the agent's boot-busy guard — reboot first). Re-evaluated as boxes toggle / reboot state flips.</summary>
    public bool CanInstallChecked =>
        !IsBusy && !IsInstalledMode && FocusedComputer is { } c && c.ApplicableUpdates.Count > 0
        && !c.IsPatching && c.RebootRequired != true;

    /// <summary>
    /// Uninstalls only the ticked updates on the focused machine. Only enabled in Installed scope
    /// and when at least one ticked update is actually uninstallable. The confirmation dialog lives
    /// in the view's code-behind so the VM doesn't pop UI directly.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUninstallChecked))]
    public Task UninstallCheckedAsync() =>
        FocusedComputer is { } c ? RunPatchSweepAsync([c], UninstallRowAsync, "Uninstall") : Task.CompletedTask;

    /// <summary>Whether "Uninstall checked" can run: Installed scope, not busy, and at least one
    /// ticked update that's actually removable. Bound by the button's enable-state (the button uses
    /// a Click handler for its confirm dialog) and re-evaluated live as boxes toggle, so it's
    /// disabled before a scan and whenever nothing removable is ticked.</summary>
    public bool CanUninstallChecked =>
        !IsBusy && IsInstalledMode && FocusedComputer is { } c && !c.IsPatching
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
            // can remove it, so ticking it is meaningless). "None" always clears.
            update.IsSelected = selected && (!installed || update.IsUninstallable);
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
    }

    private void OnChecklistItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectableUpdate.IsSelected))
        {
            RefreshChecklistCommandState();
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
    }

    /// <summary>Registers a new running operation: tracks its CTS (so Stop cancels it) and flips the
    /// busy state on. IsBusy stays on until the last concurrent operation ends.</summary>
    private CancellationTokenSource BeginOperation()
    {
        var cts = new CancellationTokenSource();
        _activeCts.Add(cts);
        IsBusy = true;
        return cts;
    }

    /// <summary>Retires an operation; clears busy only when none remain.</summary>
    private void EndOperation(CancellationTokenSource cts)
    {
        _activeCts.Remove(cts);
        cts.Dispose();
        if (_activeCts.Count == 0)
        {
            IsBusy = false;
        }
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

    private async Task RunSweepAsync(IReadOnlyList<Computer> rows, Func<Computer, CancellationToken, Task> operation)
    {
        CancellationTokenSource cts = BeginOperation();
        try
        {
            Task work = Task.WhenAll(rows.Select(row => operation(row, cts.Token)));

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
            EndOperation(cts);
        }
    }

    /// <summary>
    /// Like <see cref="RunSweepAsync"/> (shares Stop / IsBusy / the cancellation race) but
    /// bounded — installs are heavy, so each host runs under a <see cref="SemaphoreSlim"/>
    /// throttle and a per-host timeout so one stuck box never holds up the grid.
    /// </summary>
    /// <summary>Raised once when a Scan/Install/Uninstall sweep finishes (not on cancel) with a
    /// human summary — the shell shows it as a tray balloon (suppressed when the window is focused).</summary>
    public event Action<string>? OperationCompleted;

    private async Task RunPatchSweepAsync(
        IReadOnlyList<Computer> rows,
        Func<Computer, CancellationToken, Task> operation,
        string? operationLabel = null,
        SemaphoreSlim? throttle = null)
    {
        // Scans pass the high scan throttle; install/uninstall default to the conservative one.
        throttle ??= _patchThrottle;

        CancellationTokenSource cts = BeginOperation();
        bool cancelled = false;
        try
        {
            Task work = Task.WhenAll(rows.Select(row => RunOnePatchHostAsync(row, operation, throttle, cts.Token)));

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
            EndOperation(cts);
        }

        // One completion signal per sweep (not per row), only for a real finish.
        if (!cancelled && operationLabel is not null)
        {
            OperationCompleted?.Invoke(BuildCompletionSummary(operationLabel, rows));
        }
    }

    private static string BuildCompletionSummary(string label, IReadOnlyList<Computer> rows)
    {
        if (string.Equals(label, "Schedule", StringComparison.Ordinal))
        {
            int scheduled = rows.Count(r => r.ScheduledNextRun is not null);
            int errs = rows.Count(r => r.PatchState == PatchState.Error);
            string s = $"Scheduled {scheduled} machine(s)";
            if (errs > 0) s += $", {errs} failed";
            return s;
        }

        if (string.Equals(label, "Scan", StringComparison.Ordinal))
        {
            // A scan leaves EVERY machine in PatchState.Available (that's just "scan done") — so
            // count the ones that actually found updates by their update count, not the state.
            int avail = rows.Count(r => r.UpdatesAvailable > 0);
            int errs = rows.Count(r => r.PatchState == PatchState.Error);
            string s = $"Scan finished — {rows.Count} machine(s)";
            if (avail > 0) s += $", {avail} with updates";
            if (errs > 0) s += $", {errs} failed";
            return s;
        }

        // Install / Uninstall.
        int done = rows.Count(r => r.PatchState == PatchState.Done);
        int reboot = rows.Count(r => r.PatchState == PatchState.RebootPending);
        int failed = rows.Count(r => r.PatchState == PatchState.Error);
        var parts = new List<string> { $"{done} succeeded" };
        if (reboot > 0) parts.Add($"{reboot} need reboot");
        if (failed > 0) parts.Add($"{failed} failed");
        return $"{label} finished — " + string.Join(", ", parts);
    }

    private async Task RunOnePatchHostAsync(
        Computer row,
        Func<Computer, CancellationToken, Task> operation,
        SemaphoreSlim throttle,
        CancellationToken token)
    {
        await throttle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var perHost = CancellationTokenSource.CreateLinkedTokenSource(token);
            perHost.CancelAfter(_patchOptions.PerHostTimeout);
            try
            {
                await operation(row, perHost.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (perHost.IsCancellationRequested && !token.IsCancellationRequested)
            {
                // Per-host timeout (not a user Stop): surface it and let the rest of the sweep continue.
                row.UpdateError = $"Timed out after {_patchOptions.PerHostTimeout.TotalHours:N0}h";
                row.UpdateMessage = "Timed out";
                row.UpdatePhase = PatchPhase.Error.ToString();
                _activity.Error(row.Name, row.UpdateError);
            }
        }
        finally
        {
            throttle.Release();
        }
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

        computer.UpdateError = null;
        computer.UpdateProgress = null;
        computer.UpdatePhase = PatchPhase.Scanning.ToString();
        computer.UpdateMessage = "Scanning…";
        // Capture the scope at scan start so a mid-scan scope toggle doesn't route the result into
        // the wrong per-scope cache.
        UpdateScope scopeAtScan = _patchOptions.Scope;
        try
        {
            HostPatchStatus status = await _patch.ScanAsync(computer.Name, _patchOptions, _credentials.Current, token);
            ApplyStatus(computer, status, scopeAtScan);
        }
        catch (OperationCanceledException)
        {
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

        computer.IsPatching = true;
        computer.UpdateError = null;
        computer.UpdateProgress = 0;
        computer.UpdatePhase = PatchPhase.Scanning.ToString();
        computer.UpdateMessage = "Starting uninstall…";

        var progress = new Progress<HostPatchStatus>(s => ApplyStatus(computer, s));
        try
        {
            HostPatchStatus final = await _patch.UninstallAsync(computer.Name, options, _credentials.Current, progress, token);
            ApplyStatus(computer, final);
        }
        catch (OperationCanceledException)
        {
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
            computer.IsPatching = false;
        }
    }

    private async Task InstallRowAsync(Computer computer, CancellationToken token, DateTime? scheduleAt = null)
    {
        // Don't start a second operation on a row already installing/uninstalling.
        if (computer.IsPatching)
        {
            return;
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

            string[] selectedKbs = [.. computer.ApplicableUpdates
                .Where(u => u.IsSelected && !string.IsNullOrWhiteSpace(u.Kb))
                .Select(u => u.Kb!)];
            if (selectedKbs.Length == 0)
            {
                computer.UpdateError = null;
                computer.UpdateProgress = null;
                computer.UpdatePhase = PatchPhase.Idle.ToString();
                computer.UpdateMessage = "Selected updates have no KB id to target";
                return;
            }

            options.IncludeKbArticleIds = selectedKbs;
        }

        computer.IsPatching = true;
        computer.UpdateError = null;
        computer.UpdateProgress = 0;
        computer.UpdatePhase = PatchPhase.Scanning.ToString();
        computer.UpdateMessage = scheduleAt is null ? "Starting…" : "Scheduling…";

        // Progress<T> marshals callbacks to the captured (UI) context; WPF also auto-marshals
        // the scalar property updates, so the grid stays current as the SYSTEM task reports in.
        var progress = new Progress<HostPatchStatus>(s => ApplyStatus(computer, s));
        try
        {
            HostPatchStatus final = await _patch.InstallAsync(computer.Name, options, _credentials.Current, progress, token);
            ApplyStatus(computer, final);

            // Scheduled (not run now): surface it on the Scheduled-task columns + a clear message.
            if (scheduleAt is { } when && final.Phase != PatchPhase.Error)
            {
                computer.ScheduledAction = "Install updates";
                computer.ScheduledNextRun = when;
                computer.UpdateMessage = $"Scheduled for {when:g}";
                _scheduledTasks[computer.Name] = when;
            }
        }
        catch (OperationCanceledException)
        {
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
            computer.IsPatching = false;
        }
    }

    /// <summary>Writes a <see cref="HostPatchStatus"/> snapshot onto a row, logging phase transitions only.
    /// <paramref name="scopeForScan"/> is set by <see cref="ScanRowAsync"/> so the scan result lands in the
    /// right per-scope cache on <see cref="Computer"/>; null (the Progress&lt;T&gt; callback path) uses the
    /// shared options' current scope, which only matters for the Phase.Available branch.</summary>
    private void ApplyStatus(Computer computer, HostPatchStatus status, UpdateScope? scopeForScan = null)
    {
        string phase = status.Phase.ToString();
        bool phaseChanged = !string.Equals(computer.UpdatePhase, phase, StringComparison.Ordinal);

        computer.UpdatePhase = phase;
        computer.UpdateMessage = status.Message;
        computer.UpdateProgress = status.Percent;

        if (status.Phase == PatchPhase.Available)
        {
            UpdateScope scope = scopeForScan ?? _patchOptions.Scope;
            computer.UpdatesAvailable = status.AvailableCount;
            ReplaceUpdatesForScope(computer, scope, status.Updates);
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
            }
        }

        if (status.RebootPending)
        {
            computer.RebootRequired = true;
        }

        if (status.Phase == PatchPhase.Error)
        {
            computer.UpdateError = status.Message;
        }

        if (phaseChanged && status.Phase != PatchPhase.Idle)
        {
            if (status.Phase == PatchPhase.Error)
            {
                _activity.Error(computer.Name, status.Message);
            }
            else
            {
                _activity.Info(computer.Name, $"{phase}: {status.Message}");
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
        (bool online, string? error) = await ProbeReachabilityAsync(computer, token);

        computer.IsOnline = online;
        computer.LastError = online ? null : error;

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
        // of runspaces at once. But DON'T re-probe a box every 20s once it's known reboot-pending:
        // the markers are sticky until it actually reboots, so re-probing only churns fresh WinRM
        // shells (which can poison a degraded target). Probe when state is unknown/clear, or during
        // the brief post-boot recheck window. A degraded host is backed off (re-tested only every
        // DegradedRetryInterval) but DOES get retried so we notice it recovered.
        bool degraded = _degradedHosts.TryGetValue(computer.Name, out DateTime retryAt);
        bool backoffActive = degraded && DateTime.Now < retryAt;
        bool degradedRetryDue = degraded && !backoffActive;
        if (online && IsUpdateMode && !backoffActive)
        {
            bool recheck = _rebootRecheckBudget.TryGetValue(computer.Name, out int budget) && budget > 0;
            // A degraded-retry probes regardless of the reboot-pending skip — its job is to re-test WinRM.
            if (computer.RebootRequired != true || recheck || degradedRetryDue)
            {
                // No ConfigureAwait(false): this method mutates data-bound Computer state after the
                // await (and below), so keep the continuation on the captured UI context.
                await ProbeRebootPendingAsync(computer, token);

                if (recheck)
                {
                    // Stop the post-boot rechecks once we get a clean (not-pending) read; otherwise
                    // spend down the budget so a stuck-pending box doesn't get probed forever.
                    if (computer.RebootRequired == false)
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

        if (previous == online)
        {
            return; // unchanged — leave LastStatus as-is
        }

        computer.LastStatus = online ? "Online" : "Offline";
        if (online)
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
            // return trip can be timed. (First-ever-offline, previous null, isn't a reboot — skip.)
            if (previous == true)
            {
                computer.WentOfflineAt = DateTime.Now;
                computer.RebootMessage = $"Offline since {DateTime.Now:HH:mm} — waiting for it to come back…";
            }

            _activity.Warn(computer.Name, previous is null ? $"Offline — {error}" : $"Went offline — {error}");
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
        try
        {
            bool? was = computer.RebootRequired;
            bool? pending = await _rebootProbe.IsRebootPendingAsync(computer.Name, CurrentPsCredential(), token);

            // We got here with no shell-init failure → WinRM is healthy. If this host was flagged
            // degraded, it has recovered: clear the flag + the stale "WinRM unhealthy" message so the
            // user sees it's working again (this is the path that self-heals after a real reboot).
            if (_degradedHosts.TryRemove(computer.Name, out _))
            {
                if (computer.RebootMessage is { } stale && stale.StartsWith("WinRM unhealthy", StringComparison.Ordinal))
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
                // now-stale ", reboot required" the install left on the update message.
                if (was == true && !pending.Value)
                {
                    computer.RebootMessage = $"Reboot complete — back online {DateTime.Now:HH:mm}";
                    computer.WentOfflineAt = null;

                    const string suffix = ", reboot required";
                    if (computer.UpdateMessage is { } msg && msg.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        computer.UpdateMessage = msg[..^suffix.Length];
                    }

                    _activity.Info(computer.Name, "Reboot complete — back online, no reboot pending");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
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
            _degradedHosts[computer.Name] = DateTime.Now + DegradedRetryInterval;
            if (firstTime)
            {
                if (ex is RemoteShellInitException)
                {
                    computer.RebootMessage = $"WinRM unhealthy on {computer.Name} — likely reboot-pending; reboot the target to clear it.";
                }

                _activity.Warn(computer.Name, $"Reboot probe failing — backing off (retry every {DegradedRetryInterval.TotalMinutes:N0} min). {ex.Message}");
            }
        }
        finally
        {
            _rebootProbeThrottle.Release();
        }
    }

    private async Task CheckRowAsync(Computer computer, CancellationToken token)
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

        // Always attempt health, regardless of ping — many servers block ICMP but
        // answer WinRM. "Online" means it responded (ICMP or WinRM), using the
        // active credential.
        try
        {
            SccmClientInfo health = await _configMgr.GetClientHealthAsync(computer.Name, CurrentPsCredential(), token);
            computer.IsOnline = true;
            computer.SiteCode = health.SiteCode;
            computer.AgentVersion = health.ClientVersion;
            computer.RebootRequired = health.RebootRequired;
            computer.MissingUpdates = health.MissingUpdates;
            computer.RunningUpdates = health.RunningUpdates;
            computer.UserLoggedOn = health.UserLoggedOn;
            computer.LastBootTime = health.LastBootTime;
            computer.LastStatus = SummarizeHealth(health);
            _activity.Info(computer.Name, $"Health: {computer.LastStatus}");
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
            computer.LastStatus = "Online · no ConfigMgr client";
            computer.LastError = ex.Message;
            _activity.Warn(computer.Name, $"No ConfigMgr client — {ex.Message}");
        }
        catch (Exception ex)
        {
            // Couldn't reach it over WinRM — fall back to the ping result.
            computer.IsOnline = pingOnline;
            computer.LastStatus = pingOnline ? "Online · health unavailable" : "Offline";
            computer.LastError = ex.Message;
            _activity.Warn(computer.Name, $"Check failed — {ex.Message}");
        }
    }

    // Reads deep OS vitals for one row and scores it. Mirrors CheckRowAsync's clear→pull→catch-ladder
    // shape; stays on the UI context (no ConfigureAwait(false)) so the row-property writes that drive
    // the grid are marshalled correctly. Concurrency is bounded by _vitalsThrottle (acquired here so a
    // plain RunSweepAsync still can't flood WinRM), and a per-host timeout stops a hung box stalling it.
    private async Task CheckVitalsRowAsync(Computer computer, CancellationToken token)
    {
        await _vitalsThrottle.WaitAsync(token);
        try
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
        finally
        {
            _vitalsThrottle.Release();
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
        computer.RecentErrorEventCount = null;
        computer.Vitals = null;
        computer.VitalityReasons = [];
    }

    /// <summary>Copies a vitals snapshot onto the row and runs the scorer (the one source of truth).</summary>
    private void ApplyVitals(Computer computer, MachineVitals v)
    {
        computer.IsOnline = true; // it answered the remoting pull
        computer.SystemDriveFreePercent = v.SystemDriveFreePercent;
        computer.MemoryUsedPercent = v.MemoryUsedPercent;
        computer.CpuLoadPercent = v.CpuLoadPercent;
        computer.StoppedAutoServiceCount = v.StoppedAutoServiceCount;
        computer.RecentErrorEventCount = v.RecentErrorEventCount;
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

        computer.Vitals = v;
        computer.VitalsCheckedAt = DateTime.Now;

        VitalityResult r = VitalityScorer.Score(v, computer.IsOnline);
        computer.VitalityScore = r.Score;
        computer.VitalityBand = r.Band;
        computer.VitalityReasons = r.Reasons;
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
    }

    // --- selection-aware toolbar (Scan/Install act on the selection, or all rows when none) ------

    /// <summary>"Scan selected (N)" when rows are selected, else "Scan all".</summary>
    public string ScanButtonLabel =>
        SelectedComputers.Count > 0 ? $"Scan selected ({SelectedComputers.Count})" : "Scan all";

    /// <summary>"Install selected (N)" when rows are selected, else "Install all".</summary>
    public string InstallButtonLabel =>
        SelectedComputers.Count > 0 ? $"Install selected ({SelectedComputers.Count})" : "Install all";

    /// <summary>Toolbar Scan: scoped to the selection, or every row when nothing's selected.</summary>
    [RelayCommand(CanExecute = nameof(CanStartSweep))]
    private Task ScanTarget() =>
        SelectedComputers.Count > 0 ? ScanSelectedAsync([.. SelectedComputers]) : ScanUpdatesAsync();

    /// <summary>Toolbar Install: scoped to the selection, or every row when nothing's selected.</summary>
    [RelayCommand(CanExecute = nameof(CanStartSweep))]
    private Task InstallTarget() =>
        SelectedComputers.Count > 0 ? InstallSelectedAsync([.. SelectedComputers]) : InstallUpdatesAsync();

    /// <summary>
    /// Fires <paramref name="action"/> against every selected online row. Source-generated
    /// as <c>TriggerScheduleCommand</c>; bound to each right-click menu item with the
    /// action as the command parameter.
    /// </summary>
    [RelayCommand]
    private async Task TriggerScheduleAsync(ScheduleAction? action, CancellationToken token)
    {
        if (action is null)
        {
            return;
        }

        foreach (Computer computer in SelectedComputers.ToList())
        {
            computer.LastError = null;
            computer.LastStatus = $"{action.Label}…";
            try
            {
                computer.LastStatus = await _configMgr.TriggerScheduleAsync(computer.Name, action, CurrentPsCredential(), token);
                _activity.Info(computer.Name, computer.LastStatus);
            }
            catch (SccmQueryException ex)
            {
                computer.LastStatus = "Action failed";
                computer.LastError = ex.Message;
                _activity.Error(computer.Name, $"{action.Label} failed — {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                computer.LastStatus = "Cancelled";
                throw;
            }
        }
    }

    /// <summary>
    /// Enables WinRM/PSRemoting (over DCOM) on the selected machines. Invoked from the
    /// context menu after the user confirms. Runs regardless of ping state — DCOM is a
    /// different channel, and these are exactly the machines WinRM can't reach yet.
    /// </summary>
    public async Task EnableWinRmSelectedAsync(CancellationToken token = default)
    {
        foreach (Computer computer in SelectedComputers.ToList())
        {
            computer.LastError = null;
            computer.LastStatus = "Enabling WinRM…";
            try
            {
                computer.LastStatus = await _winRm.EnableAsync(computer.Name, _credentials.Current, token);
                _activity.Info(computer.Name, computer.LastStatus);
            }
            catch (OperationCanceledException)
            {
                computer.LastStatus = "Cancelled";
                throw;
            }
            catch (Exception ex)
            {
                computer.LastStatus = "Enable WinRM failed";
                computer.LastError = ex.Message;
                _activity.Error(computer.Name, $"Enable WinRM failed — {ex.Message}");
            }
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
                    computer.RebootMessage = $"Forced reboot sent {DateTime.Now:HH:mm}";
                    _activity.Info(computer.Name, "Forced reboot (shutdown /r /f /t 5)");
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
