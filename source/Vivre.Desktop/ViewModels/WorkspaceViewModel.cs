using System.Collections.ObjectModel;
using System.Management.Automation;
using Vivre.Core.Computers;
using Vivre.Core.Credentials;
using Vivre.Core.Logging;
using Vivre.Core.Models;
using Vivre.Core.Net;
using Vivre.Core.PowerShell;
using Vivre.Core.Remoting;
using Vivre.Core.Sccm;
using Vivre.Core.Scripts;
using Vivre.Core.Updates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Vivre.Desktop.ViewModels;

/// <summary>
/// One independent workspace = one tab: its own computer list, selection, and
/// operations (which can run concurrently with other tabs). Owns the grid and the
/// Ping/Check sweeps. Remote operations use the credential from <see cref="Credentials"/>
/// (app-wide, shared across tabs). Created per tab by <see cref="ShellViewModel"/>.
/// </summary>
public partial class WorkspaceViewModel : ObservableObject
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
    // Shared across tabs so a many-machine fleet can't flood WinRM with reboot probes at once.
    private static readonly SemaphoreSlim _rebootProbeThrottle = new(8);
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _monitorCts;

    /// <summary>Tab title (editable — double-click the tab header to rename).</summary>
    [ObservableProperty]
    public partial string Title { get; set; } = "New tab";

    /// <summary>Rows shown in the computer grid.</summary>
    public ObservableCollection<Computer> Computers { get; } = [];

    /// <summary>Rows the user has selected (kept in sync from the grid's selection).</summary>
    public ObservableCollection<Computer> SelectedComputers { get; } = [];

    /// <summary>ConfigMgr client actions shown in the grid's right-click menu.</summary>
    public IReadOnlyList<ScheduleAction> ClientActions => Core.Sccm.ClientActions.All;

    /// <summary>True while a sweep (Ping All / Check All) is running — drives the busy indicator and button enable-state.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PingAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(PingOfflineCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanUpdatesCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdatesCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallCheckedCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallCheckedCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
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

    /// <summary>
    /// Side-panel scope toggle: false = Applicable (default; the install flow), true = Installed
    /// (the uninstall flow — the checklist shows installed updates with checkboxes, non-uninstallable
    /// rows greyed). Per-tab; drives <see cref="PatchOptions.Scope"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsApplicableMode))]
    [NotifyPropertyChangedFor(nameof(CanShowInstallToolbar))]
    [NotifyCanExecuteChangedFor(nameof(InstallCheckedCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallCheckedCommand))]
    public partial bool IsInstalledMode { get; set; }

    /// <summary>Inverse of <see cref="IsInstalledMode"/> — bound by the "Applicable" radio in the side panel.</summary>
    public bool IsApplicableMode => !IsInstalledMode;

    /// <summary>
    /// The main toolbar's "Install" button is shown only when the tab is in Windows Update mode
    /// <em>and</em> the scope is Applicable (uninstalling all installed updates from the toolbar
    /// would be too destructive a default — uninstall is per-machine from the side panel only).
    /// </summary>
    public bool CanShowInstallToolbar => IsUpdateMode && !IsInstalledMode;

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
    public partial Computer? FocusedComputer { get; set; }

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
    public WorkspaceViewModel(IHostPinger pinger, IHostProbe hostProbe, IConfigMgrClient configMgr, IWinRmEnabler winRm, CredentialStore credentials, IComputerListStore lists, IActivityLog activity, IScriptLibrary scripts, IPatchService patch, PatchOptions patchOptions, IHostRebootProbe rebootProbe)
    {
        _pinger = pinger;
        _hostProbe = hostProbe;
        _configMgr = configMgr;
        _winRm = winRm;
        _credentials = credentials;
        _lists = lists;
        _activity = activity;
        _scripts = scripts;
        _patch = patch;
        _patchOptions = patchOptions;
        _rebootProbe = rebootProbe;
        SelectedSource = patchOptions.Source;
        ExcludeText = string.Join(", ", patchOptions.ExcludeNameContains);
        IncludeDrivers = patchOptions.IncludeDrivers;
        IsInstalledMode = patchOptions.Scope == UpdateScope.Installed;
        // No seeding — the grid starts empty; the user opens a saved list or pastes one.
        IsMonitoring = true; // start watching online/offline straight away
    }

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
    /// Push the side-panel scope toggle into the shared patch options and clear any stale
    /// scanned-update lists across all rows so the user re-scans into the new scope.
    /// </summary>
    partial void OnIsInstalledModeChanged(bool value)
    {
        _patchOptions.Scope = value ? UpdateScope.Installed : UpdateScope.Applicable;

        foreach (Computer c in Computers)
        {
            c.ScannedUpdates.Clear();
            c.UpdatesAvailable = null;
            c.UpdateMessage = null;
        }
    }

    /// <summary>
    /// When the tab flips into Windows Update mode, kick an immediate monitor pass so the
    /// Pending Reboot column populates straight away instead of waiting for the next 20 s tick.
    /// </summary>
    partial void OnIsUpdateModeChanged(bool value)
    {
        if (value && IsMonitoring && _monitorCts is { } cts && Computers.Count > 0)
        {
            _ = MonitorRowsAsync([.. Computers], cts.Token);
        }
    }

    /// <summary>Starts/stops the continuous monitor loop when <see cref="IsMonitoring"/> flips.</summary>
    partial void OnIsMonitoringChanged(bool value)
    {
        _monitorCts?.Cancel();
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

        // Check newly-added rows right away rather than waiting for the next monitor tick.
        if (added.Count > 0 && IsMonitoring && _monitorCts is { } cts)
        {
            _ = MonitorRowsAsync(added, cts.Token);
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

    // --- Windows Update lane (scan / install) ---

    /// <summary>Scans every row for applicable updates from the selected source.</summary>
    [RelayCommand(CanExecute = nameof(CanStartSweep))]
    private Task ScanUpdatesAsync() => RunPatchSweepAsync([.. Computers], ScanRowAsync);

    /// <summary>Downloads + installs applicable updates on every row (via a one-time SYSTEM task per host).</summary>
    [RelayCommand(CanExecute = nameof(CanStartSweep))]
    private Task InstallUpdatesAsync() => RunPatchSweepAsync([.. Computers], InstallRowAsync);

    /// <summary>Scans the given rows (right-click "Updates ▸ Scan"); empty ⇒ all rows.</summary>
    public Task ScanSelectedAsync(IReadOnlyList<Computer> rows) =>
        RunPatchSweepAsync(rows.Count > 0 ? rows : [.. Computers], ScanRowAsync);

    /// <summary>Installs on the given rows (right-click "Updates ▸ Install"); empty ⇒ all rows.</summary>
    public Task InstallSelectedAsync(IReadOnlyList<Computer> rows) =>
        RunPatchSweepAsync(rows.Count > 0 ? rows : [.. Computers], InstallRowAsync);

    /// <summary>Installs only the ticked updates on the focused machine (the side panel's "Install checked").</summary>
    [RelayCommand(CanExecute = nameof(CanInstallChecked))]
    private Task InstallCheckedAsync() =>
        FocusedComputer is { } c ? RunPatchSweepAsync([c], InstallRowAsync) : Task.CompletedTask;

    private bool CanInstallChecked() =>
        !IsBusy && !IsInstalledMode && FocusedComputer is { } c && c.ScannedUpdates.Count > 0;

    /// <summary>
    /// Uninstalls only the ticked updates on the focused machine. Only enabled in Installed scope
    /// and when at least one ticked update is actually uninstallable. The confirmation dialog lives
    /// in the view's code-behind so the VM doesn't pop UI directly.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUninstallChecked))]
    public Task UninstallCheckedAsync() =>
        FocusedComputer is { } c ? RunPatchSweepAsync([c], UninstallRowAsync) : Task.CompletedTask;

    private bool CanUninstallChecked() =>
        !IsBusy && IsInstalledMode && FocusedComputer is { } c
        && c.ScannedUpdates.Any(u => u.IsSelected && u.IsUninstallable);

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

        foreach (SelectableUpdate update in c.ScannedUpdates)
        {
            update.IsSelected = selected;
        }
    }

    /// <summary>Cancels the running sweep and halts continuous monitoring (the Monitor toggle restarts it).</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        _cts?.Cancel();
        IsMonitoring = false;
    }

    /// <summary>Drops the offline rows from the grid.</summary>
    [RelayCommand]
    private void RemoveOffline()
    {
        foreach (Computer computer in Computers.Where(c => c.IsOnline == false).ToList())
        {
            Computers.Remove(computer);
            SelectedComputers.Remove(computer);
        }

        if (FocusedComputer is { } focused && !Computers.Contains(focused))
        {
            FocusedComputer = null;
        }
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
        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
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
            IsBusy = false;
            _cts = null;
        }
    }

    /// <summary>
    /// Like <see cref="RunSweepAsync"/> (shares Stop / IsBusy / the cancellation race) but
    /// bounded — installs are heavy, so each host runs under a <see cref="SemaphoreSlim"/>
    /// throttle and a per-host timeout so one stuck box never holds up the grid.
    /// </summary>
    private async Task RunPatchSweepAsync(IReadOnlyList<Computer> rows, Func<Computer, CancellationToken, Task> operation)
    {
        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
        using var throttle = new SemaphoreSlim(Math.Max(1, _patchOptions.MaxConcurrentHosts));
        try
        {
            Task work = Task.WhenAll(rows.Select(row => RunOnePatchHostAsync(row, operation, throttle, cts.Token)));

            Task cancelled = Task.Delay(Timeout.Infinite, cts.Token);
            if (await Task.WhenAny(work, cancelled) == work)
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
            // Stop pressed — finished/in-flight rows keep their results.
        }
        finally
        {
            IsBusy = false;
            _cts = null;
        }
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
        computer.UpdateError = null;
        computer.UpdateProgress = null;
        computer.UpdatePhase = PatchPhase.Scanning.ToString();
        computer.UpdateMessage = "Scanning…";
        try
        {
            HostPatchStatus status = await _patch.ScanAsync(computer.Name, _patchOptions, _credentials.Current, token);
            ApplyStatus(computer, status);
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
        if (computer.ScannedUpdates.Count == 0
            || !computer.ScannedUpdates.Any(u => u.IsSelected && u.IsUninstallable))
        {
            computer.UpdateError = null;
            computer.UpdateProgress = null;
            computer.UpdatePhase = PatchPhase.Idle.ToString();
            computer.UpdateMessage = "No uninstallable updates selected";
            return;
        }

        string[] selectedKbs = [.. computer.ScannedUpdates
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
    }

    private async Task InstallRowAsync(Computer computer, CancellationToken token)
    {
        // Honor the per-machine checklist. Clone the shared options (concurrent hosts read it) and
        // scope this host's install to its ticked KBs: never scanned ⇒ no checklist ⇒ install all
        // (not-excluded, unchanged); scanned but nothing ticked ⇒ skip without launching a task.
        PatchOptions options = _patchOptions.Clone();
        if (computer.ScannedUpdates.Count > 0)
        {
            if (!computer.ScannedUpdates.Any(u => u.IsSelected))
            {
                computer.UpdateError = null;
                computer.UpdateProgress = null;
                computer.UpdatePhase = PatchPhase.Idle.ToString();
                computer.UpdateMessage = "No updates selected";
                return;
            }

            string[] selectedKbs = [.. computer.ScannedUpdates
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

        computer.UpdateError = null;
        computer.UpdateProgress = 0;
        computer.UpdatePhase = PatchPhase.Scanning.ToString();
        computer.UpdateMessage = "Starting…";

        // Progress<T> marshals callbacks to the captured (UI) context; WPF also auto-marshals
        // the scalar property updates, so the grid stays current as the SYSTEM task reports in.
        var progress = new Progress<HostPatchStatus>(s => ApplyStatus(computer, s));
        try
        {
            HostPatchStatus final = await _patch.InstallAsync(computer.Name, options, _credentials.Current, progress, token);
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
            computer.UpdateMessage = "Install failed";
            computer.UpdatePhase = PatchPhase.Error.ToString();
            _activity.Error(computer.Name, $"Install failed — {ex.Message}");
        }
    }

    /// <summary>Writes a <see cref="HostPatchStatus"/> snapshot onto a row, logging phase transitions only.</summary>
    private void ApplyStatus(Computer computer, HostPatchStatus status)
    {
        string phase = status.Phase.ToString();
        bool phaseChanged = !string.Equals(computer.UpdatePhase, phase, StringComparison.Ordinal);

        computer.UpdatePhase = phase;
        computer.UpdateMessage = status.Message;
        computer.UpdateProgress = status.Percent;

        if (status.Phase == PatchPhase.Available)
        {
            computer.UpdatesAvailable = status.AvailableCount;
            ReplaceScannedUpdates(computer, status.Updates);
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
    /// Repopulates a row's checklist from a fresh scan, preserving the user's prior unticks by KB
    /// (fallback title) so a re-scan doesn't silently re-select updates they chose to skip.
    /// </summary>
    private static void ReplaceScannedUpdates(Computer computer, IReadOnlyList<SoftwareUpdate> updates)
    {
        var prior = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (SelectableUpdate existing in computer.ScannedUpdates)
        {
            prior[existing.Kb ?? existing.Title] = existing.IsSelected;
        }

        computer.ScannedUpdates.Clear();
        foreach (SoftwareUpdate update in updates)
        {
            bool selected = !prior.TryGetValue(update.ArticleId ?? update.Title, out bool wasSelected) || wasSelected;
            computer.ScannedUpdates.Add(new SelectableUpdate(update, selected));
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
        bool? previous = computer.IsOnline;
        (bool online, string? error) = await ProbeReachabilityAsync(computer, token);

        computer.IsOnline = online;
        computer.LastError = online ? null : error;

        // While the Windows Update view is up, also keep the Pending Reboot column live —
        // a small registry/SCCM-aggregated probe over WinRM, throttled so a large fleet
        // doesn't open dozens of runspaces at once.
        if (online && IsUpdateMode)
        {
            await ProbeRebootPendingAsync(computer, token).ConfigureAwait(false);
        }

        if (previous == online)
        {
            return; // unchanged — leave LastStatus as-is
        }

        computer.LastStatus = online ? "Online" : "Offline";
        if (online)
        {
            _activity.Info(computer.Name, previous is null ? "Online" : "Came online");
        }
        else
        {
            _activity.Warn(computer.Name, previous is null ? $"Offline — {error}" : $"Went offline — {error}");
        }
    }

    /// <summary>
    /// One pending-reboot pass against an online row, throttled so a big fleet doesn't open
    /// dozens of runspaces at once. Best-effort: a probe failure leaves the row's previous
    /// <see cref="Computer.RebootRequired"/> in place and the next tick will retry.
    /// </summary>
    private async Task ProbeRebootPendingAsync(Computer computer, CancellationToken token)
    {
        await _rebootProbeThrottle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            bool? pending = await _rebootProbe.IsRebootPendingAsync(computer.Name, CurrentPsCredential(), token).ConfigureAwait(false);
            if (pending.HasValue)
            {
                computer.RebootRequired = pending.Value;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Swallow — don't spam the activity log every 20 s for a flaky host.
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

    /// <summary>Removes the currently-selected rows from the grid (Delete key / menu).</summary>
    public void RemoveSelected()
    {
        foreach (Computer computer in SelectedComputers.ToList())
        {
            Computers.Remove(computer);
        }

        SelectedComputers.Clear();

        if (FocusedComputer is { } focused && !Computers.Contains(focused))
        {
            FocusedComputer = null;
        }
    }

    /// <summary>Mirrors the grid's selection into <see cref="SelectedComputers"/> (called from code-behind).</summary>
    public void SetSelection(IEnumerable<Computer> selected)
    {
        SelectedComputers.Clear();
        foreach (Computer computer in selected)
        {
            SelectedComputers.Add(computer);
        }
    }

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
