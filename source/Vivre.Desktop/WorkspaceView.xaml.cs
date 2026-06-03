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
    /// <summary>The row the user right-clicked — the anchor for the per-field Copy ▸ submenu, so it
    /// copies THIS row's field regardless of any (possibly stale, multi-row) grid selection. Set in
    /// <see cref="OnGridRightClick"/> immediately before the menu is built.</summary>
    private Computer? _contextRow;

    public WorkspaceView()
    {
        InitializeComponent();
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

        // Per-field Copy items act on the right-clicked row (_contextRow); the multi-row items
        // (Name(s) / Selected rows / online / offline) act on the selection.
        Computer? ctx = _contextRow;

        var copy = new MenuItem { Header = "Copy" };

        bool hasSelection = vm.SelectedComputers.Count > 0;
        var copyNames = new MenuItem { Header = "Name(s)", IsEnabled = hasSelection };
        copyNames.Click += (_, _) => CopyLines(vm.SelectedComputers.Select(c => c.Name));
        copy.Items.Add(copyNames);

        var copyRows = new MenuItem { Header = "Selected rows" };
        copyRows.Click += (_, _) => CopySelectedRows();
        copy.Items.Add(copyRows);

        copy.Items.Add(new Separator());

        var copyUpdate = new MenuItem { Header = "Update message", IsEnabled = !string.IsNullOrEmpty(ctx?.UpdateMessage) };
        copyUpdate.Click += (_, _) => CopyField(static c => c.UpdateMessage);
        copy.Items.Add(copyUpdate);

        var copyReboot = new MenuItem { Header = "Reboot message", IsEnabled = !string.IsNullOrEmpty(ctx?.RebootMessage) };
        copyReboot.Click += (_, _) => CopyField(static c => c.RebootMessage);
        copy.Items.Add(copyReboot);

        var copyCmd = new MenuItem { Header = "Command result", IsEnabled = !string.IsNullOrEmpty(ctx?.CommandResult) };
        copyCmd.Click += (_, _) => CopyField(static c => c.CommandResult);
        copy.Items.Add(copyCmd);

        var copyErr = new MenuItem { Header = "Last error", IsEnabled = !string.IsNullOrEmpty(ctx?.LastError) };
        copyErr.Click += (_, _) => CopyField(static c => c.LastError);
        copy.Items.Add(copyErr);

        copy.Items.Add(new Separator());

        var copyOnline = new MenuItem { Header = "All online devices", IsEnabled = vm.OnlineNames.Count > 0 };
        copyOnline.Click += (_, _) => CopyLines(vm.OnlineNames);
        copy.Items.Add(copyOnline);

        var copyOffline = new MenuItem { Header = "All offline devices", IsEnabled = vm.OfflineNames.Count > 0 };
        copyOffline.Click += (_, _) => CopyLines(vm.OfflineNames);
        copy.Items.Add(copyOffline);

        _gridMenu.Items.Add(copy);

        _gridMenu.Items.Add(new Separator());

        // Inspect this machine: full detail window, or just its activity-log messages.
        var details = new MenuItem { Header = "Details…" };
        details.Click += OnShowDetails;
        _gridMenu.Items.Add(details);

        var showMessages = new MenuItem { Header = "Show messages" };
        showMessages.Click += OnShowMessages;
        _gridMenu.Items.Add(showMessages);

        _gridMenu.Items.Add(new Separator());

        // In Windows Update mode, lead with the patch shortcuts (selection ⇒ those rows, else all).
        if (vm.IsUpdateMode)
        {
            var updates = new MenuItem { Header = "Updates" };

            // Only meaningful when at least one selected row isn't already mid-install/uninstall
            // (those rows are skipped anyway — disabling avoids a no-op click on an in-flight machine).
            bool anyActionable = vm.SelectedComputers.Any(c => !c.IsPatching);

            int selCount = vm.SelectedComputers.Count;
            var scan = new MenuItem { Header = $"Scan selected ({selCount})", IsEnabled = anyActionable };
            scan.Click += (_, _) => _ = vm.ScanSelectedAsync([.. vm.SelectedComputers]);
            updates.Items.Add(scan);

            var install = new MenuItem { Header = $"Install selected ({selCount})", IsEnabled = anyActionable };
            install.Click += (_, _) => _ = vm.InstallSelectedAsync([.. vm.SelectedComputers]);
            updates.Items.Add(install);

            _gridMenu.Items.Add(updates);
            _gridMenu.Items.Add(new Separator());
        }

        // Force-reboot the selected machines now — the most common Windows-Update follow-up.
        var rebootForce = new MenuItem { Header = "Reboot (force now)…", IsEnabled = hasSelection };
        rebootForce.Click += OnRebootForce;
        _gridMenu.Items.Add(rebootForce);

        // Timed actions: a one-time SYSTEM task that runs at a chosen time (works in either mode).
        var schedule = new MenuItem { Header = "Schedule", IsEnabled = hasSelection };
        var schedInstall = new MenuItem { Header = "Install updates…" };
        schedInstall.Click += OnScheduleInstall;
        schedule.Items.Add(schedInstall);
        var schedReboot = new MenuItem { Header = "Reboot…" };
        schedReboot.Click += OnScheduleReboot;
        schedule.Items.Add(schedReboot);
        schedule.Items.Add(new Separator());
        var schedCancel = new MenuItem { Header = "Cancel scheduled task" };
        schedCancel.Click += OnCancelScheduled;
        schedule.Items.Add(schedCancel);
        _gridMenu.Items.Add(schedule);

        // WhatsUp Gold maintenance mode (enter before patching / exit after) for the selection, else
        // the whole tab. Runs locally against the WUG server — prompts for the WUG login.
        var wugMaintenance = new MenuItem { Header = "WhatsUp Gold maintenance…" };
        wugMaintenance.Click += OnWugMaintenance;
        _gridMenu.Items.Add(wugMaintenance);

        _gridMenu.Items.Add(new Separator());

        // Run a saved (or pasted) script: opens the Run Script window, which lists the library
        // grouped by category and is searchable — flatter than the old 3-level "Run script ▸
        // Category ▸ Script" cascade, and lets you review before running on production.
        var runSelected = new MenuItem { Header = "Run script… (selected)" };
        runSelected.Click += (_, _) => OpenScriptRunner([.. vm.SelectedComputers]);
        _gridMenu.Items.Add(runSelected);

        var runAll = new MenuItem { Header = "Run script… (all machines)" };
        runAll.Click += (_, _) => OpenScriptRunner([.. vm.Computers]);
        _gridMenu.Items.Add(runAll);

        // Copy a package (MSI/EXE or a folder of files) to the selection (else all) — same scoping as
        // Run script. Opens the review-before-run Stage window; Vivre copies the files, doesn't install.
        var stage = new MenuItem { Header = "Stage software…" };
        stage.Click += OnStageSoftware;
        _gridMenu.Items.Add(stage);

        _gridMenu.Items.Add(new Separator());

        // The five SCCM client-action triggers grouped under one submenu (was five flat items).
        var clientActions = new MenuItem { Header = "Client actions" };
        foreach (ScheduleAction action in vm.ClientActions)
        {
            clientActions.Items.Add(new MenuItem
            {
                Header = action.Label,
                Command = vm.TriggerScheduleCommand,
                CommandParameter = action,
            });
        }

        _gridMenu.Items.Add(clientActions);

        _gridMenu.Items.Add(new Separator());

        var enableWinRm = new MenuItem { Header = "Enable WinRM (PSRemoting)…" };
        enableWinRm.Click += OnEnableWinRm;
        _gridMenu.Items.Add(enableWinRm);
    }

    /// <summary>
    /// Opens the same right-click action menu from the toolbar "Actions ▾" button or the keyboard
    /// (so nothing is mouse-right-click-only). Anchors the per-field Copy items on the focused /
    /// first-selected row.
    /// </summary>
    public void OpenActionsMenu(Control placementTarget)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        _contextRow = vm.FocusedComputer ?? vm.SelectedComputers.FirstOrDefault();
        BuildContextMenu(vm);
        _gridMenu.PlacementTarget = placementTarget;
        _gridMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        _gridMenu.IsOpen = true;
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

    /// <summary>
    /// Copies one long-text field of the RIGHT-CLICKED row (<see cref="_contextRow"/>) so the user
    /// can grab that one cell's full text — independent of the grid selection, which may be a stale
    /// multi-row set shared between the Machines and Windows Update grids. Ctrl+C / "Selected rows"
    /// still copies the whole (multi-row) selection.
    /// </summary>
    private void CopyField(Func<Computer, string?> selector)
    {
        if (_contextRow is null)
        {
            return;
        }

        string? value = selector(_contextRow);
        if (!string.IsNullOrEmpty(value))
        {
            Clipboard.SetText(value);
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

    /// <summary>
    /// Force-reboot confirmation. The hot Windows-Update follow-up, but destructive (unsaved work is
    /// lost), so it restates the count + names before firing <c>shutdown /r /f</c> on each.
    /// </summary>
    private async void OnRebootForce(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { SelectedComputers.Count: > 0 } vm)
        {
            return;
        }

        int count = vm.SelectedComputers.Count;
        string names = string.Join(", ", vm.SelectedComputers.Take(8).Select(c => c.Name));
        if (count > 8)
        {
            names += $", +{count - 8} more";
        }

        var confirm = new MessageBox
        {
            Title = "Force reboot",
            Content = $"Force-reboot {count} machine(s) now?\n\n{names}\n\n"
                      + "Runs 'shutdown /r /f /t 5' — any unsaved work on those machines is lost.",
            PrimaryButtonText = $"Reboot {count}",
            CloseButtonText = "Cancel",
        };

        if (await confirm.ShowDialogAsync() == MessageBoxResult.Primary)
        {
            await vm.RebootForceSelectedAsync();
        }
    }

    private void OnWugMaintenance(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        // Target the selection, else every machine in the tab (matches Install/Scan scoping).
        IReadOnlyList<Computer> targets = vm.SelectedComputers.Count > 0
            ? [.. vm.SelectedComputers]
            : [.. vm.Computers];

        if (targets.Count == 0)
        {
            return;
        }

        new MaintenanceWindow(vm, targets) { Owner = OwnerWindow }.ShowDialog();
    }

    /// <summary>Opens the Stage software window for the selection, else every machine in the tab
    /// (matches Run script / Install scoping). The window reviews before copying — it never auto-runs.</summary>
    private void OnStageSoftware(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        IReadOnlyList<Computer> targets = vm.SelectedComputers.Count > 0
            ? [.. vm.SelectedComputers]
            : [.. vm.Computers];

        if (targets.Count == 0)
        {
            return;
        }

        new DeployWindow(vm, targets) { Owner = OwnerWindow }.ShowDialog();
    }

    /// <summary>The machine the menu acts on: the right-clicked row, else the focused/first-selected.</summary>
    private Computer? ContextOrFocused() =>
        _contextRow ?? ViewModel?.FocusedComputer ?? ViewModel?.SelectedComputers.FirstOrDefault();

    private void OnShowDetails(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && ContextOrFocused() is { } computer)
        {
            new ComputerDetailWindow(vm.CreateDetailViewModel(computer)) { Owner = OwnerWindow }.Show();
            // Fill in the OS on demand (the window binds the live model, so it appears when ready).
            _ = vm.FetchOperatingSystemAsync(computer);
        }
    }

    private void OnShowMessages(object sender, RoutedEventArgs e)
    {
        if (ContextOrFocused() is { } computer && OwnerWindow is MainWindow main)
        {
            main.ShowActivityForMachine(computer.Name);
        }
    }

    /// <summary>Schedule the install for a future time on the selected machines (one-time SYSTEM task).</summary>
    private async void OnScheduleInstall(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var targets = vm.SelectedComputers.ToList();
        if (targets.Count == 0)
        {
            return;
        }

        var dialog = new ScheduleWindow("install") { Owner = OwnerWindow };
        if (dialog.ShowDialog() == true && dialog.Value is { } at)
        {
            await vm.ScheduleInstallSelectedAsync(targets, at);
        }
    }

    /// <summary>Schedule a one-time force-reboot of the selected machines at a chosen time.</summary>
    private async void OnScheduleReboot(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var targets = vm.SelectedComputers.ToList();
        if (targets.Count == 0)
        {
            return;
        }

        var dialog = new ScheduleWindow("reboot") { Owner = OwnerWindow };
        if (dialog.ShowDialog() == true && dialog.Value is { } at)
        {
            await vm.ScheduleRebootSelectedAsync(targets, at);
        }
    }

    /// <summary>Cancel any pending Vivre scheduled task (install or reboot) on the selected machines.</summary>
    private async void OnCancelScheduled(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && vm.SelectedComputers.Count > 0)
        {
            await vm.CancelScheduledTaskSelectedAsync([.. vm.SelectedComputers]);
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

    /// <summary>"Select shown": selects every row the filter currently shows in the active grid, so
    /// the user can act on just that subset (e.g. filter to Errors → Select shown → Install).</summary>
    private void OnSelectShown(object sender, RoutedEventArgs e)
    {
        DataGrid grid = ViewModel?.IsUpdateMode == true ? UpdateGrid : ComputerGrid;
        grid.Focus();
        grid.SelectAll();
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

        // Anchor the per-field Copy items on the right-clicked row, independent of the (possibly
        // stale / multi-row) selection — fixes "Copy ▸ Update message copied all machines".
        _contextRow = row.Item as Computer;

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
        if (row?.Item is not Computer computer || ViewModel is not { } vm)
        {
            return;
        }

        // The double-click selects this row first, so target the current selection — same machines
        // the right-click "Run script (selected)" would use (no surprise single-machine scope).
        IReadOnlyList<Computer> targets = vm.SelectedComputers.Count > 0 ? [.. vm.SelectedComputers] : [computer];
        OpenScriptRunner(targets);
    }

    private async void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        // Shift+F10 / the Menu key → open the action menu by keyboard (parity with right-click).
        if (sender is DataGrid grid && (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))))
        {
            e.Handled = true;
            OpenActionsMenu(grid);
            return;
        }

        if (e.Key != Key.Delete)
        {
            return;
        }

        e.Handled = true;
        int count = vm.SelectedComputers.Count;
        if (count == 0)
        {
            return;
        }

        // Small deletes are instant (re-pasteable, so cheap to undo); confirm only a large prune so
        // the guard never habituates on the 1–2 row case.
        if (count > 5)
        {
            var confirm = new MessageBox
            {
                Title = "Remove machines",
                Content = $"Remove {count} machines from this tab?\n\nThey stay in any saved list — re-load to bring them back.",
                PrimaryButtonText = $"Remove {count}",
                CloseButtonText = "Cancel",
            };
            if (await confirm.ShowDialogAsync() != MessageBoxResult.Primary)
            {
                return;
            }
        }

        vm.RemoveSelected();
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
