using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Vivre.Core.Columns;
using Vivre.Core.Models;
using Vivre.Core.Sccm;
using Vivre.Core.Scripts;
using Vivre.Desktop.ViewModels;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

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
        Loaded += OnViewLoaded;
        Unloaded += OnViewUnloaded;
    }

    // The runtime-built custom columns by name (DataGridColumn has no Tag, so we track them here to tell
    // them apart from the XAML built-ins).
    private readonly Dictionary<string, DataGridColumn> _customGridColumns = new(StringComparer.OrdinalIgnoreCase);
    private bool _columnsWired;
    // The view-model we wired layout subscriptions to (held so OnViewUnloaded detaches from the SAME one).
    private WorkspaceViewModel? _wiredVm;
    private string? _customSortKey;
    private bool _customSortAscending = true;

    /// <summary>Once the tab's view-model is attached, apply the saved column layout and keep the grid in
    /// step with it (built-in show/hide + the user's custom script columns), and wire custom-column sorting.</summary>
    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        if (_columnsWired || ViewModel is not { } vm)
        {
            return;
        }

        _columnsWired = true;
        _wiredVm = vm;
        vm.CustomColumns.CollectionChanged += OnLayoutChanged;
        vm.HiddenColumns.CollectionChanged += OnLayoutChanged;
        ComputerGrid.Sorting += OnComputerGridSorting;
        SyncColumns();
    }

    /// <summary>The TabControl recreates this view when you switch tabs, so drop the layout subscriptions
    /// when it leaves the visual tree — otherwise the (longer-lived) view-model's collections keep the
    /// detached view alive. OnViewLoaded re-wires it if the view comes back.</summary>
    private void OnViewUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_columnsWired)
        {
            return;
        }

        _columnsWired = false;
        if (_wiredVm is { } vm)
        {
            vm.CustomColumns.CollectionChanged -= OnLayoutChanged;
            vm.HiddenColumns.CollectionChanged -= OnLayoutChanged;
        }

        _wiredVm = null;
        ComputerGrid.Sorting -= OnComputerGridSorting;
    }

    private void OnLayoutChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncColumns();

    /// <summary>Brings the machine grid in line with the view-model's saved layout: hides/shows the
    /// built-in columns and creates/removes a text column per custom-column spec (bound to the row's
    /// per-key <see cref="Computer.CustomValues"/> store so cells fill live during a sweep).</summary>
    private void SyncColumns()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        // Hide/show built-ins (key by header text; never hide Name; leave our custom columns alone).
        var customCols = new HashSet<DataGridColumn>(_customGridColumns.Values);
        foreach (DataGridColumn col in ComputerGrid.Columns)
        {
            if (customCols.Contains(col))
            {
                continue;
            }

            string header = GetColumnKey(col);
            if (header.Length == 0 || header == "Name")
            {
                continue;
            }

            col.Visibility = vm.HiddenColumns.Contains(header) ? Visibility.Collapsed : Visibility.Visible;
        }

        // Remove custom columns whose spec is gone.
        var wanted = vm.CustomColumns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, DataGridColumn col) in _customGridColumns.ToList())
        {
            if (!wanted.Contains(name))
            {
                ComputerGrid.Columns.Remove(col);
                _customGridColumns.Remove(name);
            }
        }

        // Add a column for any spec that doesn't have one yet.
        foreach (CustomColumnSpec spec in vm.CustomColumns)
        {
            if (!_customGridColumns.ContainsKey(spec.Name))
            {
                DataGridColumn col = MakeCustomColumn(spec.Name);
                ComputerGrid.Columns.Add(col);
                _customGridColumns[spec.Name] = col;
            }
        }
    }

    private static DataGridTextColumn MakeCustomColumn(string name) => new()
    {
        Header = name,
        Width = DataGridLength.Auto,
        MinWidth = 90,
        MaxWidth = 320,
        Binding = new Binding($"CustomValues[{name}]"),
        ElementStyle = TrimmedCellStyle(),
    };

    // TextBlock cell style: ellipsis when narrow + full value on hover (matches the built-in text columns).
    private static Style TrimmedCellStyle()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding { RelativeSource = RelativeSource.Self, Path = new PropertyPath(TextBlock.TextProperty) }));
        return style;
    }

    /// <summary>A stable string key/label for a built-in column, handling both plain string headers and
    /// the two-line <see cref="TextBlock"/> headers (e.g. "Reboot Pending") used by the status columns —
    /// without it those columns couldn't be listed or hidden.</summary>
    private static string GetColumnKey(DataGridColumn col) => col.Header switch
    {
        string s => s,
        TextBlock tb => HeaderText(tb),
        { } other => other.ToString() ?? string.Empty,
        null => string.Empty,
    };

    private static string HeaderText(TextBlock tb)
    {
        var sb = new StringBuilder();
        foreach (Inline inline in tb.Inlines)
        {
            if (inline is Run run)
            {
                sb.Append(run.Text);
            }
            else if (inline is LineBreak)
            {
                sb.Append(' ');
            }
        }

        string text = sb.ToString().Trim();
        return text.Length > 0 ? text : tb.Text ?? string.Empty;
    }

    /// <summary>Custom columns can't sort via the default (indexer) path, so sort the view ourselves with a
    /// numeric-aware comparer on the row's CustomValues; built-in columns keep their normal sort.</summary>
    private void OnComputerGridSorting(object sender, DataGridSortingEventArgs e)
    {
        if (ViewModel is not { } vm || e.Column.Header is not string key || !_customGridColumns.ContainsKey(key))
        {
            return;
        }

        e.Handled = true;
        ListSortDirection dir = _customSortKey == key && _customSortAscending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        _customSortKey = key;
        _customSortAscending = dir == ListSortDirection.Ascending;

        if (CollectionViewSource.GetDefaultView(vm.Computers) is ListCollectionView view)
        {
            view.CustomSort = new CustomColumnComparer(key, dir);
        }

        foreach (DataGridColumn col in ComputerGrid.Columns)
        {
            col.SortDirection = null;
        }

        e.Column.SortDirection = dir;
    }

    /// <summary>Compares two rows by a custom column's value — numeric when both parse as numbers (so
    /// "Days since reboot" sorts 2 &lt; 10), otherwise case-insensitive text.</summary>
    private sealed class CustomColumnComparer(string key, ListSortDirection direction) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            string a = (x as Computer)?.CustomValues[key] ?? string.Empty;
            string b = (y as Computer)?.CustomValues[key] ?? string.Empty;
            int cmp = double.TryParse(a, out double da) && double.TryParse(b, out double db)
                ? da.CompareTo(db)
                : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            return direction == ListSortDirection.Ascending ? cmp : -cmp;
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

    // M24: helper to attach a SymbolIcon to a MenuItem's Icon slot.
    private static MenuItem WithIcon(MenuItem item, SymbolRegular symbol)
    {
        item.Icon = new SymbolIcon { Symbol = symbol, FontSize = 16 };
        return item;
    }

    private void BuildContextMenu(WorkspaceViewModel vm)
    {
        _gridMenu.Items.Clear();
        bool hasSelection = vm.SelectedComputers.Count > 0;

        // ---- Inspect ----
        var details = WithIcon(new MenuItem { Header = "Details…" }, SymbolRegular.Info24);
        details.Click += OnShowDetails;
        _gridMenu.Items.Add(details);

        var showMessages = WithIcon(new MenuItem { Header = "Show messages" }, SymbolRegular.History24);
        showMessages.Click += OnShowMessages;
        _gridMenu.Items.Add(showMessages);

        // Copy ▸ — per-field items act on the right-clicked row (_contextRow); the multi-row items
        // (Name(s) / Selected rows / online / offline) act on the selection.
        Computer? ctx = _contextRow;
        var copy = WithIcon(new MenuItem { Header = "Copy" }, SymbolRegular.Copy24);

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

        // ---- Windows Update mode: lead with the patch shortcuts (selection ⇒ those rows) ----
        if (vm.IsUpdateMode)
        {
            // Only meaningful when at least one selected row isn't already mid-install/uninstall.
            bool anyActionable = vm.SelectedComputers.Any(c => !c.IsPatching);
            int selCount = vm.SelectedComputers.Count;

            var scan = WithIcon(new MenuItem { Header = $"Scan selected ({selCount})", IsEnabled = anyActionable }, SymbolRegular.Search24);
            scan.Click += (_, _) => _ = vm.ScanSelectedAsync([.. vm.SelectedComputers]);
            _gridMenu.Items.Add(scan);

            var install = WithIcon(new MenuItem { Header = $"Install selected ({selCount})", IsEnabled = anyActionable }, SymbolRegular.ArrowDownload24);
            install.Click += (_, _) => _ = vm.InstallSelectedAsync([.. vm.SelectedComputers]);
            _gridMenu.Items.Add(install);

            _gridMenu.Items.Add(new Separator());
        }

        // ---- Run ----
        // Run a saved (or pasted) script against the selection or the whole tab; review before it runs.
        var runScript = WithIcon(new MenuItem { Header = "Run script" }, SymbolRegular.Play24);
        var runSelected = new MenuItem { Header = "Selected machines…" };
        runSelected.Click += (_, _) => OpenScriptRunner([.. vm.SelectedComputers]);
        runScript.Items.Add(runSelected);
        var runAll = new MenuItem { Header = "All machines…" };
        runAll.Click += (_, _) => OpenScriptRunner([.. vm.Computers]);
        runScript.Items.Add(runAll);
        _gridMenu.Items.Add(runScript);

        // The SCCM client-action triggers grouped under one submenu.
        var clientActions = WithIcon(new MenuItem { Header = "Client actions" }, SymbolRegular.Rocket24);
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

        var enableWinRm = WithIcon(new MenuItem { Header = "Enable WinRM (PSRemoting)…" }, SymbolRegular.PlugConnected24);
        enableWinRm.Click += OnEnableWinRm;
        _gridMenu.Items.Add(enableWinRm);

        _gridMenu.Items.Add(new Separator());

        // ---- Software ▸ ----
        var software = WithIcon(new MenuItem { Header = "Software" }, SymbolRegular.Apps24);
        // Check whether a named product is installed across the selection (else all) → the Software column.
        var checkSoftware = new MenuItem { Header = "Check software…" };
        checkSoftware.Click += OnCheckSoftware;
        software.Items.Add(checkSoftware);
        // Copy a package (MSI/EXE or folder) to the selection (else all); Vivre stages the files, doesn't install.
        var stage = new MenuItem { Header = "Stage software…" };
        stage.Click += OnStageSoftware;
        software.Items.Add(stage);
        _gridMenu.Items.Add(software);

        // ---- Export ▸ ----
        var export = WithIcon(new MenuItem { Header = "Export" }, SymbolRegular.ArrowExportUp24);
        // The rows currently shown (respects the filter) + all visible/custom columns — same as File ▸ Export to CSV.
        var exportShown = new MenuItem { Header = "Shown rows + columns (CSV)…", IsEnabled = vm.VisibleRowCount > 0 };
        exportShown.Click += OnExportShownRows;
        export.Items.Add(exportShown);
        // The software-check results (on-demand; enabled once a check has run).
        var exportSoftware = new MenuItem { Header = "Software report (CSV)…", IsEnabled = vm.HasSoftwareResults };
        exportSoftware.Click += OnExportSoftwareReport;
        export.Items.Add(exportSoftware);
        _gridMenu.Items.Add(export);

        _gridMenu.Items.Add(new Separator());

        // ---- Power / maintenance ----
        // Force-reboot the selected machines now — the most common Windows-Update follow-up.
        var rebootForce = WithIcon(new MenuItem { Header = "Reboot (force now)…", IsEnabled = hasSelection }, SymbolRegular.ArrowClockwise24);
        rebootForce.Click += OnRebootForce;
        _gridMenu.Items.Add(rebootForce);

        // Timed actions: a one-time SYSTEM task that runs at a chosen time (works in either mode).
        var schedule = WithIcon(new MenuItem { Header = "Schedule", IsEnabled = hasSelection }, SymbolRegular.CalendarClock24);
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

        // WhatsUp Gold maintenance mode (enter before patching / exit after) for the selection, else the
        // whole tab. Runs locally against the WUG server — prompts for the WUG login.
        var wugMaintenance = WithIcon(new MenuItem { Header = "WhatsUp Gold maintenance…" }, SymbolRegular.Toolbox24);
        wugMaintenance.Click += OnWugMaintenance;
        _gridMenu.Items.Add(wugMaintenance);

        // ---- Grid setup (machine mode only) ----
        if (vm.IsMachineMode)
        {
            _gridMenu.Items.Add(new Separator());
            var columns = WithIcon(new MenuItem { Header = "Columns…" }, SymbolRegular.ColumnTriple24);
            columns.Click += OnManageColumns;
            _gridMenu.Items.Add(columns);
        }
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

    /// <summary>Opens the Check software window for the selection, else every machine in the tab. Asks for
    /// a product name, then fills each row's Software column with the match (read-only — no confirm).</summary>
    private void OnCheckSoftware(object sender, RoutedEventArgs e)
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

        new SoftwareCheckWindow(vm, targets) { Owner = OwnerWindow }.ShowDialog();
    }

    /// <summary>Saves the current tab's software-check results to a CSV the user picks (right-click ▸
    /// Export software report). On-demand only — checking software never writes a file. Exports the rows
    /// currently shown (respects the filter), like the tab's "Export tab to CSV".</summary>
    private void OnExportSoftwareReport(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        if (!vm.HasSoftwareResults)
        {
            vm.Activity.Warn(null, "Export software report: run Check software… first — nothing to export.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export software report to CSV",
            Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = $"{SanitizeFileName(vm.Title)}-software-report.csv",
            DefaultExt = ".csv",
            AddExtension = true,
        };

        if (dialog.ShowDialog(OwnerWindow) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, vm.BuildSoftwareReportCsv(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            vm.Activity.Info(null, $"Exported software report to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            vm.Activity.Error(null, $"Software report export failed: {ex.Message}");
        }
    }

    /// <summary>Saves the rows currently shown in the grid (respecting the filter) with all visible + custom
    /// columns to a CSV the user picks (right-click ▸ Export ▸ Shown rows). Identical export to File ▸ Export
    /// to CSV — reuses <see cref="WorkspaceViewModel.BuildReportCsv"/>.</summary>
    private void OnExportShownRows(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        if (vm.VisibleRowCount == 0)
        {
            vm.Activity.Warn(null, "Export: no rows are shown to export.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export shown rows to CSV",
            Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = $"{SanitizeFileName(vm.Title)}-report.csv",
            DefaultExt = ".csv",
            AddExtension = true,
        };

        if (dialog.ShowDialog(OwnerWindow) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, vm.BuildReportCsv(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            vm.Activity.Info(null, $"Exported {vm.VisibleRowCount} row(s) to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            vm.Activity.Error(null, $"Export failed: {ex.Message}");
        }
    }

    /// <summary>Opens the Columns manager for the machine grid (hide built-ins, add predefined/custom
    /// script columns). Passes the current built-in headers so the dialog can list them.</summary>
    private void OnManageColumns(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var customCols = new HashSet<DataGridColumn>(_customGridColumns.Values);
        var builtins = ComputerGrid.Columns
            .Where(c => !customCols.Contains(c))
            .Select(GetColumnKey)
            .Where(k => k.Length > 0 && k != "Name")
            .ToList();

        new ColumnsWindow(vm, builtins) { Owner = OwnerWindow }.ShowDialog();
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(name) ? "tab" : name;
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
            // Header / empty area (e.g. an empty grid before any machines are loaded): no per-machine
            // actions, but still offer column customization so columns can be set up ahead of time.
            if (vm.IsMachineMode)
            {
                ShowColumnsOnlyMenu(grid);
                e.Handled = true;
            }

            return;
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

    // Minimal right-click menu for the empty grid / header — just column customization, so columns can be
    // arranged before any machines exist. (The full per-machine menu needs a clicked row.)
    private void ShowColumnsOnlyMenu(DataGrid grid)
    {
        _gridMenu.Items.Clear();
        var columns = new MenuItem { Header = "Columns…" };
        columns.Click += OnManageColumns;
        _gridMenu.Items.Add(columns);

        grid.ContextMenu = _gridMenu;
        _gridMenu.PlacementTarget = grid;
        _gridMenu.IsOpen = true;
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

    // --- M30: mode chip handlers (RadioButton.Checked) ---

    /// <summary>M30: Machines chip selected — switch to machine mode (same state as View ▸ Machines / Ctrl+M).
    /// Checked also fires when the OneWay binding re-checks the chip on an external mode change; the guard
    /// makes that a harmless no-op.</summary>
    private void OnMachinesModeChipChecked(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { IsUpdateMode: true } vm)
        {
            vm.IsUpdateMode = false;
        }
    }

    /// <summary>M30: Windows Update chip selected — switch to update mode (same state as View ▸ Windows Update / Ctrl+M).</summary>
    private void OnUpdateModeChipChecked(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { IsUpdateMode: false } vm)
        {
            vm.IsUpdateMode = true;
        }
    }

    // --- M29: "Get started" card handler ---

    /// <summary>M29: Open help button on the cold-start card — wires to the same command as F1 / Help ▸ How to use Vivre.</summary>
    private void OnGetStartedHelp(object sender, RoutedEventArgs e)
    {
        if (OwnerWindow is MainWindow main)
        {
            MainWindow.HelpKey.Execute(null, main);
        }
    }

    // --- M13: clear-filter button handler ---

    /// <summary>M13: Clear filter button on the filter-empty overlay — resets to All + clears the name box.</summary>
    private void OnClearFilter(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.ActiveFilter = ViewModels.RowFilter.All;
            vm.FilterText = string.Empty;
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
