using System.ComponentModel;
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
    /// <summary>Default side-panel width when a machine is first focused; the user's drag is
    /// preserved across machine switches via <see cref="_lastFocused"/>.</summary>
    private static readonly GridLength ChecklistOpenWidth = new(280);

    /// <summary>Tracks the previous focused machine so the column width only resets when
    /// transitioning from "no machine" to "a machine" — not when switching between machines,
    /// where the user's splitter drag should be kept.</summary>
    private Computer? _lastFocused;

    public WorkspaceView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is WorkspaceViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is WorkspaceViewModel newVm)
        {
            newVm.PropertyChanged += OnViewModelPropertyChanged;
            UpdateChecklistColumnWidth(newVm.FocusedComputer);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.FocusedComputer) && sender is WorkspaceViewModel vm)
        {
            UpdateChecklistColumnWidth(vm.FocusedComputer);
        }
    }

    private void UpdateChecklistColumnWidth(Computer? focused)
    {
        if (ChecklistColumn is null)
        {
            return;
        }

        if (focused is null)
        {
            ChecklistColumn.Width = new GridLength(0);
        }
        else if (_lastFocused is null)
        {
            // Transitioning from "panel closed" to "panel opening" — apply the default width.
            // Switching from one focused machine to another leaves the column alone so any
            // splitter drag the user made is preserved.
            ChecklistColumn.Width = ChecklistOpenWidth;
        }

        _lastFocused = focused;
    }

    /// <summary>
    /// Close (✕) on the checklist panel — clears <see cref="WorkspaceViewModel.FocusedComputer"/>
    /// so the auto-collapse listener takes the panel back to 0 width and the grid gets the full
    /// window back. The grid's selected row stays selected.
    /// </summary>
    private void OnCloseChecklist(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.FocusedComputer = null;
        }
    }

    /// <summary>
    /// Uninstall confirmation. Pops a small Wpf.Ui MessageBox before kicking the per-machine
    /// uninstall sweep — uninstalls are destructive enough to deserve a "yes, really" prompt,
    /// especially with multi-select. The VM only exposes the command; the dialog lives here so
    /// the VM doesn't pop UI directly.
    /// </summary>
    private async void OnUninstallChecked(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { FocusedComputer: { } c } vm)
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

    /// <summary>
    /// Left-click on a row in the Windows Update grid always reopens the checklist for that
    /// machine. Needed because <c>SelectionChanged</c> doesn't fire when the clicked row is
    /// already the selected row — so without this, closing the panel via the ✕ would strand
    /// the user on that machine until they clicked a different one first.
    /// </summary>
    private void OnUpdateGridLeftClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        DataGridRow? row = FindParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is Computer focused)
        {
            vm.FocusedComputer = focused;
        }
    }

    private WorkspaceViewModel? ViewModel => DataContext as WorkspaceViewModel;

    private Window? OwnerWindow => Window.GetWindow(this);

    // One menu instance, re-parented to whichever grid (Machines / Windows Update) was clicked.
    private readonly ContextMenu _gridMenu = new();

    // --- right-click action menu (built lazily; DataContext isn't set in the ctor) ---

    private void BuildContextMenu(WorkspaceViewModel vm)
    {
        _gridMenu.Items.Clear();

        Computer? firstSelected = vm.SelectedComputers.FirstOrDefault();

        var copy = new MenuItem { Header = "Copy" };

        var copyRows = new MenuItem { Header = "Selected rows" };
        copyRows.Click += (_, _) => CopySelectedRows();
        copy.Items.Add(copyRows);

        copy.Items.Add(new Separator());

        var copyUpdate = new MenuItem { Header = "Update message", IsEnabled = !string.IsNullOrEmpty(firstSelected?.UpdateMessage) };
        copyUpdate.Click += (_, _) => CopyField(static c => c.UpdateMessage);
        copy.Items.Add(copyUpdate);

        var copyReboot = new MenuItem { Header = "Reboot message", IsEnabled = !string.IsNullOrEmpty(firstSelected?.RebootMessage) };
        copyReboot.Click += (_, _) => CopyField(static c => c.RebootMessage);
        copy.Items.Add(copyReboot);

        var copyCmd = new MenuItem { Header = "Command result", IsEnabled = !string.IsNullOrEmpty(firstSelected?.CommandResult) };
        copyCmd.Click += (_, _) => CopyField(static c => c.CommandResult);
        copy.Items.Add(copyCmd);

        var copyErr = new MenuItem { Header = "Last error", IsEnabled = !string.IsNullOrEmpty(firstSelected?.LastError) };
        copyErr.Click += (_, _) => CopyField(static c => c.LastError);
        copy.Items.Add(copyErr);

        _gridMenu.Items.Add(copy);

        _gridMenu.Items.Add(new Separator());

        // In Windows Update mode, lead with the patch shortcuts (selection ⇒ those rows, else all).
        if (vm.IsUpdateMode)
        {
            var updates = new MenuItem { Header = "Updates" };

            var scan = new MenuItem { Header = "Scan selected" };
            scan.Click += (_, _) => _ = vm.ScanSelectedAsync([.. vm.SelectedComputers]);
            updates.Items.Add(scan);

            var install = new MenuItem { Header = "Install selected" };
            install.Click += (_, _) => _ = vm.InstallSelectedAsync([.. vm.SelectedComputers]);
            updates.Items.Add(install);

            _gridMenu.Items.Add(updates);
            _gridMenu.Items.Add(new Separator());
        }

        var runSelected = new MenuItem { Header = "Run PowerShell Script" };
        runSelected.Click += (_, _) => OpenScriptRunner([.. vm.SelectedComputers]);
        _gridMenu.Items.Add(runSelected);

        var runAll = new MenuItem { Header = "Run PowerShell (All Machines)" };
        runAll.Click += (_, _) => OpenScriptRunner([.. vm.Computers]);
        _gridMenu.Items.Add(runAll);

        _gridMenu.Items.Add(BuildScriptMenu(vm));

        _gridMenu.Items.Add(new Separator());

        foreach (ScheduleAction action in vm.ClientActions)
        {
            _gridMenu.Items.Add(new MenuItem
            {
                Header = action.Label,
                Command = vm.TriggerScheduleCommand,
                CommandParameter = action,
            });
        }

        _gridMenu.Items.Add(new Separator());

        var enableWinRm = new MenuItem { Header = "Enable WinRM (PSRemoting)…" };
        enableWinRm.Click += OnEnableWinRm;
        _gridMenu.Items.Add(enableWinRm);
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

    /// <summary>
    /// Copies one field across the selected rows (one row per line, blanks dropped). Used by the
    /// Copy submenu for the long-text columns so the user can grab the full message without the
    /// rest of the row noise — Ctrl+C / "Selected rows" still copies the whole row.
    /// </summary>
    private void CopyField(Func<Computer, string?> selector)
    {
        if (ViewModel is not { SelectedComputers.Count: > 0 } vm)
        {
            return;
        }

        string text = string.Join(
            Environment.NewLine,
            vm.SelectedComputers.Select(c => selector(c) ?? string.Empty).Where(static s => s.Length > 0));

        if (text.Length > 0)
        {
            Clipboard.SetText(text);
        }
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

            // Drive the Windows Update side panel from the primary (focused) machine row.
            if (grid.SelectedItem is Computer focused)
            {
                vm.FocusedComputer = focused;
            }
        }
    }

    private void OnGridRightClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not DataGrid grid)
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
            grid.UnselectAll();
            row.IsSelected = true;
        }

        BuildContextMenu(vm);
        // Re-parent the single menu to the grid that was clicked (Machines or Windows Update).
        grid.ContextMenu = _gridMenu;
        _gridMenu.PlacementTarget = row;
        _gridMenu.IsOpen = true;
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
