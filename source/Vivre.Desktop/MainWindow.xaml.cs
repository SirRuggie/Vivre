using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Vivre.Core.Models;
using Vivre.Core.Updates;
using Vivre.Desktop.ViewModels;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace Vivre.Desktop;

/// <summary>
/// The shell window: menus (File / Settings), tab management, and the TabControl that
/// hosts one <see cref="WorkspaceView"/> per tab. Per-tab grid behaviour lives in
/// <see cref="WorkspaceView"/>; this code-behind handles app-level concerns and the
/// File menu (which acts on the active tab).
/// </summary>
public partial class MainWindow : FluentWindow
{
    // --- keyboard accelerators (wired in MainWindow.xaml's InputBindings/CommandBindings) ---
    public static readonly RoutedUICommand NewTabKey = new("New tab", nameof(NewTabKey), typeof(MainWindow));
    public static readonly RoutedUICommand CloseTabKey = new("Close tab", nameof(CloseTabKey), typeof(MainWindow));
    public static readonly RoutedUICommand FocusAddKey = new("Focus add box", nameof(FocusAddKey), typeof(MainWindow));
    public static readonly RoutedUICommand RenameTabKey = new("Rename tab", nameof(RenameTabKey), typeof(MainWindow));
    public static readonly RoutedUICommand ToggleModeKey = new("Toggle mode", nameof(ToggleModeKey), typeof(MainWindow));
    public static readonly RoutedUICommand RefreshKey = new("Refresh", nameof(RefreshKey), typeof(MainWindow));
    public static readonly RoutedUICommand InstallKey = new("Install", nameof(InstallKey), typeof(MainWindow));
    public static readonly RoutedUICommand HelpKey = new("Help", nameof(HelpKey), typeof(MainWindow));

    /// <summary>Persisted preferences (theme) — injected by the composition root.</summary>
    internal AppSettingsStore? Settings { get; set; }

    /// <summary>Activity log for surfacing settings-save failures — injected by the composition root.</summary>
    internal Core.Logging.IActivityLog? Log { get; set; }

    /// <summary>The theme applied at startup — used to tick the right Theme menu item.</summary>
    internal string SavedTheme { get; set; } = "Dark";

    public MainWindow()
    {
        InitializeComponent();
        // DataContext is assigned by the composition root (App) after construction,
        // so build the File menu once it's available.
        Loaded += (_, _) =>
        {
            BuildFileMenu();
            StartRelativeTimeRefresh();
            HookOperationToasts();
            HookBottomDock();
            UpdateThemeChecks(SavedTheme);
            try { AutoCheckItem.IsChecked = Settings?.Load().AutoCheckOnLoad ?? true; } catch { AutoCheckItem.IsChecked = true; }
        };

        // App-lifetime teardown (single main window): stop timers, drop cross-tab subscriptions, and dispose
        // the tray icon + any open tabs. Cosmetic at process exit, but leaves nothing dangling.
        Closed += OnWindowClosed;
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    // --- completion toast (tray balloon when a Scan/Install/Uninstall finishes) ---

    private void HookOperationToasts()
    {
        if (Shell is not { } shell)
        {
            return;
        }

        foreach (WorkspaceViewModel tab in shell.Tabs.OfType<WorkspaceViewModel>())
        {
            tab.OperationCompleted += OnOperationCompleted;
        }

        // Tabs come and go — keep subscriptions in step (no double-subscribe, no leak).
        shell.Tabs.CollectionChanged += (_, e) =>
        {
            if (e.OldItems is not null)
            {
                foreach (WorkspaceViewModel tab in e.OldItems.OfType<WorkspaceViewModel>())
                {
                    tab.OperationCompleted -= OnOperationCompleted;
                }
            }

            if (e.NewItems is not null)
            {
                foreach (WorkspaceViewModel tab in e.NewItems.OfType<WorkspaceViewModel>())
                {
                    tab.OperationCompleted -= OnOperationCompleted;
                    tab.OperationCompleted += OnOperationCompleted;
                }
            }
        };
    }

    private DispatcherTimer? _completionBarTimer;

    private void OnOperationCompleted(string summary)
    {
        // Window focused → a brief in-window banner (the operator is watching, a tray balloon would
        // be missed/ignored). Window unfocused → the tray balloon, as before.
        if (IsActive)
        {
            Dispatcher.BeginInvoke(() => ShowCompletionBar(summary));
            return;
        }

        Dispatcher.BeginInvoke(() => TrayIcon.ShowBalloonTip("Vivre", summary, Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info));
    }

    private void ShowCompletionBar(string summary)
    {
        CompletionBar.Message = summary;
        CompletionBar.Severity = summary.Contains("failed", StringComparison.OrdinalIgnoreCase)
            ? InfoBarSeverity.Warning
            : InfoBarSeverity.Success;
        CompletionBar.IsOpen = true;

        // Auto-dismiss after a few seconds (the activity log + status bar keep the durable record).
        _completionBarTimer ??= new DispatcherTimer();
        _completionBarTimer.Stop();
        _completionBarTimer.Interval = TimeSpan.FromSeconds(7);
        _completionBarTimer.Tick -= OnCompletionBarTick;
        _completionBarTimer.Tick += OnCompletionBarTick;
        _completionBarTimer.Start();
    }

    private void OnCompletionBarTick(object? sender, EventArgs e)
    {
        _completionBarTimer?.Stop();
        CompletionBar.IsOpen = false;
    }

    // --- keep relative times ("Last reboot") current between health checks ---

    private DispatcherTimer? _relativeTimeTimer;

    private void StartRelativeTimeRefresh()
    {
        // LastRebootDisplay is relative to DateTime.Now, so it drifts; tick every minute to re-evaluate it on
        // every row. One app-level timer (rooted by the dispatcher while running; stopped in OnWindowClosed).
        _relativeTimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _relativeTimeTimer.Tick += OnRefreshRelativeTimes;
        _relativeTimeTimer.Start();
    }

    /// <summary>App-lifetime teardown (wired in the ctor). Stops the timers, drops the shell/observed-tab
    /// subscriptions, disposes any tabs still open, and disposes the tray icon.</summary>
    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_relativeTimeTimer is { } rt)
        {
            rt.Stop();
            rt.Tick -= OnRefreshRelativeTimes;
            _relativeTimeTimer = null;
        }

