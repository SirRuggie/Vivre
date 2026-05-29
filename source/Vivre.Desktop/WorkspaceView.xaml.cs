using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Vivre.Core.Models;
using Vivre.Core.Sccm;
using Vivre.Core.Scripts;
using Vivre.Desktop.ViewModels;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace Vivre.Desktop;

/// <summary>
/// The per-tab content: the command bar, add-computers bar, and the computer grid.
/// Its DataContext is the tab's <see cref="WorkspaceViewModel"/> (set by the shell's
/// TabControl). Code-behind is the grid glue WPF can't do in XAML — selection sync,
/// the right-click action menu, double-click to run, Delete/Copy, quick-add.
/// </summary>
public partial class WorkspaceView : UserControl
{
    public WorkspaceView()
    {
        InitializeComponent();
    }

    private WorkspaceViewModel? ViewModel => DataContext as WorkspaceViewModel;

    private Window? OwnerWindow => Window.GetWindow(this);

    // --- right-click action menu (built lazily; DataContext isn't set in the ctor) ---

    private void BuildContextMenu(WorkspaceViewModel vm)
    {
        GridMenu.Items.Clear();

        // Copy ▸ submenu — names / command results for the selected rows, the full TSV, or every
        // online / offline device. All land on the clipboard newline-separated (paste into Excel).
        bool hasSelection = vm.SelectedComputers.Count > 0;
        var copy = new MenuItem { Header = "Copy" };

        var copyNames = new MenuItem { Header = "Name(s)", IsEnabled = hasSelection };
        copyNames.Click += (_, _) => CopyLines(vm.SelectedComputers.Select(c => c.Name));
        copy.Items.Add(copyNames);

        var copyResults = new MenuItem { Header = "Command result(s)", IsEnabled = hasSelection };
        copyResults.Click += (_, _) => CopyLines(vm.SelectedComputers.Select(c => c.CommandResult));
        copy.Items.Add(copyResults);

        var copyRows = new MenuItem { Header = "Selected rows (all columns)", IsEnabled = hasSelection };
        copyRows.Click += (_, _) => CopySelectedRows();
        copy.Items.Add(copyRows);

        copy.Items.Add(new Separator());

        var copyOnline = new MenuItem { Header = "All online devices", IsEnabled = vm.OnlineNames.Count > 0 };
        copyOnline.Click += (_, _) => CopyLines(vm.OnlineNames);
        copy.Items.Add(copyOnline);

        var copyOffline = new MenuItem { Header = "All offline devices", IsEnabled = vm.OfflineNames.Count > 0 };
        copyOffline.Click += (_, _) => CopyLines(vm.OfflineNames);
        copy.Items.Add(copyOffline);

        GridMenu.Items.Add(copy);

        GridMenu.Items.Add(new Separator());

        var runSelected = new MenuItem { Header = "Run PowerShell Script" };
        runSelected.Click += (_, _) => OpenScriptRunner([.. vm.SelectedComputers]);
        GridMenu.Items.Add(runSelected);

        var runAll = new MenuItem { Header = "Run PowerShell (All Machines)" };
        runAll.Click += (_, _) => OpenScriptRunner([.. vm.Computers]);
        GridMenu.Items.Add(runAll);

        GridMenu.Items.Add(BuildScriptMenu(vm));

        GridMenu.Items.Add(new Separator());

        foreach (ScheduleAction action in vm.ClientActions)
        {
            GridMenu.Items.Add(new MenuItem
            {
                Header = action.Label,
                Command = vm.TriggerScheduleCommand,
                CommandParameter = action,
            });
        }

        GridMenu.Items.Add(new Separator());

        var enableWinRm = new MenuItem { Header = "Enable WinRM (PSRemoting)…" };
        enableWinRm.Click += OnEnableWinRm;
        GridMenu.Items.Add(enableWinRm);
    }

