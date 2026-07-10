using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Vivre.Core.Columns;
using Vivre.Core.Models;
using Vivre.Core.Sccm;
using Vivre.Core.Scripts;
using Vivre.Core.Updates;
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
/// the right-click action menu, double-click to open Details, Delete/Copy, quick-add.
/// </summary>
public partial class WorkspaceView : UserControl
{
    /// <summary>The row the user right-clicked — the anchor for the per-field Copy ▸ submenu, so it
    /// copies THIS row's field regardless of any (possibly stale, multi-row) grid selection. Set in
    /// <see cref="OnGridRightClick"/> immediately before the menu is built.</summary>
    private Computer? _contextRow;

    /// <summary>Debounce timer for the post-settle corrective re-layout (see the constructor for the why).</summary>
    private readonly System.Windows.Threading.DispatcherTimer _settleRelayoutTimer;

    public WorkspaceView()
    {
        InitializeComponent();
        Loaded += OnViewLoaded;
        Unloaded += OnViewUnloaded;

        // Debounced "layout-settle" corrective re-layout. A virtualizing DataGrid first laid out DURING the
        // window-open bring-up (its slot still growing) realizes ~0 rows on the cold pass and arranges to ~0
        // even though its slot is real, then converges over later passes — but if the size stops changing
        // before it converges it stays stuck (blank, or a short stale height) until external churn (a window
        // resize) forces a fresh pass. This timer supplies that pass: kicked on every SizeChanged (startup
        // bring-up + fresh-tab reveal) and on a bound-collection reload (where the size doesn't change), it
        // fires ONCE ~150ms after things settle and re-measures + re-arranges the visible grids against the
        // now-stable viewport. Kicks and teardown live in OnViewLoaded/OnViewUnloaded so a detached keep-alive
        // view can't leak a running timer.
        _settleRelayoutTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _settleRelayoutTimer.Tick += OnSettleRelayoutTick;
    }

