using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
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
            UpdateThemeChecks(SavedTheme);
        };
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    // --- completion toast (tray balloon when a Scan/Install/Uninstall finishes) ---

    private void HookOperationToasts()
    {
        if (Shell is not { } shell)
        {
            return;
        }

        foreach (WorkspaceViewModel tab in shell.Tabs)
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
            Dispatcher.Invoke(() => ShowCompletionBar(summary));
            return;
        }

        Dispatcher.Invoke(() => TrayIcon.ShowBalloonTip("Vivre", summary, Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info));
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

    private void StartRelativeTimeRefresh()
    {
        // LastRebootDisplay is relative to DateTime.Now, so it drifts; tick every minute to
        // re-evaluate it on every row. One app-level timer (rooted by the dispatcher while running).
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timer.Tick += OnRefreshRelativeTimes;
        timer.Start();
    }

    private void OnRefreshRelativeTimes(object? sender, EventArgs e)
    {
        if (Shell is not { } shell)
        {
            return;
        }

        foreach (var computer in shell.Tabs.SelectMany(tab => tab.Computers))
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

    // --- View menu: activity log panel (hidden by default) ---

    private GridLength _activityHeight = new(170);

    private void OnToggleActivityLog(object sender, RoutedEventArgs e)
    {
        bool show = ActivityLogMenuItem.IsChecked;
        if (show)
        {
            ActivityRow.Height = _activityHeight;
            SplitterRow.Height = GridLength.Auto;
            ActivitySplitter.Visibility = Visibility.Visible;
            ActivityPanel.Visibility = Visibility.Visible;
        }
        else
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
    }

    /// <summary>Opens the activity-log panel filtered to one machine (right-click "Show messages").</summary>
    public void ShowActivityForMachine(string machine)
    {
        if (Shell is { } shell)
        {
            shell.ActivityLog.SearchText = machine;
        }

        if (!ActivityLogMenuItem.IsChecked)
        {
            ActivityLogMenuItem.IsChecked = true;
            OnToggleActivityLog(ActivityLogMenuItem, new RoutedEventArgs());
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
            Settings?.Save(new AppSettings { Theme = theme });
        }
        catch (Exception ex)
        {
            Log?.Warn(null, $"Couldn't save theme preference. {ex.Message}");
        }
    }

    /// <summary>Ticks the active theme in the menu (the three items are a manual radio group).</summary>
    private void UpdateThemeChecks(string theme)
    {
        if (LightThemeItem is null)
        {
            return;
        }

        LightThemeItem.IsChecked = theme == "Light";
        DarkThemeItem.IsChecked = theme == "Dark";
        SystemThemeItem.IsChecked = theme == "System";
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
        if (Shell?.SelectedTab is not { } vm)
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
        if (Shell?.SelectedTab is not { } vm)
        {
            return;
        }

        // Mirror InstallTarget's scope: the selected rows, or every row when nothing is selected.
        bool hasSelection = vm.SelectedComputers.Count > 0;
        int count = hasSelection ? vm.SelectedComputers.Count : vm.Computers.Count;
        if (count == 0 || !vm.InstallTargetCommand.CanExecute(null))
        {
            return; // nothing to target / a sweep is already running
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
        if (Shell?.SelectedTab is { } tab)
        {
            RenameTab(tab);
        }
    }

    private void OnToggleModeKey(object sender, ExecutedRoutedEventArgs e) =>
        Shell?.SelectedTab?.ToggleUpdateModeCommand.Execute(null);

    private void OnRefreshKey(object sender, ExecutedRoutedEventArgs e)
    {
        var cmd = Shell?.SelectedTab?.PingAllCommand;
        if (cmd?.CanExecute(null) == true)
        {
            cmd.Execute(null);
        }
    }

    private void OnInstallKey(object sender, ExecutedRoutedEventArgs e)
    {
        // Ctrl+Enter installs — only where the toolbar Install button is shown (Update / Applicable scope).
        if (Shell?.SelectedTab is { CanShowInstallToolbar: true })
        {
            OnInstallClick(sender, e);
        }
    }

    // --- tabs ---

    private void OnNewTab(object sender, RoutedEventArgs e) => Shell?.NewTabCommand.Execute(null);

    private void OnCloseTab(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WorkspaceViewModel workspace })
        {
            CloseTabWithGuard(workspace);
        }
    }

    /// <summary>Closes a tab — but confirms first when it holds work (loaded machines or a live
    /// monitor/sweep) so a curated list or running op isn't lost on a stray click. Empty, idle tabs
    /// close instantly so the guard never habituates.</summary>
    private async void CloseTabWithGuard(WorkspaceViewModel workspace)
    {
        if (workspace.HasWork)
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

        Shell?.CloseTabCommand.Execute(workspace);
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
        if (Shell?.SelectedTab is { } vm)
        {
            new LoadComputersWindow(vm) { Owner = this }.ShowDialog();
        }
    }

    private void AddFromQuickBox()
    {
        if (Shell?.SelectedTab is not { } vm)
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

        WorkspaceViewModel? vm = Shell?.SelectedTab;

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
