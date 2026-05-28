using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Vivre.Desktop.ViewModels;
using Wpf.Ui.Appearance;
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
    public MainWindow()
    {
        InitializeComponent();
        // DataContext is assigned by the composition root (App) after construction,
        // so build the File menu once it's available.
        Loaded += (_, _) =>
        {
            BuildFileMenu();
            StartRelativeTimeRefresh();
        };
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

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

    // --- theme (app-wide) ---

    private void OnThemeLight(object sender, RoutedEventArgs e) => ApplicationThemeManager.Apply(ApplicationTheme.Light);

    private void OnThemeDark(object sender, RoutedEventArgs e) => ApplicationThemeManager.Apply(ApplicationTheme.Dark);

    private void OnThemeSystem(object sender, RoutedEventArgs e) => ApplicationThemeManager.ApplySystemTheme();

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        if (Shell is { } shell)
        {
            new SettingsWindow(shell.Credentials) { Owner = this }.ShowDialog();
        }
    }

    private void OnOpenAbout(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();

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

    // --- tabs ---

    private void OnNewTab(object sender, RoutedEventArgs e) => Shell?.NewTabCommand.Execute(null);

    private void OnCloseTab(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WorkspaceViewModel workspace })
        {
            Shell?.CloseTabCommand.Execute(workspace);
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

        var newTab = new MenuItem { Header = "_New tab" };
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

        var exitItem = new MenuItem { Header = "E_xit" };
        exitItem.Click += (_, _) => Close();
        FileMenu.Items.Add(exitItem);
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
