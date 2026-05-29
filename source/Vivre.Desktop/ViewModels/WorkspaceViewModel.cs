using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
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
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _monitorCts;

    /// <summary>Tab title (editable — double-click the tab header to rename).</summary>
    [ObservableProperty]
    public partial string Title { get; set; } = "New tab";

    /// <summary>Rows shown in the computer grid.</summary>
    public ObservableCollection<Computer> Computers { get; } = [];

    /// <summary>Rows the user has selected (kept in sync from the grid's selection).</summary>
    public ObservableCollection<Computer> SelectedComputers { get; } = [];

    /// <summary>Live "Online: (online/total)" summary for this tab, shown in the bottom status bar.
    /// Recomputed whenever a row's online state changes or the list grows/shrinks.</summary>
    public string OnlineSummary => $"Online: ({Computers.Count(c => c.IsOnline == true)}/{Computers.Count})";

    /// <summary>Names of the online rows (IsOnline == true), for the status-bar "Copy online" button.</summary>
    public IReadOnlyList<string> OnlineNames => [.. Computers.Where(c => c.IsOnline == true).Select(c => c.Name)];

    /// <summary>Names of the offline rows (IsOnline == false), for the status-bar "Copy offline" button.</summary>
    public IReadOnlyList<string> OfflineNames => [.. Computers.Where(c => c.IsOnline == false).Select(c => c.Name)];

    /// <summary>ConfigMgr client actions shown in the grid's right-click menu.</summary>
    public IReadOnlyList<ScheduleAction> ClientActions => Core.Sccm.ClientActions.All;

    /// <summary>True while a sweep (Ping All / Check All) is running — drives the busy indicator and button enable-state.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PingAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(PingOfflineCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    public partial bool IsBusy { get; set; }

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
    public WorkspaceViewModel(IHostPinger pinger, IHostProbe hostProbe, IConfigMgrClient configMgr, IWinRmEnabler winRm, CredentialStore credentials, IComputerListStore lists, IActivityLog activity, IScriptLibrary scripts)
    {
        _pinger = pinger;
        _hostProbe = hostProbe;
        _configMgr = configMgr;
        _winRm = winRm;
        _credentials = credentials;
        _lists = lists;
        _activity = activity;
        _scripts = scripts;
        // Keep the status-bar online/total live as rows are added/removed and their state changes.
        Computers.CollectionChanged += OnComputersChanged;
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
    }

    private void OnComputerStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Computer.IsOnline))
        {
            OnPropertyChanged(nameof(OnlineSummary));
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