    /// <summary>SizeChanged kick: the view's size changes throughout the window-open bring-up and again when a
    /// keep-alive tab is revealed — (re)start the debounce so the corrective pass fires once the size settles.</summary>
    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e) => KickSettleRelayout();

    /// <summary>Collection kick: a reload (Clear + refill) into an already-visible, fixed-size tab changes
    /// neither the view size nor visibility, so SizeChanged can't cover it — (re)start the debounce instead.</summary>
    private void OnComputersChanged(object? sender, NotifyCollectionChangedEventArgs e) => KickSettleRelayout();

    private void KickSettleRelayout()
    {
        _settleRelayoutTimer.Stop();
        _settleRelayoutTimer.Start();
    }

    /// <summary>Fires ONCE ~150ms after the last size/collection change (the bring-up or reload has settled).
    /// Re-measures AND re-arranges each visible grid so a virtualizing grid stuck at a cold/under-realized height
    /// (0 rows in a real slot, or a stale partial height) gets a fresh convergent pass against the now-stable
    /// viewport — the correction a manual window resize provides today. Cannot loop: correcting a grid's height
    /// stays within its "*" row, so it changes neither this view's size nor the bound collection, so it re-fires
    /// neither kick.</summary>
    private void OnSettleRelayoutTick(object? sender, EventArgs e)
    {
        _settleRelayoutTimer.Stop();
        RelayoutIfVisible(ComputerGrid);
        RelayoutIfVisible(UpdateGrid);
    }

    private static void RelayoutIfVisible(FrameworkElement grid)
    {
        if (!grid.IsVisible)
        {
            return;
        }

        grid.InvalidateMeasure();
        grid.InvalidateArrange();
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
    /// step with it (built-in show/hide + the user's custom script columns), wire custom-column sorting,
    /// and subscribe to the VM's clear-selection event so the command bar's Clear button can deselect
    /// both DataGrids.</summary>
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
        vm.ClearSelectionRequested += OnClearSelectionRequested;
        vm.PropertyChanged += OnVmPropertyChanged;
        // Kick sources for the debounced settle-relayout (see the constructor). SizeChanged covers the
        // window-open bring-up and a fresh keep-alive tab's reveal; the bound-collection change covers a reload
        // into an already-visible, fixed-size tab (where the size doesn't move). Both torn down in OnViewUnloaded
        // — and the timer is stopped there too — so a detached view can't leak a running timer.
        SizeChanged += OnViewSizeChanged;
        vm.Computers.CollectionChanged += OnComputersChanged;
        ComputerGrid.Sorting += OnComputerGridSorting;
        SyncColumns();
        UpdateStagedColumnVisibility(vm);
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
        SizeChanged -= OnViewSizeChanged;
        _settleRelayoutTimer.Stop();
        if (_wiredVm is { } vm)
        {
            vm.CustomColumns.CollectionChanged -= OnLayoutChanged;
            vm.HiddenColumns.CollectionChanged -= OnLayoutChanged;
            vm.ClearSelectionRequested -= OnClearSelectionRequested;
            vm.PropertyChanged -= OnVmPropertyChanged;
            vm.Computers.CollectionChanged -= OnComputersChanged;
        }

        _wiredVm = null;
        ComputerGrid.Sorting -= OnComputerGridSorting;
    }

    private void OnLayoutChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncColumns();

    /// <summary>A DataGridColumn isn't in the visual tree, so its Visibility can't be data-bound — drive the
    /// whole "Staged" column (header + width) from the VM's HasStagedServer2016 in code-behind instead, so it
    /// disappears entirely when no 2016 box is flagged for staged patching.</summary>
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.HasStagedServer2016) && ViewModel is { } vm)
        {
            UpdateStagedColumnVisibility(vm);
        }
    }

    private void UpdateStagedColumnVisibility(WorkspaceViewModel vm) =>
        StagedColumn.Visibility = vm.HasStagedServer2016 ? Visibility.Visible : Visibility.Collapsed;

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

    /// <summary>
    /// Clears the selection when the user left-clicks genuinely empty grid space (the blank area
    /// below the last row) — standard Explorer/Excel behavior. Uses the bubbling phase so rows,
    /// cells, headers, and scroll bars have already handled their own input first.
    ///
    /// Walks up from <see cref="MouseButtonEventArgs.OriginalSource"/> using
    /// <see cref="VisualTreeHelper.GetParent"/>. If any of the "active" grid elements is
    /// encountered before reaching the <see cref="DataGrid"/> itself, the click is on real content
    /// and the method returns without touching the selection. Only when the walk reaches the
    /// DataGrid having seen none of those types does it call <c>UnselectAll()</c>.
    /// </summary>
    private void OnGridEmptyAreaClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid)
        {
            return;
        }

        DependencyObject? node = e.OriginalSource as DependencyObject;
        while (node is not null && !ReferenceEquals(node, grid))
        {
            if (node is DataGridRow
                or DataGridCell
                or DataGridColumnHeader
                or ScrollBar
                or ButtonBase)
            {
                return;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        if (node is null)
        {
            // OriginalSource wasn't in the grid's visual tree (e.g. popup content whose event
            // routed logically) — that's not empty grid space; leave the selection alone.
            return;
        }

        // Walk reached the DataGrid without hitting any interactive element — truly empty space.
        grid.UnselectAll();
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

        // ---- Patching (Windows Update) section: lead with the patch shortcuts (selection ⇒ those rows) ----
        if (vm.IsUpdateMode)
        {
            // Only meaningful when at least one selected row isn't already mid-install/uninstall.
            bool anyActionable = vm.SelectedComputers.Any(c => !c.IsPatching);
            int selCount = vm.SelectedComputers.Count;

            var scan = WithIcon(new MenuItem { Header = $"Scan selected ({selCount})", IsEnabled = anyActionable }, SymbolRegular.Search24);
            scan.Click += (_, _) => _ = vm.ScanSelectedAsync([.. vm.SelectedComputers]);
            _gridMenu.Items.Add(scan);

            var install = WithIcon(new MenuItem { Header = $"Install selected ({selCount})", IsEnabled = anyActionable }, SymbolRegular.ArrowDownload24);
            install.Click += async (_, _) =>
            {
                var sel = vm.SelectedComputers.ToList();
                IReadOnlyList<Computer> targets = sel.Count > 0 ? sel : [.. vm.Computers];
                if (targets.Count == 0)
                {
                    return;
                }

                // Staged-patching gate: a flagged 2016 box whose CU isn't staged routes through the decision dialog.
                if (await StagedInstallInteraction.ResolveAsync(Window.GetWindow(this), vm, targets) == StagedInstallOutcome.ProceedNormally)
                {
                    _ = vm.InstallSelectedAsync(targets);
                }
            };
            _gridMenu.Items.Add(install);

            // Staged-patching toggle — Server 2016 rows only. Marks/unmarks THIS row (the right-clicked one) as
            // needing the DISM staging lane; the choice persists and seeds future loads. Show only the applicable
            // verb so the menu never offers "Mark" on an already-flagged box (or vice-versa).
            if (LcuRouting.Is2016(ctx?.OsBuild))
            {
                Computer row2016 = ctx!;
                if (row2016.RequiresStagedPatching)
                {
                    var unmark = WithIcon(new MenuItem { Header = "Remove Staged flag" }, SymbolRegular.Dismiss24);
                    unmark.Click += (_, _) => vm.SetStagedPatching(row2016, false);
                    _gridMenu.Items.Add(unmark);
                }
                else
                {
                    var mark = WithIcon(new MenuItem { Header = "Mark as Staged patching" }, SymbolRegular.Box24);
                    mark.Click += (_, _) => vm.SetStagedPatching(row2016, true);
                    _gridMenu.Items.Add(mark);
                }
            }

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

        // Reboot & verify — Patching-only AND only when a selected box is genuinely reboot-pending. Keyed on
        // the SAME PatchState.RebootPending signal that drives the grid's "Reboot pending" pill (via
        // RebootVerifyMenu.ShouldOfferRebootVerify) so the item and the pill always agree — it shows for a
        // box pending from ANY source (in-session install, app reopen, re-scan, BatchPatch, manual), not
        // just one installed this session, since RebootRequired survives a re-scan. Visibility only — the
        // click still routes through the existing operator-confirmed Reboot & verify flow.
        if (vm.SelectedComputers.Any(c => RebootVerifyMenu.ShouldOfferRebootVerify(c, vm.IsUpdateMode)))
        {
            var rebootVerify = WithIcon(new MenuItem { Header = "Reboot & verify…", IsEnabled = hasSelection }, SymbolRegular.ArrowClockwiseDashes24);
            rebootVerify.Click += OnRebootAndVerify;
            _gridMenu.Items.Add(rebootVerify);
        }

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

        // Read-only sibling of the maintenance action — check current WUG state without changing it.
        var wugState = WithIcon(new MenuItem { Header = "Check WhatsUp Gold state…" }, SymbolRegular.Search24);
        wugState.Click += OnCheckWugState;
        _gridMenu.Items.Add(wugState);

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
        new ScriptRunnerWindow(resolved, vm.PowerShell, vm.Credentials, vm.Activity, vm.ScriptLibrary, initialScript) { Owner = OwnerWindow }.Show();
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

    private void OnCheckWugState(object sender, RoutedEventArgs e)
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

        new WugStateWindow(vm, targets) { Owner = OwnerWindow }.ShowDialog();
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

        // Build the CSV on the UI thread (it reads data-bound row/VM state), then push the disk write off
        // the UI thread so a large report never freezes the grid. The activity log is the progress
        // indicator: an "Exporting…" line now, the result when it finishes (it marshals to the UI itself).
        string csv = vm.BuildSoftwareReportCsv();
        string fileName = dialog.FileName;
        vm.Activity.Info(null, $"Exporting software report to {fileName}…");
        _ = Task.Run(() =>
        {
            try
            {
                File.WriteAllText(fileName, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                vm.Activity.Info(null, $"Exported software report to {fileName}");
            }
            catch (Exception ex)
            {
                vm.Activity.Error(null, $"Software report export failed: {ex.Message}");
            }
        });
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

        // Build the CSV on the UI thread (it reads data-bound row/VM state), then push the disk write off
        // the UI thread so a large export never freezes the grid. The activity log is the progress
        // indicator: an "Exporting…" line now, the result when it finishes (it marshals to the UI itself).
        string csv = vm.BuildReportCsv();
        string fileName = dialog.FileName;
        int rowCount = vm.VisibleRowCount;
        vm.Activity.Info(null, $"Exporting {rowCount} row(s) to {fileName}…");
        _ = Task.Run(() =>
        {
            try
            {
                File.WriteAllText(fileName, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                vm.Activity.Info(null, $"Exported {rowCount} row(s) to {fileName}");
            }
            catch (Exception ex)
            {
                vm.Activity.Error(null, $"Export failed: {ex.Message}");
            }
        });
    }

    /// <summary>Opens the Columns manager for the machine grid (hide built-ins, add predefined/custom
    /// script columns). Passes the current built-in headers so the dialog can list them.</summary>
    private void OnManageColumns(object sender, RoutedEventArgs e) => OpenColumnsWindowFromShell();

    /// <summary>Public entry point so the shell's Settings page can trigger the Columns dialog.</summary>
    public void OpenColumnsWindowFromShell()
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
            OpenDetails(vm, computer);
    }

    /// <summary>Opens a <see cref="ComputerDetailWindow"/> for <paramref name="computer"/> and starts
    /// the OS fetch. Called from both the Details right-click item and the row double-click handler.</summary>
    private void OpenDetails(WorkspaceViewModel vm, Computer computer)
    {
        new ComputerDetailWindow(vm.CreateDetailViewModel(computer)) { Owner = OwnerWindow }.Show();
        // Fill in the OS on demand (the window binds the live model, so it appears when ready).
        _ = vm.FetchOperatingSystemAsync(computer);
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

            // The Reboot Wave button is physically un-clickable until the operator has explicitly
            // selected at least one machine — a production reboot must never be one stray click away.
            // (The command also acts only on the selection; this is the enable gate.)
            RebootWaveButton.IsEnabled = vm.SelectedComputers.Count > 0;

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

    /// <summary>Server 2016 action bar — Stage. Before touching any box, re-check the package folder: if
    /// this month's CU <c>.msu</c> isn't there (or is wrong/ambiguous), show the guided "add the package"
    /// prompt and stage nothing. "Stage now" in that prompt loops back here to re-check, so the operator
    /// can drop the file and proceed without hunting for the button again.</summary>
    private async void OnStage2016(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || !EnsureStageTargets(vm))
        {
            return;
        }

        // Scan-this-session gate + guided package-readiness loop + stage, scoped to the panel's flagged-2016
        // targets. Shared with the decision dialog's "Stage CU first" branch so both behave identically.
        await StagedInstallInteraction.RunStageWorkflowAsync(Window.GetWindow(this), vm, vm.Server2016ActionTargets());
    }

    /// <summary>Clean up (panel button): free component-store space on the selected 2016 boxes — or all 2016 in
    /// the tab when none are selected. Selection-driven and independent of staged-state (unlike Stage / Verify),
    /// so it is NOT gated by <see cref="EnsureStageTargets"/>: component cleanup is self-contained, reboot-free,
    /// and helps normal Windows Update on any 2016 box. The button is enabled by HasServer2016, so a click always
    /// has a 2016 box to act on (the command no-ops only if the selection contains no 2016 box).</summary>
    private void OnCleanUp2016(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        vm.CleanUp2016Command.Execute(null);
    }

    /// <summary>Verify (panel button): read each flagged 2016 box's build and confirm the CU committed. Gated by
    /// <see cref="EnsureStageTargets"/> so an empty flagged set shows the guidance dialog instead of no-opping.</summary>
    private void OnVerify2016(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || !EnsureStageTargets(vm))
        {
            return;
        }

        vm.Verify2016Command.Execute(null);
    }

    /// <summary>Guards the panel's flagged-2016 actions (Stage / Verify): when no box is marked for staged
    /// patching there is nothing for them to act on, so show the guidance dialog BEFORE any box is touched and
    /// return false. Returns true when at least one flagged 2016 box exists. This is the "doesn't work when
    /// nothing's marked" feedback — those buttons used to silently no-op on an empty set. (Clean up is decoupled
    /// from staged-state and no longer routes through this guard.)</summary>
    private bool EnsureStageTargets(WorkspaceViewModel vm)
    {
        if (StagePreconditions.HasAnyStageTarget(vm.Server2016ActionTargets()))
        {
            return true;
        }

        new StagedPatchingNeededDialog { Owner = Window.GetWindow(this) }.ShowDialog();
        return false;
    }

    /// <summary>
    /// Fleet-wide Reboot &amp; verify entry point (2016 action bar and grid right-click).
    /// Production reboot — names the explicitly selected machines, confirms, and only invokes
    /// <c>RebootAndVerifyCommand</c> on confirmation. The command acts only on the selection.
    /// </summary>
    private async void OnRebootAndVerify(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var selected = vm.SelectedComputers.ToList();
        if (selected.Count == 0)
        {
            return; // gate should prevent this; never reboot without an explicit selection
        }

        // Name every box the operator chose (up to 10 inline, then a count).
        const int MaxInline = 10;
        string names = string.Join(", ", selected.Take(MaxInline).Select(c => c.Name));
        if (selected.Count > MaxInline)
        {
            names += $", +{selected.Count - MaxInline} more";
        }

        var confirm = new MessageBox
        {
            Title = "Reboot & verify",
            Content = $"This will reboot {selected.Count} machine(s):\n\n{names}\n\n"
                      + "Each reboots gracefully; if one won't go down within 8 minutes it is "
                      + "forced, to complete the reboot you ordered. Vivre then tracks each box "
                      + "until it is verified back online (2016 boxes verify by build/UBR; others "
                      + "by re-scan).\n\n"
                      + "Run this when the boxes are safe to restart (typically overnight).",
            PrimaryButtonText = "Reboot & verify",
            CloseButtonText = "Cancel",
        };

        if (await confirm.ShowDialogAsync() == MessageBoxResult.Primary
            && vm.RebootAndVerifyCommand.CanExecute(null))
        {
            vm.RebootAndVerifyCommand.Execute(null);
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

        // Open Details for the double-clicked row's machine (Details is single-machine — use the row
        // directly rather than the selection). Scripts are right-click ▸ Run script only.
        OpenDetails(vm, computer);
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

    /// <summary>
    /// Fix D: handles the Unchecked event on every filter chip.
    /// When the user clicks the already-lit chip (deselecting it), ConvertBack returns DoNothing so
    /// the binding doesn't update ActiveFilter.  We read the chip's Tag to know which filter it
    /// carries, then apply the "fall back to All" logic ourselves.
    /// Guard: only act when <c>vm.ActiveFilter == f</c> so that binding-driven sibling unchecks
    /// (which fire when another chip is checked) are no-ops.
    /// Special case: if the user clicks the lit "All" chip, re-light it — All is always on.
    /// </summary>
    private void OnFilterChipUnchecked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton chip) return;
        if (chip.Tag is not ViewModels.RowFilter f) return;
        if (ViewModel is not { } vm) return;

        if (f == ViewModels.RowFilter.All && vm.ActiveFilter == ViewModels.RowFilter.All)
        {
            // The "All" chip itself was clicked while already lit — re-light it; All is always on.
            chip.IsChecked = true;
            return;
        }

        // Only fall back to All when this chip is the currently active one.
        if (vm.ActiveFilter == f)
        {
            vm.ActiveFilter = ViewModels.RowFilter.All;
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

    // --- selection clear (driven by the command-bar's Clear button via the VM event) ---

    /// <summary>Raised by <see cref="WorkspaceViewModel.RequestClearSelection"/>; deselects all rows in both grids.</summary>
    private void OnClearSelectionRequested()
    {
        ComputerGrid.UnselectAll();
        UpdateGrid.UnselectAll();
    }
}