    /// <summary>
    /// The "Run script ▸" cascading menu, mirroring the script library's category folders.
    /// Picking a leaf opens the Run Script window pre-loaded with that script and targeted at
    /// the current selection — so you review and hit Run (no accidental bulk reboot from a click).
    /// </summary>
    private MenuItem BuildScriptMenu(WorkspaceViewModel vm)
    {
        var root = new MenuItem { Header = "Run script" };

        IReadOnlyList<ScriptFile> scripts = SafeListScripts(vm);
        if (scripts.Count == 0)
        {
            root.Items.Add(new MenuItem { Header = "(no saved scripts)", IsEnabled = false });
            return root;
        }

        // Category sub-menus first (alphabetical), then any root-level scripts.
        foreach (IGrouping<string, ScriptFile> group in scripts
                     .Where(s => !string.IsNullOrEmpty(s.Category))
                     .GroupBy(s => s.Category)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var category = new MenuItem { Header = group.Key };
            foreach (ScriptFile script in group)
            {
                category.Items.Add(ScriptMenuItem(vm, script));
            }

            root.Items.Add(category);
        }

        List<ScriptFile> loose = [.. scripts.Where(s => string.IsNullOrEmpty(s.Category))];
        if (loose.Count > 0)
        {
            if (root.Items.Count > 0)
            {
                root.Items.Add(new Separator());
            }

            foreach (ScriptFile script in loose)
            {
                root.Items.Add(ScriptMenuItem(vm, script));
            }
        }

        return root;
    }

    private MenuItem ScriptMenuItem(WorkspaceViewModel vm, ScriptFile script)
    {
        var item = new MenuItem { Header = script.Name };
        item.Click += (_, _) => OpenScriptRunner([.. vm.SelectedComputers], script);
        return item;
    }

    private static IReadOnlyList<ScriptFile> SafeListScripts(WorkspaceViewModel vm)
    {
        try
        {
            return vm.ScriptLibrary.List();
        }
        catch
        {
            return [];
        }
    }

    private void OpenScriptRunner(IReadOnlyList<Computer> targets, ScriptFile? initialScript = null)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        IReadOnlyList<Computer> resolved = targets.Count > 0 ? targets : [.. vm.Computers];
        new ScriptRunnerWindow(resolved, vm.Credentials, vm.Activity, vm.ScriptLibrary, initialScript) { Owner = OwnerWindow }.Show();
    }

    /// <summary>Copies the values one-per-line to the clipboard (Excel-friendly), skipping blanks.</summary>
    private static void CopyLines(IEnumerable<string?> values)
    {
        string text = string.Join(Environment.NewLine, values.Where(v => !string.IsNullOrEmpty(v)));
        if (text.Length > 0)
        {
            Clipboard.SetText(text);
        }
    }

    private void CopySelectedRows()
    {
        if (ViewModel is not { SelectedComputers.Count: > 0 } vm)
        {
            return;
        }

        var text = new StringBuilder();
        foreach (Computer c in vm.SelectedComputers)
        {
            text.AppendLine(string.Join('\t',
                c.Name,
                c.IsOnline switch { true => "Online", false => "Offline", _ => "Unknown" },
                c.SiteCode,
                c.AgentVersion,
                c.LastStatus,
                c.LastError,
                c.CommandResult));
        }

        Clipboard.SetText(text.ToString());
    }

    private async void OnEnableWinRm(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { SelectedComputers.Count: > 0 } vm)
        {
            return;
        }

        var confirm = new MessageBox
        {
            Title = "Enable WinRM",
            Content = $"Enable PowerShell Remoting on {vm.SelectedComputers.Count} machine(s)?\n\n"
                      + "This runs 'Enable-PSRemoting -Force' on each target over DCOM, "
                      + "and changes the target's configuration.",
            PrimaryButtonText = "Enable",
            CloseButtonText = "Cancel",
        };

        if (await confirm.ShowDialogAsync() == MessageBoxResult.Primary)
        {
            await vm.EnableWinRmSelectedAsync();
        }
    }

    // --- grid interactions ---

    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is { } vm && sender is DataGrid grid)
        {
            vm.SetSelection(grid.SelectedItems.OfType<Computer>());
        }
    }

    private void OnGridRightClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        DataGridRow? row = FindParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row is null)
        {
            return; // right-clicked a header / empty area
        }

        if (!row.IsSelected)
        {
            ComputerGrid.UnselectAll();
            row.IsSelected = true;
        }

        BuildContextMenu(vm);
        GridMenu.PlacementTarget = row;
        GridMenu.IsOpen = true;
        e.Handled = true;
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        DataGridRow? row = FindParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is Computer computer)
        {
            OpenScriptRunner([computer]); // double-click → script window for that one machine
        }
    }

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && ViewModel is { } vm)
        {
            vm.RemoveSelected();
            e.Handled = true;
        }
    }

    private static T? FindParent<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null and not T)
        {
            element = VisualTreeHelper.GetParent(element);
        }

        return element as T;
    }
}
