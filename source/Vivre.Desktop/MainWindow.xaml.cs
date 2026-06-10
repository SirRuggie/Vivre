using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
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
/// The shell window: menu bar, NavigationView pane (Computers / Settings), and the keep-alive content
/// host that holds the workspace and the Settings page side by side (Visibility-toggled). Per-tab grid
/// behaviour lives in <see cref="WorkspaceView"/>; this code-behind handles app-level concerns.
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

    /// <summary>Persisted preferences — injected by the composition root.</summary>
    internal AppSettingsStore? Settings { get; set; }

    /// <summary>Activity log for surfacing settings-save failures — injected by the composition root.</summary>
    internal Core.Logging.IActivityLog? Log { get; set; }

    /// <summary>The theme applied at startup — used to tick the right Theme radio on the Settings page.</summary>
    internal string SavedTheme { get; set; } = "Dark";

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        BuildFileMenu();
        StartRelativeTimeRefresh();
        HookOperationToasts();
        HookBottomDock();
        UpdateStatusBarVisibility();

        // Initialize the Settings page (injected dependencies).
        if (Settings is { } store && Log is { } log && Shell is { } shell)
        {
            SettingsSection.Initialize(store, log, shell.Credentials, this);
            SettingsSection.UpdateThemeChecks(SavedTheme);
        }

        // Show the Computers section and set the nav highlight on startup.
        ShowNavSection(computers: true);

        // Restore persisted pane open/close state.
        try
        {
            if (Settings?.Load() is { NavPaneOpen: true })
            {
                NavView.IsPaneOpen = true;
                ContentHost.Margin = new Thickness(NavView.OpenPaneLength, 0, 0, 0);
            }
        }
        catch { /* settings read failed; leave default closed */ }
    }

    // --- NavigationView pane ---

    /// <summary>Click handler on the Computers nav item — drives the section toggle.</summary>
    private void OnComputersNavClick(object sender, RoutedEventArgs e) => ShowNavSection(computers: true);

    /// <summary>Click handler on the Settings footer item — drives the section toggle.</summary>
    private void OnSettingsNavClick(object sender, RoutedEventArgs e) => ShowNavSection(computers: false);

    /// <summary>NavigationView.ItemInvoked fires on a click/tap in BOTH compact (icons-only, pane closed)
    /// and expanded states — unlike NavigationViewItem.Click, which only routes when the pane is open. This
    /// is the reliable hook for the collapsed-by-default pane. Walk up from the clicked element to the
    /// NavigationViewItem to learn which one was invoked.</summary>
    private void OnNavItemInvoked(NavigationView sender, RoutedEventArgs e)
    {
        DependencyObject? d = e.OriginalSource as DependencyObject;
        while (d is not null and not NavigationViewItem)
        {
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }

        if (d is NavigationViewItem item)
        {
            ShowNavSection(computers: ReferenceEquals(item, ComputersNavItem));
        }
    }

    /// <summary>
    /// Toggles which section is visible. Neither Navigate() nor ReplaceContent() is ever called,
    /// so both sections stay in the visual tree. This is the keep-alive mechanism:
    /// ComputersSection (TabControlEx + all workspace chrome) is never rebuilt on a nav switch.
    /// </summary>
    private void ShowNavSection(bool computers)
    {
        if (ComputersSection is null || SettingsSection is null) return;
        ComputersSection.Visibility = computers ? Visibility.Visible : Visibility.Collapsed;
        SettingsSection.Visibility  = computers ? Visibility.Collapsed : Visibility.Visible;
        ComputersNavItem.IsActive = computers;
        SettingsNavItem.IsActive  = !computers;
        UpdateStatusBarVisibility();
    }

    /// <summary>
    /// Keeps the full-width status bar visible only when the Computers section is shown AND
    /// the active tab is a workspace tab. Hides it on the Settings page and on the RDP tab.
    /// </summary>
    private void UpdateStatusBarVisibility()
    {
        if (StatusBar is null) return;
        bool computersActive = ComputersSection?.Visibility == Visibility.Visible;
        bool workspace = Shell?.IsWorkspaceTab == true;
        StatusBar.Visibility = (computersActive && workspace) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNavPaneOpened(NavigationView sender, RoutedEventArgs e)
    {
        // Guard: ContentHost may not exist yet if this fires during InitializeComponent.
        if (ContentHost is null) return;
        // Shift ContentHost right to reveal the expanded pane label+icon strip.
        ContentHost.Margin = new Thickness(sender.OpenPaneLength, 0, 0, 0);
        SaveNavPaneState(true);
    }

    private void OnNavPaneClosed(NavigationView sender, RoutedEventArgs e)
    {
        // Guard: ContentHost may not exist yet if this fires during InitializeComponent.
        if (ContentHost is null) return;
        // Shift ContentHost left to expose only the compact icon strip.
        ContentHost.Margin = new Thickness(sender.CompactPaneLength, 0, 0, 0);
        SaveNavPaneState(false);
    }

    private void SaveNavPaneState(bool open)
    {
        if (Settings is null) return;
        try
        {
            AppSettings s = Settings.Load();
            s.NavPaneOpen = open;
            Settings.Save(s);
        }
        catch (Exception ex)
        {
            Log?.Warn(null, $"Couldn't save nav pane state. {ex.Message}");
        }
    }

    // --- public helpers called from SettingsPage ---

    /// <summary>Opens the Columns window acting on the currently-active workspace tab.</summary>
    public void OpenColumnsWindow()
    {
        // Columns window needs a WorkspaceViewModel and the current builtin column headers.
        // It's opened from WorkspaceView; here we delegate to the active tab's WorkspaceView.
        // The simplest path: find the active tab's WorkspaceView in the visual tree and invoke its columns action.
        if (Shell?.SelectedTab is not WorkspaceViewModel vm) return;

        // WorkspaceView exposes OpenColumnsWindow as an internal helper.
        if (FindActiveWorkspaceView() is { } wsv)
        {
            wsv.OpenColumnsWindowFromShell();
        }
    }

    private WorkspaceView? FindActiveWorkspaceView()
    {
        // Walk the PART_ItemsHolder children to find the visible WorkspaceView.
        if (WorkspaceTabs.Template.FindName("PART_ItemsHolder", WorkspaceTabs) is System.Windows.Controls.Panel holder)
        {
            foreach (ContentPresenter cp in holder.Children.OfType<ContentPresenter>())
            {
                if (cp.Visibility == Visibility.Visible)
                {
                    return cp.ContentTemplate?.FindName("__root__", cp) as WorkspaceView
                           ?? FindVisualChild<WorkspaceView>(cp);
                }
            }
        }
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            if (FindVisualChild<T>(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>Opens the help window (or re-activates the open one). Called by SettingsPage and F1.</summary>
    public void ShowHelpPublic() => ShowHelp();

    // --- completion toast (tray balloon when a Scan/Install/Uninstall finishes) ---

    private void HookOperationToasts()
    {
        if (Shell is not { } shell) return;

        foreach (WorkspaceViewModel tab in shell.Tabs.OfType<WorkspaceViewModel>())
        {
            tab.OperationCompleted += OnOperationCompleted;
        }

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

    private void OnOperationCompleted(string summary, ViewModels.OperationSeverity severity)
    {
        if (IsActive)
        {
            Dispatcher.BeginInvoke(() => ShowCompletionBar(summary, severity));
            return;
        }

        Dispatcher.BeginInvoke(() => TrayIcon.ShowBalloonTip("Vivre", summary, Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info));
    }

    private void ShowCompletionBar(string summary, ViewModels.OperationSeverity severity)
    {
        CompletionBar.Message = summary;
        CompletionBar.Severity = severity switch
        {
            ViewModels.OperationSeverity.Error => InfoBarSeverity.Error,
            ViewModels.OperationSeverity.Warning => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Success,
        };
        CompletionBar.IsOpen = true;

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

    // --- keep relative times current ---

    private DispatcherTimer? _relativeTimeTimer;

    private void StartRelativeTimeRefresh()
    {
        _relativeTimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _relativeTimeTimer.Tick += OnRefreshRelativeTimes;
        _relativeTimeTimer.Start();
    }

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

        SubscribeToSelectedTab(null);

        foreach (IDisposable tab in Shell?.Tabs.OfType<IDisposable>().ToArray() ?? [])
        {
            tab.Dispose();
        }

        TrayIcon?.Dispose();
    }

    private void OnRefreshRelativeTimes(object? sender, EventArgs e)
    {
        if (Shell is not { } shell) return;

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

    // --- unified bottom dock (Activity + per-machine Updates) ---

    private GridLength _activityHeight = new(170);
    private WorkspaceViewModel? _observedTab;

    private void HookBottomDock()
    {
        if (Shell is not { } shell) return;

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
            UpdateStatusBarVisibility();
        }
    }

    private void SubscribeToSelectedTab(WorkspaceViewModel? tab)
    {
        if (ReferenceEquals(_observedTab, tab)) return;

        if (_observedTab is not null)
        {
            _observedTab.PropertyChanged -= OnSelectedTabPropertyChanged;
        }

        _observedTab = tab;

        if (_observedTab is not null)
        {
            _observedTab.PropertyChanged += OnSelectedTabPropertyChanged;
        }

        FleetProgressBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
        FleetProgressBar.Value = _observedTab?.FleetProgress ?? 0;
    }

    private void OnSelectedTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceViewModel.FocusedComputer) or nameof(WorkspaceViewModel.IsUpdateMode))
        {
            RecomputeBottomDock();

            if (e.PropertyName == nameof(WorkspaceViewModel.FocusedComputer) && UpdatesTriggerActive)
            {
                BottomDockTabs.SelectedItem = UpdatesTab;
            }
        }

        if (e.PropertyName == nameof(WorkspaceViewModel.FleetProgress)
            && sender is WorkspaceViewModel vm2)
        {
            var anim = new DoubleAnimation(
                vm2.FleetProgress,
                new Duration(TimeSpan.FromMilliseconds(300)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            FleetProgressBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, anim);
        }
    }

    private bool UpdatesTriggerActive =>
        Shell?.SelectedTab is WorkspaceViewModel { IsUpdateMode: true, FocusedComputer: not null };

    private void RecomputeBottomDock()
    {
        bool updates = UpdatesTriggerActive;
        bool activity = ActivityLogMenuItem.IsChecked;

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

        ApplyUpdateFilter((Shell?.SelectedTab as WorkspaceViewModel)?.FocusedComputer);
    }

    private void ShowDock()
    {
        ActivityRow.Height = _activityHeight;
        SplitterRow.Height = GridLength.Auto;
        ActivitySplitter.Visibility = Visibility.Visible;
        ActivityPanel.Visibility = Visibility.Visible;
        ActivityPanel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(300))));
    }

    private void HideDock()
    {
        if (ActivityRow.ActualHeight > 0)
        {
            _activityHeight = new GridLength(ActivityRow.ActualHeight);
        }

        ActivitySplitter.Visibility = Visibility.Collapsed;
        ActivityPanel.Visibility = Visibility.Collapsed;
        ActivityPanel.Opacity = 0;
        SplitterRow.Height = new GridLength(0);
        ActivityRow.Height = new GridLength(0);
    }

    private void OnToggleActivityLog(object sender, RoutedEventArgs e)
    {
        if (ActivityLogMenuItem.IsChecked)
        {
            BottomDockTabs.SelectedItem = ActivityTab;
        }

        RecomputeBottomDock();
    }

    private void OnCloseBottomDock(object sender, RoutedEventArgs e)
    {
        ActivityLogMenuItem.IsChecked = false;
        if (Shell?.SelectedTab is WorkspaceViewModel vm)
        {
            vm.FocusedComputer = null;
        }

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

    // --- per-machine update-list filter ---

    private string _updateFilter = string.Empty;

    private void OnUpdateFilterChanged(object sender, TextChangedEventArgs e)
    {
        _updateFilter = (sender as System.Windows.Controls.TextBox)?.Text?.Trim() ?? string.Empty;
        ApplyUpdateFilter((Shell?.SelectedTab as WorkspaceViewModel)?.FocusedComputer);
    }

    private void ApplyUpdateFilter(Computer? focused)
    {
        if (focused is null) return;

        ApplyUpdateFilterTo(focused.ApplicableUpdates);
        ApplyUpdateFilterTo(focused.InstalledUpdates);
    }

    private void ApplyUpdateFilterTo(System.Collections.IEnumerable collection)
    {
        ICollectionView? view = System.Windows.Data.CollectionViewSource.GetDefaultView(collection);
        if (view is null) return;

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

    private async void OnUninstallChecked(object sender, RoutedEventArgs e)
    {
        if (Shell?.SelectedTab is not WorkspaceViewModel { FocusedComputer: { } c } vm) return;

        int count = c.InstalledUpdates.Count(u => u.IsSelected && u.IsUninstallable);
        if (count == 0) return;

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

    // --- activity-log copy ---

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
            catch { /* clipboard transiently locked */ }
        }
    }

    // --- View mode + update source ---

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

    private void OnOpenCrossDomainRdp(object sender, RoutedEventArgs e) => Shell?.OpenCrossDomainRdpCommand.Execute(null);

    private void OnHelpKey(object sender, ExecutedRoutedEventArgs e) => ShowHelp();

    private void ShowHelp()
    {
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
        if (Shell?.SelectedTab is not WorkspaceViewModel vm) return;

        var dialog = new TextPromptWindow(
            "Exclude updates",
            "Comma-separated update title terms to skip (e.g. SQL, Silverlight, Edge):",
            vm.ExcludeText) { Owner = this };

        if (dialog.ShowDialog() == true && dialog.Value is { } text)
        {
            vm.ExcludeText = text;
        }
    }

    // --- toolbar Install ---

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (Shell?.SelectedTab is not WorkspaceViewModel vm) return;

        bool hasSelection = vm.SelectedComputers.Count > 0;
        IReadOnlyList<Computer> targets = hasSelection ? [.. vm.SelectedComputers] : [.. vm.Computers];
        int count = targets.Count;
        if (count == 0 || !vm.InstallTargetCommand.CanExecute(null)) return;

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
                return;
            }

            if (choice != MessageBoxResult.Secondary) return;

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

    // --- keyboard accelerator handlers ---

    private void OnNewTabKey(object sender, ExecutedRoutedEventArgs e) => Shell?.NewTabCommand.Execute(null);

    private void OnCloseTabKey(object sender, ExecutedRoutedEventArgs e)
    {
        if (Shell?.SelectedTab is { } tab) CloseTabWithGuard(tab);
    }

    private void OnFocusAddKey(object sender, ExecutedRoutedEventArgs e)
    {
        // Only meaningful when on the Computers section.
        if (ComputersSection.Visibility == Visibility.Visible)
        {
            QuickAddBox.Focus();
            QuickAddBox.SelectAll();
        }
    }

    private void OnRenameTabKey(object sender, ExecutedRoutedEventArgs e)
    {
        if (Shell?.SelectedTab is WorkspaceViewModel tab) RenameTab(tab);
    }

    private void OnToggleModeKey(object sender, ExecutedRoutedEventArgs e) =>
        (Shell?.SelectedTab as WorkspaceViewModel)?.ToggleUpdateModeCommand.Execute(null);

    private void OnRefreshKey(object sender, ExecutedRoutedEventArgs e)
    {
        var cmd = (Shell?.SelectedTab as WorkspaceViewModel)?.PingAllCommand;
        if (cmd?.CanExecute(null) == true) cmd.Execute(null);
    }

    private void OnInstallKey(object sender, ExecutedRoutedEventArgs e)
    {
        if (Shell?.SelectedTab is WorkspaceViewModel { CanShowInstallToolbar: true })
        {
            OnInstallClick(sender, e);
        }
    }

    // --- tabs ---

    private void OnNewTab(object sender, RoutedEventArgs e) => Shell?.NewTabCommand.Execute(null);

    private void OnCloseTab(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ITabViewModel tab }) CloseTabWithGuard(tab);
    }

    private async void CloseTabWithGuard(ITabViewModel tab)
    {
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
            if (await confirm.ShowDialogAsync() != MessageBoxResult.Primary) return;
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
            if (index >= 0) CloseTabs([.. shell.Tabs.Skip(index + 1)]);
        }
    }

    private async void CloseTabs(IReadOnlyList<ITabViewModel> tabs)
    {
        if (Shell is not { } shell || tabs.Count == 0) return;

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

            if (await confirm.ShowDialogAsync() != MessageBoxResult.Primary) return;
        }

        foreach (ITabViewModel tab in tabs) shell.CloseTabCommand.Execute(tab);
    }

    private void OnTabHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: WorkspaceViewModel workspace })
        {
            RenameTab(workspace);
        }
    }

    private void OnTabHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle
            && sender is FrameworkElement { DataContext: ITabViewModel tab })
        {
            CloseTabWithGuard(tab);
            e.Handled = true;
        }
    }

    private void OnRenameTab(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WorkspaceViewModel workspace }) RenameTab(workspace);
    }

    private void RenameTab(WorkspaceViewModel workspace)
    {
        var dialog = new TextPromptWindow("Rename tab", "Tab name:", workspace.Title) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Value is { } name) workspace.Title = name;
    }

    // --- shared add-computers bar ---

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
        if (Shell?.SelectedTab is not WorkspaceViewModel vm) return;

        string[] names = QuickAddBox.Text.Split(
            [',', ';', ' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (names.Length > 0)
        {
            vm.AddComputers(names);
            QuickAddBox.Text = string.Empty;
        }
    }

    // --- File menu ---

    private void OnFileMenuOpened(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, FileMenu)) BuildFileMenu();
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
        saveItem.Click += (_, _) => { if (vm is not null) SaveCurrentAsList(vm); };
        FileMenu.Items.Add(saveItem);

        var deleteMenu = new MenuItem { Header = "_Delete list" };
        AddListItems(deleteMenu, lists, name => { if (vm is not null) ConfirmDeleteList(vm, name); });
        FileMenu.Items.Add(deleteMenu);

        FileMenu.Items.Add(new Separator());

        var pasteItem = new MenuItem { Header = "_Paste computers…", IsEnabled = vm is not null };
        pasteItem.Click += (_, _) => { if (vm is not null) new LoadComputersWindow(vm) { Owner = this }.ShowDialog(); };
        FileMenu.Items.Add(pasteItem);

        FileMenu.Items.Add(new Separator());

        var exportItem = new MenuItem { Header = "_Export to CSV…", IsEnabled = vm is { HasComputers: true } };
        exportItem.Click += (_, _) => { if (vm is not null) ExportTabCsv(vm); };
        FileMenu.Items.Add(exportItem);

        FileMenu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = "E_xit" };
        exitItem.Click += (_, _) => Close();
        FileMenu.Items.Add(exitItem);
    }

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

        if (dialog.ShowDialog(this) != true) return;

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
        if (dialog.ShowDialog() == true && dialog.Value is { } name) vm.SaveCurrentAsList(name);
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
        if (await confirm.ShowDialogAsync() == MessageBoxResult.Primary) vm.DeleteList(name);
    }
}