        if (_completionBarTimer is { } cb)
        {
            cb.Stop();
            cb.Tick -= OnCompletionBarTick;
            _completionBarTimer = null;
        }

        if (Shell is { } shell)
        {
            shell.PropertyChanged -= OnShellPropertyChanged;
        }

        SubscribeToSelectedTab(null); // detach the observed tab's PropertyChanged

        foreach (IDisposable tab in Shell?.Tabs.OfType<IDisposable>().ToArray() ?? [])
        {
            tab.Dispose();
        }

        TrayIcon?.Dispose();
    }

    private void OnRefreshRelativeTimes(object? sender, EventArgs e)
    {
        if (Shell is not { } shell)
        {
            return;
        }

        foreach (var computer in shell.Tabs.OfType<WorkspaceViewModel>().SelectMany(tab => tab.Computers))
        {
            computer.RefreshRelativeTime();
        }
    }

    // --- system tray ---

    private void OnTrayOpen(object sender, RoutedEventArgs e)
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnTrayExit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    // --- unified bottom dock (Activity tab + per-machine Updates tab) ---

    /// <summary>Remembered (possibly user-resized) dock height, restored when the dock reopens.</summary>
    private GridLength _activityHeight = new(170);

    /// <summary>The shell whose <see cref="ShellViewModel.SelectedTab"/> we're currently subscribed to —
    /// tracked so we can re-subscribe when the active tab changes.</summary>
    private WorkspaceViewModel? _observedTab;

    /// <summary>
    /// Wire up cross-tab observation: the shell's SelectedTab changing, and the active tab's
    /// FocusedComputer / IsUpdateMode changing — any of which can open/close the dock or flip which
    /// tab should be selected. Re-subscribes to the new SelectedTab whenever it changes.
    /// </summary>
    private void HookBottomDock()
    {
        if (Shell is not { } shell)
        {
            return;
        }

        shell.PropertyChanged += OnShellPropertyChanged;
        SubscribeToSelectedTab(shell.SelectedTab as WorkspaceViewModel);
        RecomputeBottomDock();
    }

    private void OnShellPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.SelectedTab))
        {
            SubscribeToSelectedTab(Shell?.SelectedTab as WorkspaceViewModel);
            RecomputeBottomDock();
        }
    }

    private void SubscribeToSelectedTab(WorkspaceViewModel? tab)
    {
        if (ReferenceEquals(_observedTab, tab))
        {
            return;
        }

        if (_observedTab is not null)
        {
            _observedTab.PropertyChanged -= OnSelectedTabPropertyChanged;
        }

        _observedTab = tab;

        if (_observedTab is not null)
        {
            _observedTab.PropertyChanged += OnSelectedTabPropertyChanged;
        }
    }

    private void OnSelectedTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceViewModel.FocusedComputer) or nameof(WorkspaceViewModel.IsUpdateMode))
        {
            RecomputeBottomDock();

            // Clicking a machine in Update view is an explicit "show me this machine" — land on the
            // Updates tab (RecomputeBottomDock has just made it visible), even if Activity was showing.
            if (e.PropertyName == nameof(WorkspaceViewModel.FocusedComputer) && UpdatesTriggerActive)
            {
                BottomDockTabs.SelectedItem = UpdatesTab;
            }
        }
    }

    /// <summary>True when the per-machine Updates panel should be shown: a machine is focused in
    /// Windows Update mode on the active tab.</summary>
    private bool UpdatesTriggerActive =>
        Shell?.SelectedTab is WorkspaceViewModel { IsUpdateMode: true, FocusedComputer: not null };

    /// <summary>
    /// Single source of truth for the dock: shows/hides the Updates tab, opens or collapses the dock
    /// per the open rule (activity requested OR a focused machine in Update mode), auto-selects the
    /// right tab, and re-applies the KB/title filter to the (possibly new) focused machine.
    /// </summary>
    private void RecomputeBottomDock()
    {
        bool updates = UpdatesTriggerActive;
        bool activity = ActivityLogMenuItem.IsChecked;

        // The Updates tab is only selectable when a machine is focused in Update mode.
        UpdatesTab.Visibility = updates ? Visibility.Visible : Visibility.Collapsed;
        if (!updates && BottomDockTabs.SelectedItem == UpdatesTab)
        {
            BottomDockTabs.SelectedItem = ActivityTab;
        }

        if (activity || updates)
        {
            ShowDock();
        }
        else
        {
            HideDock();
        }

        // Keep the filter box pointed at whatever machine is now focused.
        ApplyUpdateFilter((Shell?.SelectedTab as WorkspaceViewModel)?.FocusedComputer);
    }

    private void ShowDock()
    {
        ActivityRow.Height = _activityHeight;
        SplitterRow.Height = GridLength.Auto;
        ActivitySplitter.Visibility = Visibility.Visible;
        ActivityPanel.Visibility = Visibility.Visible;
    }

    private void HideDock()
    {
        // Remember the current (possibly user-resized) height so reopening restores it.
        if (ActivityRow.ActualHeight > 0)
        {
            _activityHeight = new GridLength(ActivityRow.ActualHeight);
        }

        ActivitySplitter.Visibility = Visibility.Collapsed;
        ActivityPanel.Visibility = Visibility.Collapsed;
        SplitterRow.Height = new GridLength(0);
        ActivityRow.Height = new GridLength(0);
    }

    /// <summary>View ▸ Activity log — opens the dock on the Activity tab (or closes it, unless the
    /// per-machine Updates trigger still keeps the dock open).</summary>
    private void OnToggleActivityLog(object sender, RoutedEventArgs e)
    {
        if (ActivityLogMenuItem.IsChecked)
        {
            BottomDockTabs.SelectedItem = ActivityTab;
        }

        RecomputeBottomDock();
    }

    /// <summary>
    /// The dock Close button — dismisses the whole dock: turn activity-requested off AND clear the
    /// focused machine, then collapse the dock. Mirrors the old close path exactly.
    /// </summary>
    private void OnCloseBottomDock(object sender, RoutedEventArgs e)
    {
        ActivityLogMenuItem.IsChecked = false;
        if (Shell?.SelectedTab is WorkspaceViewModel vm)
        {
            vm.FocusedComputer = null;
        }

        // FocusedComputer = null fires RecomputeBottomDock via the observer, but clear explicitly in
        // case nothing was focused (so the activity-off still collapses the dock).
        RecomputeBottomDock();
    }

    /// <summary>Opens the activity-log dock filtered to one machine (right-click "Show messages").</summary>
    public void ShowActivityForMachine(string machine)
    {
        if (Shell is { } shell)
        {
            shell.ActivityLog.SearchText = machine;
        }

        ActivityLogMenuItem.IsChecked = true;
        BottomDockTabs.SelectedItem = ActivityTab;
        RecomputeBottomDock();
    }

    // --- per-machine update-list filter (the "Filter by KB or title" box in the Updates tab) ---

    private string _updateFilter = string.Empty;

    private void OnUpdateFilterChanged(object sender, TextChangedEventArgs e)
    {
        _updateFilter = (sender as System.Windows.Controls.TextBox)?.Text?.Trim() ?? string.Empty;
        ApplyUpdateFilter((Shell?.SelectedTab as WorkspaceViewModel)?.FocusedComputer);
    }

    /// <summary>
    /// Filters both per-scope update lists by KB or title. Applied to each collection's default view
    /// (which the grids bind to), so it survives tab switches and the focused machine changing.
    /// </summary>
    private void ApplyUpdateFilter(Computer? focused)
    {
        if (focused is null)
        {
            return;
        }

        ApplyUpdateFilterTo(focused.ApplicableUpdates);
        ApplyUpdateFilterTo(focused.InstalledUpdates);
    }

    private void ApplyUpdateFilterTo(System.Collections.IEnumerable collection)
    {
        ICollectionView? view = System.Windows.Data.CollectionViewSource.GetDefaultView(collection);
        if (view is null)
        {
            return;
        }

        if (_updateFilter.Length == 0)
        {
            view.Filter = null;
        }
        else
        {
            string f = _updateFilter;
            view.Filter = o => o is Vivre.Core.Updates.SelectableUpdate u
                && ((u.Kb?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (u.Title?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false));
        }
    }

    /// <summary>
    /// Uninstall confirmation (the Updates tab's "Uninstall checked" button). Pops a Wpf.Ui MessageBox
    /// before kicking the per-machine uninstall sweep — uninstalls are destructive enough to deserve a
    /// "yes, really" prompt. Only counts ticked rows that are actually removable.
    /// </summary>
    private async void OnUninstallChecked(object sender, RoutedEventArgs e)
    {
        if (Shell?.SelectedTab is not WorkspaceViewModel { FocusedComputer: { } c } vm)
        {
            return;
        }

        // The Uninstall button is only visible in Installed scope, so the relevant cache here is
        // InstalledUpdates (the user can only have ticked rows from that scope's list).
        int count = c.InstalledUpdates.Count(u => u.IsSelected && u.IsUninstallable);
        if (count == 0)
        {
            return;
        }

        var confirm = new MessageBox
        {
            Title = "Uninstall updates",
            Content = $"Uninstall {count} update(s) from {c.Name}?\n\n"
                      + "This may require a reboot. Use with care — once removed, some updates "
                      + "can't be reinstalled through normal scans (Windows may consider them superseded).",
            PrimaryButtonText = "Uninstall",
            CloseButtonText = "Cancel",
        };

        if (await confirm.ShowDialogAsync() == MessageBoxResult.Primary)
        {
            await vm.UninstallCheckedAsync();
        }
    }

    // --- activity-log copy (right-click a line, or Copy all) ---

    /// <summary>
    /// Right-clicking a line selects it first (unless it's already part of a multi-selection), so the
    /// context-menu Copy acts on what was clicked. Handled at the grid level — walking up the visual
    /// tree to the row — rather than via a row Style, so we don't override WPF-UI's themed DataGridRow.
    /// </summary>
    private void OnActivityGridRightClick(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? d = e.OriginalSource as DependencyObject;
        while (d is not null and not DataGridRow)
        {
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }

        if (d is DataGridRow { IsSelected: false } row)
        {
            ActivityGrid.SelectedItems.Clear();
            row.IsSelected = true;
        }
    }

    private void OnActivityCopy(object sender, RoutedEventArgs e) => CopyActivityEntries(ActivityGrid.SelectedItems);

    private void OnActivityCopyAll(object sender, RoutedEventArgs e) => CopyActivityEntries(ActivityGrid.Items);

    /// <summary>Copies the given log entries to the clipboard as tab-separated time / machine / message lines.</summary>
    private static void CopyActivityEntries(System.Collections.IEnumerable items)
    {
        var sb = new StringBuilder();
        foreach (object item in items)
        {
            if (item is Core.Logging.LogEntry entry)
            {
                sb.Append(entry.Timestamp.ToString("HH:mm:ss"))
                  .Append('\t')
                  .Append(entry.Machine ?? string.Empty)
                  .Append('\t')
                  .AppendLine(entry.Message);
            }
        }

        if (sb.Length > 0)
        {
            try { Clipboard.SetText(sb.ToString()); }
            catch { /* the clipboard can be transiently locked by another app — not worth surfacing */ }
        }
    }

    // --- theme (app-wide, persisted to %APPDATA%\Vivre\settings.json) ---

    private void OnThemeLight(object sender, RoutedEventArgs e) => SetTheme("Light");

    private void OnThemeDark(object sender, RoutedEventArgs e) => SetTheme("Dark");

    private void OnThemeSystem(object sender, RoutedEventArgs e) => SetTheme("System");

    private void SetTheme(string theme)
    {
        App.ApplyTheme(theme);
        UpdateThemeChecks(theme);
        try
        {
            // Load-modify-save: saving a fresh AppSettings would wipe everything else (packages folder,
            // software-service map, custom columns + hidden-column layout, auto-check flag).
            AppSettings s = Settings?.Load() ?? new AppSettings();
            s.Theme = theme;
            Settings?.Save(s);
        }
        catch (Exception ex)
        {
            Log?.Warn(null, $"Couldn't save theme preference. {ex.Message}");
        }
    }

    /// <summary>Settings ▸ Auto-check on load. Persists the flag; each tab reads it when a list loads to
    /// decide whether to auto-ping + check vitals on the new machines.</summary>
    private void OnToggleAutoCheck(object sender, RoutedEventArgs e)
    {
        try
        {
            AppSettings s = Settings?.Load() ?? new AppSettings();
            s.AutoCheckOnLoad = AutoCheckItem.IsChecked;
            Settings?.Save(s);
        }
        catch (Exception ex)
        {
            Log?.Warn(null, $"Couldn't save the auto-check setting. {ex.Message}");
        }
    }

    /// <summary>Selects the active theme's radio dot in the menu (the three are a radio group).</summary>
    private void UpdateThemeChecks(string theme)
    {
        if (LightThemeRadio is null)
        {
            return;
        }

        LightThemeRadio.IsChecked = theme == "Light";
        DarkThemeRadio.IsChecked = theme == "Dark";
        SystemThemeRadio.IsChecked = theme == "System";
    }

    // --- View mode + update source (radio menu items; the click sets the state, the bound RadioButton
    //     reflects it). ---

    private void OnSelectMachinesMode(object sender, RoutedEventArgs e) => Shell?.ShowMachineView(updateMode: false);

    private void OnSelectUpdateMode(object sender, RoutedEventArgs e) => Shell?.ShowMachineView(updateMode: true);

    private void OnSourceWindowsUpdate(object sender, RoutedEventArgs e) => SetSource(UpdateSource.WindowsUpdate);

    private void OnSourceMicrosoftUpdate(object sender, RoutedEventArgs e) => SetSource(UpdateSource.MicrosoftUpdate);

    private void OnSourceManaged(object sender, RoutedEventArgs e) => SetSource(UpdateSource.Managed);

    private void SetSource(UpdateSource source)
    {
        if (Shell?.SelectedTab is WorkspaceViewModel vm)
        {
            vm.SelectedSource = source;
        }
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        if (Shell is { } shell)
        {
            new SettingsWindow(shell.Credentials) { Owner = this }.ShowDialog();
        }
    }

    private void OnOpenAbout(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();

    private void OnOpenCrossDomainRdp(object sender, RoutedEventArgs e) => Shell?.OpenCrossDomainRdpCommand.Execute(null);

    private void OnOpenHelp(object sender, RoutedEventArgs e) => ShowHelp();

    private void OnHelpKey(object sender, ExecutedRoutedEventArgs e) => ShowHelp();

    private void ShowHelp()
    {
        // Reuse the open guide if there is one, otherwise open it modeless.
        foreach (Window w in Application.Current.Windows)
        {
            if (w is HelpWindow existing)
            {
                existing.Activate();
                return;
            }
        }

        new HelpWindow { Owner = this }.Show();
    }

    private void OnOpenExcludeDialog(object sender, RoutedEventArgs e)
    {
        if (Shell?.SelectedTab is not WorkspaceViewModel vm)
        {
            return;
        }

        var dialog = new TextPromptWindow(
            "Exclude updates",
            "Comma-separated update title terms to skip (e.g. SQL, Silverlight, Edge):",
            vm.ExcludeText) { Owner = this };

        if (dialog.ShowDialog() == true && dialog.Value is { } text)
        {
            vm.ExcludeText = text;
        }
    }

    // --- toolbar Install (confirm scope before hitting production) ---

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (Shell?.SelectedTab is not WorkspaceViewModel vm)
        {
            return;
        }

        // Mirror InstallTarget's scope: the selected rows, or every row when nothing is selected.
        bool hasSelection = vm.SelectedComputers.Count > 0;
        IReadOnlyList<Computer> targets = hasSelection ? [.. vm.SelectedComputers] : [.. vm.Computers];
        int count = targets.Count;
        if (count == 0 || !vm.InstallTargetCommand.CanExecute(null))
        {
            return; // nothing to target / a sweep is already running
        }

        // Pre-flight: a reboot-pending target can jam WinRM and fail the install (the WinRM-unhealthy
        // failure mode). Offer to reboot those first instead of wasting the run on them. "Install
        // anyway" is itself the install confirmation, so we don't double-prompt in that case.
        List<Computer> pending = [.. targets.Where(c => c.RebootRequired == true)];
        if (pending.Count > 0)
        {
            var nudge = new MessageBox
            {
                Title = "Reboot pending",
                Content = $"{pending.Count} of {count} target machine(s) have a reboot pending.\n\n"
                          + "A pending reboot can jam WinRM and make the install fail. You can reboot those first "
                          + "(then install once they're back), or install anyway.",
                PrimaryButtonText = $"Reboot the {pending.Count} first",
                SecondaryButtonText = "Install anyway",
                CloseButtonText = "Cancel",
            };

            MessageBoxResult choice = await nudge.ShowDialogAsync();
            if (choice == MessageBoxResult.Primary)
            {
                await vm.RebootForceSelectedAsync(pending);
                return; // don't install now — the user re-runs install once they're back online
            }

            if (choice != MessageBoxResult.Secondary)
            {
                return; // Cancel / closed
            }

            // "Install anyway" → proceed straight to the install (this dialog was the confirmation).
            if (vm.InstallTargetCommand.CanExecute(null))
            {
                vm.InstallTargetCommand.Execute(null);
            }

            return;
        }

        string scope = hasSelection ? $"the {count} selected machine(s)" : $"all {count} machine(s) in this tab";
        var confirm = new MessageBox
        {
            Title = "Install updates",
            Content = $"Download and install applicable updates on {scope}?\n\n"
                      + "Each host runs a one-time SYSTEM task. A required reboot is reported, not forced.",
            PrimaryButtonText = $"Install on {count}",
            CloseButtonText = "Cancel",
        };

        if (await confirm.ShowDialogAsync() == MessageBoxResult.Primary
            && vm.InstallTargetCommand.CanExecute(null))
        {
            vm.InstallTargetCommand.Execute(null);
        }
    }

    // --- keyboard accelerator handlers (routed from the window's InputBindings) ---

    private void OnNewTabKey(object sender, ExecutedRoutedEventArgs e) => Shell?.NewTabCommand.Execute(null);

    private void OnCloseTabKey(object sender, ExecutedRoutedEventArgs e)
    {
        if (Shell?.SelectedTab is { } tab)
        {
            CloseTabWithGuard(tab);
        }
    }

    private void OnFocusAddKey(object sender, ExecutedRoutedEventArgs e)
    {
        QuickAddBox.Focus();
        QuickAddBox.SelectAll();
    }

    private void OnRenameTabKey(object sender, ExecutedRoutedEventArgs e)
    {
        if (Shell?.SelectedTab is WorkspaceViewModel tab)
        {
            RenameTab(tab);
        }
    }

    private void OnToggleModeKey(object sender, ExecutedRoutedEventArgs e) =>
        (Shell?.SelectedTab as WorkspaceViewModel)?.ToggleUpdateModeCommand.Execute(null);

    private void OnRefreshKey(object sender, ExecutedRoutedEventArgs e)
    {
        var cmd = (Shell?.SelectedTab as WorkspaceViewModel)?.PingAllCommand;
        if (cmd?.CanExecute(null) == true)
        {
            cmd.Execute(null);
        }
    }

    private void OnInstallKey(object sender, ExecutedRoutedEventArgs e)
    {
        // Ctrl+Enter installs — only where the toolbar Install button is shown (Update / Applicable scope).
        if (Shell?.SelectedTab is WorkspaceViewModel { CanShowInstallToolbar: true })
        {
            OnInstallClick(sender, e);
        }
    }

    // --- tabs ---

    private void OnNewTab(object sender, RoutedEventArgs e) => Shell?.NewTabCommand.Execute(null);

    private void OnCloseTab(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ITabViewModel tab })
        {
            CloseTabWithGuard(tab);
        }
    }

    /// <summary>Closes a tab — but confirms first when it holds work (loaded machines or a live
    /// monitor/sweep) so a curated list or running op isn't lost on a stray click. Empty, idle tabs
    /// close instantly so the guard never habituates.</summary>
    private async void CloseTabWithGuard(ITabViewModel tab)
    {
        // Only a machine workspace holds unsaved "work" worth confirming; the Cross-Domain RDP tab's state lives
        // on disk, so it (and idle workspaces) close instantly.
        if (tab is WorkspaceViewModel { HasWork: true } workspace)
        {
            int n = workspace.Computers.Count;
            string detail = n > 0 ? $"{n} machine(s)" : "a running operation";
            var confirm = new MessageBox
            {
                Title = "Close tab",
                Content = $"Close \"{workspace.Title}\"? It has {detail}.\n\nMachines stay in any saved list — re-open to bring them back.",
                PrimaryButtonText = "Close tab",
                CloseButtonText = "Keep open",
            };
            if (await confirm.ShowDialogAsync() != MessageBoxResult.Primary)
            {
                return;
            }
        }

        Shell?.CloseTabCommand.Execute(tab);
    }

    private void OnCloseOtherTabs(object sender, RoutedEventArgs e)
    {
        if (Shell is { } shell && sender is FrameworkElement { DataContext: ITabViewModel keep })
        {
            CloseTabs([.. shell.Tabs.Where(t => t != keep)]);
        }
    }

    private void OnCloseTabsToRight(object sender, RoutedEventArgs e)
    {
        if (Shell is { } shell && sender is FrameworkElement { DataContext: ITabViewModel anchor })
        {
            int index = shell.Tabs.IndexOf(anchor);
            if (index >= 0)
            {
                CloseTabs([.. shell.Tabs.Skip(index + 1)]);
            }
        }
    }

    /// <summary>Closes a set of tabs (browser-style "close others / to the right"). Confirms once
    /// up front if any of them still has work, rather than prompting per tab.</summary>
    private async void CloseTabs(IReadOnlyList<ITabViewModel> tabs)
    {
        if (Shell is not { } shell || tabs.Count == 0)
        {
            return;
        }

        int withWork = tabs.OfType<WorkspaceViewModel>().Count(t => t.HasWork);
        if (withWork > 0)
        {
            var confirm = new MessageBox
            {
                Title = "Close tabs",
                Content = $"Close {tabs.Count} tab(s)? {withWork} still ha{(withWork == 1 ? "s" : "ve")} machines or a running operation.\n\n"
                          + "Machines stay in any saved list — re-open to bring them back.",
                PrimaryButtonText = $"Close {tabs.Count}",
                CloseButtonText = "Keep open",
            };

            if (await confirm.ShowDialogAsync() != MessageBoxResult.Primary)
            {
                return;
            }
        }

        foreach (ITabViewModel tab in tabs)
        {
            shell.CloseTabCommand.Execute(tab);
        }
    }

    private void OnTabHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: WorkspaceViewModel workspace })
        {
            RenameTab(workspace);
        }
    }

    private void OnRenameTab(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WorkspaceViewModel workspace })
        {
            RenameTab(workspace);
        }
    }

    private void RenameTab(WorkspaceViewModel workspace)
    {
        var dialog = new TextPromptWindow("Rename tab", "Tab name:", workspace.Title) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Value is { } name)
        {
            workspace.Title = name;
        }
    }

    // --- shared add-computers bar (acts on the active tab) ---

    private void OnQuickAddKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddFromQuickBox();
            e.Handled = true;
        }
    }

    private void OnQuickAdd(object sender, RoutedEventArgs e) => AddFromQuickBox();

    private void OnPasteList(object sender, RoutedEventArgs e)
    {
        if (Shell?.SelectedTab is WorkspaceViewModel vm)
        {
            new LoadComputersWindow(vm) { Owner = this }.ShowDialog();
        }
    }

    private void AddFromQuickBox()
    {
        if (Shell?.SelectedTab is not WorkspaceViewModel vm)
        {
            return;
        }

        string[] names = QuickAddBox.Text.Split(
            [',', ';', ' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (names.Length > 0)
        {
            vm.AddComputers(names);
            QuickAddBox.Text = string.Empty;
        }
    }

    // --- File menu (acts on the active tab's workspace) ---

    private void OnFileMenuOpened(object sender, RoutedEventArgs e)
    {
        // SubmenuOpened bubbles: when a child submenu (Open list / Delete list) opens it would
        // re-fire here and rebuild the whole menu, destroying the submenu under the cursor. Only
        // rebuild when it's the File menu itself opening.
        if (ReferenceEquals(e.OriginalSource, FileMenu))
        {
            BuildFileMenu();
        }
    }

    private void BuildFileMenu()
    {
        FileMenu.Items.Clear();

        var newTab = new MenuItem { Header = "_New tab", InputGestureText = "Ctrl+T" };
        newTab.Click += OnNewTab;
        FileMenu.Items.Add(newTab);

        FileMenu.Items.Add(new Separator());

        WorkspaceViewModel? vm = Shell?.SelectedTab as WorkspaceViewModel;

        var newClear = new MenuItem { Header = "_Clear this tab", IsEnabled = vm is not null };
        newClear.Click += (_, _) => vm?.SetComputers([]);
        FileMenu.Items.Add(newClear);

        IReadOnlyList<string> lists = vm?.SavedLists() ?? [];

        var openMenu = new MenuItem { Header = "_Open list", IsEnabled = vm is not null };
        AddListItems(openMenu, lists, name => vm?.OpenList(name));
        FileMenu.Items.Add(openMenu);

        var saveItem = new MenuItem { Header = "_Save tab as list…", IsEnabled = vm is not null };
        saveItem.Click += (_, _) => { if (vm is not null) { SaveCurrentAsList(vm); } };
        FileMenu.Items.Add(saveItem);

        var deleteMenu = new MenuItem { Header = "_Delete list" };
        AddListItems(deleteMenu, lists, name => { if (vm is not null) { ConfirmDeleteList(vm, name); } });
        FileMenu.Items.Add(deleteMenu);

        FileMenu.Items.Add(new Separator());

        var pasteItem = new MenuItem { Header = "_Paste computers…", IsEnabled = vm is not null };
        pasteItem.Click += (_, _) => { if (vm is not null) { new LoadComputersWindow(vm) { Owner = this }.ShowDialog(); } };
        FileMenu.Items.Add(pasteItem);

        FileMenu.Items.Add(new Separator());

        var exportItem = new MenuItem { Header = "_Export to CSV…", IsEnabled = vm is { HasComputers: true } };
        exportItem.Click += (_, _) => { if (vm is not null) { ExportTabCsv(vm); } };
        FileMenu.Items.Add(exportItem);

        FileMenu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = "E_xit" };
        exitItem.Click += (_, _) => Close();
        FileMenu.Items.Add(exitItem);
    }

    /// <summary>Exports the active tab's currently-shown rows (respecting the grid filter) to a CSV —
    /// machine · online · state · updates · reboot · error · OS · schedule — for a maintenance-window
    /// write-up. Writes UTF-8 with a BOM so Excel opens it cleanly.</summary>
    private void ExportTabCsv(WorkspaceViewModel vm)
    {
        if (vm.VisibleRowCount == 0)
        {
            Log?.Warn(null, "Export: nothing to export (no rows shown).");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export tab to CSV",
            Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = $"{SanitizeFileName(vm.Title)}-report.csv",
            DefaultExt = ".csv",
            AddExtension = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, vm.BuildReportCsv(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Log?.Info(null, $"Exported {vm.VisibleRowCount} row(s) to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            Log?.Error(null, $"Export failed: {ex.Message}");
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(name) ? "tab" : name;
    }

    private static void AddListItems(MenuItem parent, IReadOnlyList<string> lists, Action<string> onClick)
    {
        if (lists.Count == 0)
        {
            parent.Items.Add(new MenuItem { Header = "(no saved lists)", IsEnabled = false });
            return;
        }

        foreach (string name in lists)
        {
            string captured = name;
            var item = new MenuItem { Header = captured };
            item.Click += (_, _) => onClick(captured);
            parent.Items.Add(item);
        }
    }

    private void SaveCurrentAsList(WorkspaceViewModel vm)
    {
        var dialog = new TextPromptWindow("Save list", "Save this tab's machines as a list named:") { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Value is { } name)
        {
            vm.SaveCurrentAsList(name);
        }
    }

    private async void ConfirmDeleteList(WorkspaceViewModel vm, string name)
    {
        var confirm = new MessageBox
        {
            Title = "Delete list",
            Content = $"Delete the saved list '{name}'? (Loaded machines stay in the tab.)",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
        };
        if (await confirm.ShowDialogAsync() == MessageBoxResult.Primary)
        {
            vm.DeleteList(name);
        }
    }
}
