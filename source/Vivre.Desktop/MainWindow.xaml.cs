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
/// The shell window: no menu bar; NavigationView pane (Fleet ▸ Health · Patching / Scripts / Cross-Domain RDP /
/// Settings); keep-alive content host that holds all sections Visibility-toggled (never rebuilt on nav switch).
/// Per-tab grid behaviour lives in <see cref="WorkspaceView"/>; this code-behind handles app-level concerns.
/// </summary>
public partial class MainWindow : FluentWindow
{
    // ── toolbar compact state (set by AdaptiveLayoutController; XAML binds style triggers to this) ──

    /// <summary>
    /// True when the window is narrow enough that toolbar button labels are hidden and buttons
    /// show icons only.  Set by <see cref="AdaptiveLayoutController"/>; XAML style triggers bind
    /// to this to collapse label TextBlocks and show full-label tooltips.
    /// </summary>
    public static readonly DependencyProperty ToolbarCompactProperty =
        DependencyProperty.Register(
            nameof(ToolbarCompact),
            typeof(bool),
            typeof(MainWindow),
            new PropertyMetadata(false));

    /// <summary>Gets or sets whether the command-bar buttons are in icon-only (compact) mode.</summary>
    public bool ToolbarCompact
    {
        get => (bool)GetValue(ToolbarCompactProperty);
        set => SetValue(ToolbarCompactProperty, value);
    }

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

    /// <summary>Script library singleton — injected by the composition root; passed to ScriptsSection.</summary>
    internal Core.Scripts.IScriptLibrary? ScriptLibrary { get; set; }

    /// <summary>The theme applied at startup — used to tick the right Theme radio on the Settings page.</summary>
    internal string SavedTheme { get; set; } = "Dark";

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    // ── adaptive layout controller (owns Wide/Medium/Narrow pane state + ContentHost margin) ──
    private AdaptiveLayoutController? _adaptiveLayout;

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
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

        // Initialize the Scripts page (injected IScriptLibrary singleton).
        if (ScriptLibrary is { } library)
        {
            ScriptsSection.Initialize(library);
        }

        // Show the Health section by default on startup.
        ShowNavSection(NavSection.Health);

        _adaptiveLayout = new AdaptiveLayoutController(
            NavView,
            ContentHost,
            NavPaneColumn,
            Settings,
            compact => ToolbarCompact = compact,
            () => Shell?.SelectedTab is WorkspaceViewModel { IsUpdateMode: true },
            ActionCluster,
            CommandBarGrid,
            Log);
        _adaptiveLayout.Initialise(this);

        // Layout diagnostic probe (permanent, env-gated, zero cost when unset). Launch with
        // VIVRE_LAYOUT_PROBE=1: the app steps itself through four window widths, measures the
        // empty-state overlays' ancestor chain and centre-vs-viewport deltas in BOTH overlay
        // states, writes %TEMP%\vivre-layout-probe.txt, and exits. Measure-only: it never alters
        // alignment, so it reports what the live XAML actually does. This is the instrument that
        // disproved the "DataGrid width leak" centering lore (deltas 0.0 at all widths, both
        // states, two code generations) — keep it for any future layout-drift suspicion.
        if (Environment.GetEnvironmentVariable("VIVRE_LAYOUT_PROBE") == "1")
        {
            _ = RunLayoutProbeAsync();
        }
    }

    #region Layout diagnostic probe (env-gated: VIVRE_LAYOUT_PROBE=1)

    private async Task RunLayoutProbeAsync()
    {
        var sb = new StringBuilder();
        string path = Path.Combine(Path.GetTempPath(), "vivre-layout-probe.txt");
        try
        {
            await Task.Delay(800); // let first layout settle

            // Find the Get-started card: the TextBlock "Get started" → its ancestor Border.
            System.Windows.Controls.TextBlock? title =
                FindDescendant<System.Windows.Controls.TextBlock>(this, tb => tb.Text == "Get started");
            if (title is null) { File.WriteAllText(path, "PROBE FAIL: card title not found"); Application.Current.Shutdown(); return; }
            DependencyObject d = title;
            while (d is not null and not Border) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            if (d is not Border card) { File.WriteAllText(path, "PROBE FAIL: card border not found"); Application.Current.Shutdown(); return; }

            // Measure-only: the live XAML's own alignment is what gets measured.
            foreach ((string label, double? w) in new (string, double?)[] { ("narrow-900", 900), ("normal-1360", 1360), ("wide-1900", 1900), ("maximized", null) })
            {
                if (w is { } width) { WindowState = WindowState.Normal; Width = width; Height = 780; }
                else { WindowState = WindowState.Maximized; }
                await Task.Delay(450); // layout + any animations settle
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);

                sb.AppendLine($"=== {label} ===");
                sb.AppendLine($"window: ActualWidth={ActualWidth:F1} state={WindowState}");
                sb.AppendLine($"ContentHost: ActualWidth={ContentHost.ActualWidth:F1}");

                // Card centre vs viewport centre (both in window coordinates).
                Point cardTopLeft = card.TransformToVisual(this).Transform(new Point(0, 0));
                double cardCenterX = cardTopLeft.X + card.ActualWidth / 2.0;
                Point hostTopLeft = ContentHost.TransformToVisual(this).Transform(new Point(0, 0));
                double viewportCenterX = hostTopLeft.X + ContentHost.ActualWidth / 2.0;
                sb.AppendLine($"cardCenterX={cardCenterX:F1}  viewportCenterX={viewportCenterX:F1}  delta={(cardCenterX - viewportCenterX):F1}");

                // Ancestor chain: card → window, ActualWidth + DesiredSize at each level.
                AppendAncestors(sb, card);
                sb.AppendLine();
            }

            // ── Phase 2: the HARD state — machines loaded, grid rendered WIDER than the viewport,
            //    then a non-matching filter collapses it and shows the filter-empty overlay.
            //    (AutoCheckOnLoad must be off for this run so no remote sweeps fire.)
            if (Shell?.SelectedTab is WorkspaceViewModel vm)
            {
                // Narrow window FIRST so the visible grid's column extent exceeds the viewport.
                WindowState = WindowState.Normal; Width = 900; Height = 780;
                vm.SetComputers([.. Enumerable.Range(1, 25).Select(i => $"PROBE-MACHINE-{i:D2}")]);
                await Task.Delay(600); // let the grid render its full column set at narrow width
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                sb.AppendLine($"=== grid-visible @900 (pre-filter) === GridOverlayState={vm.GridOverlayState}");

                // Now filter to nothing → state 3 → filter-empty overlay shows, grid collapses.
                vm.FilterText = "ZZZ_NO_MATCH";
                await Task.Delay(400);
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                sb.AppendLine($"after filter: GridOverlayState={vm.GridOverlayState}");

                System.Windows.Controls.TextBlock? noMatch =
                    FindDescendant<System.Windows.Controls.TextBlock>(this, tb => tb.Text == "No machines match this filter");
                if (noMatch is null) { sb.AppendLine("PROBE FAIL: filter-empty panel not found"); }
                else
                {
                    DependencyObject p = noMatch;
                    while (p is not null and not StackPanel) p = System.Windows.Media.VisualTreeHelper.GetParent(p);
                    if (p is StackPanel panel)
                    {
                        // Measure-only: the live XAML's own alignment is what gets measured.
                        foreach ((string label, double? w) in new (string, double?)[] { ("fe-narrow-900", 900), ("fe-normal-1360", 1360), ("fe-wide-1900", 1900), ("fe-maximized", null) })
                        {
                            if (w is { } width) { WindowState = WindowState.Normal; Width = width; Height = 780; }
                            else { WindowState = WindowState.Maximized; }
                            await Task.Delay(450);
                            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);

                            sb.AppendLine($"=== {label} ===");
                            sb.AppendLine($"window: ActualWidth={ActualWidth:F1} state={WindowState}  GridOverlayState={vm.GridOverlayState}");
                            Point feTopLeft = panel.TransformToVisual(this).Transform(new Point(0, 0));
                            double feCenterX = feTopLeft.X + panel.ActualWidth / 2.0;
                            Point hostTopLeft2 = ContentHost.TransformToVisual(this).Transform(new Point(0, 0));
                            double viewportCenterX2 = hostTopLeft2.X + ContentHost.ActualWidth / 2.0;
                            sb.AppendLine($"panelCenterX={feCenterX:F1}  viewportCenterX={viewportCenterX2:F1}  delta={(feCenterX - viewportCenterX2):F1}");
                            AppendAncestors(sb, panel);
                            sb.AppendLine();
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"PROBE EXCEPTION: {ex}");
        }

        File.WriteAllText(path, sb.ToString());
        Application.Current.Shutdown();
    }

    private static void AppendAncestors(StringBuilder sb, DependencyObject start)
    {
        sb.AppendLine("ancestors (type/name: Actual / Desired):");
        DependencyObject? a = start;
        while (a is not null)
        {
            if (a is FrameworkElement fe)
            {
                sb.AppendLine($"  {fe.GetType().Name}/{fe.Name}: A={fe.ActualWidth:F1} D={fe.DesiredSize.Width:F1}");
            }
            a = System.Windows.Media.VisualTreeHelper.GetParent(a);
        }
    }

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t && predicate(t)) return t;
            if (FindDescendant(child, predicate) is { } found) return found;
        }
        return null;
    }

    #endregion

    // --- NavigationView pane ---

    /// <summary>The nav destinations (Health and Patching replace Computers).</summary>
    private enum NavSection { Health, Patching, Scripts, Rdp, Settings }

    /// <summary>Click handler on the Fleet parent nav item — expands/collapses only; does NOT change section.</summary>
    private void OnFleetNavClick(object sender, RoutedEventArgs e) { /* expand/collapse only — children are the real destinations */ }

    /// <summary>Click handler on the Health nav item — drives the section toggle.</summary>
    private void OnHealthNavClick(object sender, RoutedEventArgs e) => ShowNavSection(NavSection.Health);

    /// <summary>Click handler on the Patching nav item — drives the section toggle.</summary>
    private void OnPatchingNavClick(object sender, RoutedEventArgs e) => ShowNavSection(NavSection.Patching);

    /// <summary>Click handler on the Scripts nav item — drives the section toggle.</summary>
    private void OnScriptsNavClick(object sender, RoutedEventArgs e) => ShowNavSection(NavSection.Scripts);

    /// <summary>Click handler on the Cross-Domain RDP nav item — drives the section toggle.</summary>
    private void OnRdpNavClick(object sender, RoutedEventArgs e) => ShowNavSection(NavSection.Rdp);

    /// <summary>Click handler on the Settings footer item — drives the section toggle.</summary>
    private void OnSettingsNavClick(object sender, RoutedEventArgs e) => ShowNavSection(NavSection.Settings);

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
            // Fleet parent click: expand/collapse only — do NOT navigate away.
            if (ReferenceEquals(item, FleetNavItem)) return;

            NavSection section = ReferenceEquals(item, HealthNavItem)   ? NavSection.Health
                               : ReferenceEquals(item, PatchingNavItem) ? NavSection.Patching
                               : ReferenceEquals(item, ScriptsNavItem)  ? NavSection.Scripts
                               : ReferenceEquals(item, RdpNavItem)      ? NavSection.Rdp
                               :                                           NavSection.Settings;
            ShowNavSection(section);
        }
    }

    /// <summary>
    /// Shows exactly one nav section and collapses the others. Neither Navigate() nor ReplaceContent()
    /// is ever called — all sections stay in the visual tree permanently. This is the keep-alive
    /// mechanism: ComputersSection, ScriptsSection, RdpSection, and SettingsSection are
    /// Visibility-toggled only and are never rebuilt on a nav switch. Within ComputersSection the two
    /// TabControlEx strips (Health and Patching) are also Visibility-toggled — never removed.
    /// </summary>
    private void ShowNavSection(NavSection section)
    {
        if (ComputersSection is null || ScriptsSection is null || RdpSection is null || SettingsSection is null) return;

        bool computers = section is NavSection.Health or NavSection.Patching;
        ComputersSection.Visibility = computers            ? Visibility.Visible : Visibility.Collapsed;
        ScriptsSection.Visibility   = section == NavSection.Scripts   ? Visibility.Visible : Visibility.Collapsed;
        RdpSection.Visibility       = section == NavSection.Rdp       ? Visibility.Visible : Visibility.Collapsed;
        SettingsSection.Visibility  = section == NavSection.Settings  ? Visibility.Visible : Visibility.Collapsed;

        // Update nav highlight.
        HealthNavItem.IsActive   = section == NavSection.Health;
        PatchingNavItem.IsActive = section == NavSection.Patching;
        ScriptsNavItem.IsActive  = section == NavSection.Scripts;
        RdpNavItem.IsActive      = section == NavSection.Rdp;
        SettingsNavItem.IsActive = section == NavSection.Settings;

        // Mirror section switch into the ShellViewModel so the SelectedTab routing stays in sync.
        if (Shell is { } shell)
        {
            if (section == NavSection.Health)
                shell.ActiveFleetSection = ViewModels.FleetSection.Health;
            else if (section == NavSection.Patching)
                shell.ActiveFleetSection = ViewModels.FleetSection.Patching;
        }

        UpdateStatusBarVisibility();

        // The active section's mode (Health vs Patching) changed — the Patching toolbar is wider,
        // so re-check whether the command-bar labels still fit.
        _adaptiveLayout?.RefreshToolbar();
    }

    /// <summary>
    /// Keeps the full-width status bar visible only when the Computers section is shown AND
    /// the active tab is a workspace tab. Hides it on the Settings page and on the RDP section.
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

        // Delegate to the adaptive controller when initialised; it manages the NavPaneColumn width
        // and persists the user's intent only when at Wide width.
        if (_adaptiveLayout is { } ctrl)
        {
            ctrl.OnPaneOpened();
        }
        else
        {
            // Fallback before Loaded fires (shouldn't normally occur).
            NavPaneColumn.Width = new System.Windows.GridLength(sender.OpenPaneLength);
        }
    }

    private void OnNavPaneClosed(NavigationView sender, RoutedEventArgs e)
    {
        // Guard: ContentHost may not exist yet if this fires during InitializeComponent.
        if (ContentHost is null) return;

        if (_adaptiveLayout is { } ctrl)
        {
            ctrl.OnPaneClosed();
        }
        else
        {
            NavPaneColumn.Width = new System.Windows.GridLength(sender.CompactPaneLength);
        }
    }

    // --- public helpers called from SettingsPage ---

    /// <summary>Opens the Columns window acting on the currently-active workspace tab.</summary>
    public void OpenColumnsWindow()
    {
        // Columns window needs a WorkspaceViewModel and the current builtin column headers.
        // It's opened from WorkspaceView; here we delegate to the active tab's WorkspaceView.
        if (Shell?.SelectedTab is not WorkspaceViewModel) return;

        // WorkspaceView exposes OpenColumnsWindow as an internal helper.
        if (FindActiveWorkspaceView() is { } wsv)
        {
            wsv.OpenColumnsWindowFromShell();
        }
    }

    private WorkspaceView? FindActiveWorkspaceView()
    {
        // Walk the PART_ItemsHolder of the currently-visible tab strip to find the active WorkspaceView.
        TabControlEx activeStrip = Shell?.ActiveFleetSection == ViewModels.FleetSection.Patching
            ? PatchingTabs
            : HealthTabs;

        if (activeStrip.Template.FindName("PART_ItemsHolder", activeStrip) is System.Windows.Controls.Panel holder)
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

        // Hook existing tabs in both sections.
        foreach (WorkspaceViewModel tab in shell.AllTabs)
        {
            tab.OperationCompleted += OnOperationCompleted;
        }

        // Subscribe to both collections for future tabs.
        SubscribeToastsToCollection(shell.HealthTabs);
        SubscribeToastsToCollection(shell.PatchingTabs);
    }

    private void SubscribeToastsToCollection(System.Collections.ObjectModel.ObservableCollection<WorkspaceViewModel> tabs)
    {
        tabs.CollectionChanged += (_, e) =>
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

    // FIFO queue for completion banners: when multiple ops finish near-simultaneously, show one at
    // a time in finish order, each for its normal hold duration. Drained by OnCompletionBarTick.
    private readonly Queue<(string Summary, ViewModels.OperationSeverity Severity)> _completionBarQueue = new();

    private void OnOperationCompleted(string summary, ViewModels.OperationSeverity severity)
    {
        if (IsActive)
        {
            Dispatcher.BeginInvoke(() => EnqueueCompletionBar(summary, severity));
            return;
        }

        Dispatcher.BeginInvoke(() => TrayIcon.ShowBalloonTip("Vivre", summary, Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info));
    }

    /// <summary>Adds a completion notification to the queue. If no banner is currently showing,
    /// starts the first one immediately. Concurrent ops that finish while the current banner is
    /// visible are queued and shown in order once the hold timer expires.</summary>
    private void EnqueueCompletionBar(string summary, ViewModels.OperationSeverity severity)
    {
        _completionBarQueue.Enqueue((summary, severity));

        // Only start showing if we're not already in a hold cycle.
        if (_completionBarTimer is null || !_completionBarTimer.IsEnabled)
        {
            ShowNextCompletionBar();
        }
    }

    private void ShowNextCompletionBar()
    {
        if (_completionBarQueue.Count == 0) return;

        (string summary, ViewModels.OperationSeverity severity) = _completionBarQueue.Dequeue();
        ShowCompletionBar(summary, severity);
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

        // Drain the next queued banner, if any.
        if (_completionBarQueue.Count > 0)
        {
            ShowNextCompletionBar();
        }
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

        foreach (IDisposable tab in Shell?.AllTabs.OfType<IDisposable>().ToArray() ?? [])
        {
            tab.Dispose();
        }

        // Dispose the RDP singleton — it's no longer in Tabs (it's a nav section, not a tab).
        (Shell?.RdpViewModel as IDisposable)?.Dispose();

        TrayIcon?.Dispose();
    }

    private void OnRefreshRelativeTimes(object? sender, EventArgs e)
    {
        if (Shell is not { } shell) return;

        foreach (var computer in shell.AllTabs.SelectMany(tab => tab.Computers))
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

    // The dock reopens to the operator's last saved height (persisted in AppSettings) — we no longer
    // shrink it to a fraction on reopen. To keep the machine grid usable, the grid keeps this minimum;
    // the same value is set as WorkspaceGridRow.MinHeight in XAML so a splitter drag can't squeeze the
    // list away either, and ShowDock caps the saved height against it should the window have shrunk since.
    private const double WorkspaceGridMinHeight = 150;

    /// <summary>Sensible floor so a degenerate saved value can't open the dock uselessly thin.</summary>
    private const double DockMinOpenHeight = 100;

    private GridLength _activityHeight = new(170);
    private WorkspaceViewModel? _observedTab;

    private void HookBottomDock()
    {
        if (Shell is not { } shell) return;

        // Seed _activityHeight from the persisted value when it's sane (> 0).
        if (Settings is { } store)
        {
            try
            {
                double saved = store.Load().BottomDockHeight;
                if (saved > 0)
                {
                    _activityHeight = new GridLength(saved);
                }
            }
            catch (Exception ex)
            {
                Log?.Warn(null, $"Couldn't load dock height setting. {ex.Message}");
            }
        }

        shell.PropertyChanged += OnShellPropertyChanged;
        SubscribeToSelectedTab(shell.SelectedTab);
        RecomputeBottomDock();
    }

    private void OnShellPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.SelectedTab))
        {
            SubscribeToSelectedTab(Shell?.SelectedTab);
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

        StatusProgressBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
        StatusProgressBar.Value = _observedTab?.FleetProgress ?? 0;
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

        // Selection change swaps a different button set into the ActionCluster — re-measure so
        // labels collapse to icon-only (with tooltips) at narrow widths, exactly like Health↔Patching.
        if (e.PropertyName == nameof(WorkspaceViewModel.HasSelection))
        {
            _adaptiveLayout?.RefreshToolbar();
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
            StatusProgressBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, anim);
        }
    }

    private bool UpdatesTriggerActive =>
        Shell?.SelectedTab is WorkspaceViewModel { IsUpdateMode: true, FocusedComputer: not null };

    private void RecomputeBottomDock()
    {
        // The Updates TAB exists only in Patching mode with a focused machine (it shows that machine's
        // updates). The dock's OPEN-state is driven SOLELY by the toggle — selecting a row NEVER opens it.
        bool updatesTab = UpdatesTriggerActive;
        bool open = ActivityLogToggle?.IsChecked == true;

        UpdatesTab.Visibility = updatesTab ? Visibility.Visible : Visibility.Collapsed;
        if (!updatesTab && BottomDockTabs.SelectedItem == UpdatesTab)
        {
            BottomDockTabs.SelectedItem = ActivityTab;
        }

        if (open)
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
        // Re-assert the workspace grid row as star-sized in case a previous splitter drag
        // pinned it to a fixed pixel height — this ensures the grid always flexes with the window.
        WorkspaceGridRow.Height = new GridLength(1, GridUnitType.Star);

        // Open to the operator's last saved height AS-IS (persisted across close/reopen). The only clamps
        // keep both panes usable: never thinner than the dock floor, and never so tall the machine grid
        // drops below its minimum (which can only happen if the window shrank since the height was saved).
        double rawHeight = _activityHeight.Value;
        double sectionHeight = ComputersSection.ActualHeight;
        double openHeight;
        if (sectionHeight > 0)
        {
            double maxHeight = Math.Max(DockMinOpenHeight, sectionHeight - WorkspaceGridMinHeight);
            openHeight = Math.Max(DockMinOpenHeight, Math.Min(rawHeight, maxHeight));
        }
        else
        {
            // Pre-layout: use the remembered height as-is (section height not known yet).
            openHeight = Math.Max(DockMinOpenHeight, rawHeight);
        }

        ActivityRow.Height = new GridLength(openHeight);
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
            PersistDockHeight(ActivityRow.ActualHeight);
        }

        // Re-assert the workspace grid row as star-sized so the grid flexes with the window
        // regardless of any fixed pixel height a prior splitter drag may have left on it.
        WorkspaceGridRow.Height = new GridLength(1, GridUnitType.Star);

        ActivitySplitter.Visibility = Visibility.Collapsed;
        ActivityPanel.Visibility = Visibility.Collapsed;
        ActivityPanel.Opacity = 0;
        SplitterRow.Height = new GridLength(0);
        ActivityRow.Height = new GridLength(0);
    }

    private void OnActivitySplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        // Persist the raw user-dragged height immediately so it survives even if the app closes
        // while the dock stays open (HideDock won't be called in that case).
        if (ActivityRow.ActualHeight > 0)
        {
            _activityHeight = new GridLength(ActivityRow.ActualHeight);
            PersistDockHeight(ActivityRow.ActualHeight);
        }
    }

    private void PersistDockHeight(double height)
    {
        if (Settings is null) return;
        try
        {
            AppSettings s = Settings.Load();
            s.BottomDockHeight = height;
            Settings.Save(s);
        }
        catch (Exception ex)
        {
            Log?.Warn(null, $"Couldn't save dock height. {ex.Message}");
        }
    }

    private void OnActivityLogToggleChanged(object sender, RoutedEventArgs e)
    {
        if (ActivityLogToggle.IsChecked == true)
        {
            // Patching opens to the Updates tab (the primary patching content) when a machine is focused;
            // otherwise — and always in Health — open to the Activity tab.
            BottomDockTabs.SelectedItem = UpdatesTriggerActive ? UpdatesTab : ActivityTab;
        }

        RecomputeBottomDock();
    }

    private void OnCloseBottomDock(object sender, RoutedEventArgs e)
    {
        if (ActivityLogToggle is not null)
        {
            ActivityLogToggle.IsChecked = false;
        }

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

        if (ActivityLogToggle is not null)
        {
            ActivityLogToggle.IsChecked = true;
        }

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

    // --- update source ---

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

        var dialog = new ExcludeUpdatesWindow(vm.ExcludeText) { Owner = this };

        if (dialog.ShowDialog() == true && dialog.Value is { } text)
        {
            vm.ExcludeText = text;
        }
    }

    // --- toolbar Install ---

    private async void OnInstallClick(object sender, RoutedEventArgs e) =>
        await RunInstallFlowAsync(Shell?.SelectedTab as WorkspaceViewModel, selectionOnly: false);

    /// <summary>Called from the selection command bar in <see cref="WorkspaceView"/> — installs on
    /// the currently-selected machines with the same confirm dialog as the toolbar Install button.</summary>
    public void TriggerInstallForSelection()
    {
        if (Shell?.SelectedTab is WorkspaceViewModel { HasSelection: true } vm)
        {
            _ = RunInstallFlowAsync(vm, selectionOnly: true);
        }
    }

    // --- selection cluster handlers (command bar, in-place swap) ---

    /// <summary>Command-bar Scan (N): scans only the selected machines.</summary>
    private void OnSelectionScanClick(object sender, RoutedEventArgs e)
    {
        if (Shell?.SelectedTab is WorkspaceViewModel { HasSelection: true } vm)
        {
            _ = vm.ScanSelectedAsync([.. vm.SelectedComputers]);
        }
    }

    /// <summary>Command-bar Install (N): same confirm-then-install flow as the toolbar, scoped to selection.</summary>
    private void OnSelectionInstallClick(object sender, RoutedEventArgs e) => TriggerInstallForSelection();

    /// <summary>Command-bar Clear selection: raises the VM event so the active WorkspaceView's grids clear.</summary>
    private void OnSelectionClearClick(object sender, RoutedEventArgs e)
    {
        if (Shell?.SelectedTab is WorkspaceViewModel vm)
        {
            vm.RequestClearSelection();
        }
    }

    /// <summary>
    /// The shared install-with-confirm flow. When <paramref name="selectionOnly"/> is true the
    /// scope is always the current selection (used by the selection bar); when false the scope
    /// is the selection when rows are selected, otherwise every machine in the tab (toolbar).
    /// </summary>
    private async Task RunInstallFlowAsync(WorkspaceViewModel? vm, bool selectionOnly)
    {
        if (vm is null) return;

        bool hasSelection = vm.SelectedComputers.Count > 0;
        IReadOnlyList<Computer> targets = (selectionOnly || hasSelection)
            ? [.. vm.SelectedComputers]
            : [.. vm.Computers];
        int count = targets.Count;
        if (count == 0 || !vm.InstallTargetCommand.CanExecute(null)) return;

        List<Computer> pending = [.. targets.Where(c => c.RebootRequired == true)];

        // Shared tail: route through the Server 2016 staged-patching gate, then — only when no decision was
        // needed — the usual "Install on N" confirm and the normal install command. When the gate shows its
        // decision dialog and handles the run, the generic confirm is skipped (the decision dialog IS the confirm).
        async Task GatedProceedAsync(bool showConfirm)
        {
            if (await StagedInstallInteraction.ResolveAsync(this, vm, targets) != StagedInstallOutcome.ProceedNormally)
            {
                return; // gate handled the run, or the operator cancelled
            }

            if (showConfirm)
            {
                string scope = (selectionOnly || hasSelection)
                    ? $"the {count} selected machine(s)"
                    : $"all {count} machine(s) in this tab";
                var confirm = new MessageBox
                {
                    Title = "Install updates",
                    Content = $"Download and install applicable updates on {scope}?\n\n"
                              + "Each host runs a one-time SYSTEM task. A required reboot is reported, not forced.",
                    PrimaryButtonText = $"Install on {count}",
                    CloseButtonText = "Cancel",
                };
                if (await confirm.ShowDialogAsync() != MessageBoxResult.Primary)
                {
                    return;
                }
            }

            if (vm.InstallTargetCommand.CanExecute(null))
            {
                vm.InstallTargetCommand.Execute(null);
            }
        }

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

            await GatedProceedAsync(showConfirm: false); // operator already chose "Install anyway"
            return;
        }

        await GatedProceedAsync(showConfirm: true);
    }

    /// <summary>Re-seed every loaded row's <see cref="Computer.RequiresStagedPatching"/> from the persisted
    /// StagedHosts set. Called after the Settings page edits the list so open tabs don't keep a stale flag (which
    /// would mis-route an install). Mirrors the row-add seeding: set the flag from membership on EVERY row, not
    /// just confirmed 2016 ones — so a flag removed in Settings is also cleared on a not-yet-classified (null
    /// OsBuild) row before its build resolves to 2016. Harmless on non-2016 rows: routing, the panel, the Staged
    /// column and HasStagedServer2016 all additionally gate on Is2016, so the flag only ever acts on 2016 boxes.</summary>
    public void ResyncStagedPatchingFlags()
    {
        if (Settings is not { } store || Shell is not { } shell)
        {
            return;
        }

        HashSet<string> staged = store.Load().StagedHosts;
        foreach (WorkspaceViewModel tab in shell.AllTabs.OfType<WorkspaceViewModel>())
        {
            foreach (Computer c in tab.Computers)
            {
                c.RequiresStagedPatching = StagedHostMatching.IsStaged(staged, c.Name);
            }
        }
    }

    // --- keyboard accelerator handlers ---

    private void OnNewTabKey(object sender, ExecutedRoutedEventArgs e) => Shell?.NewTabCommand.Execute(null);

    private void OnCloseTabKey(object sender, ExecutedRoutedEventArgs e)
    {
        if (Shell?.SelectedTab is { } tab) CloseTabWithGuard(tab);
    }

    /// <summary>Returns the active section's tab collection.</summary>
    private System.Collections.ObjectModel.ObservableCollection<WorkspaceViewModel>? ActiveTabs =>
        Shell is { } shell
            ? (shell.ActiveFleetSection == ViewModels.FleetSection.Health ? shell.HealthTabs : shell.PatchingTabs)
            : null;

    private void OnFocusAddKey(object sender, ExecutedRoutedEventArgs e)
    {
        // Only meaningful when on the Computers section.
        if (ComputersSection.Visibility == Visibility.Visible)
        {
            (FindName("QuickAddBox") as UIElement)?.Focus();
        }
    }

    private void OnRenameTabKey(object sender, ExecutedRoutedEventArgs e)
    {
        if (Shell?.SelectedTab is WorkspaceViewModel tab) RenameTab(tab);
    }

    private void OnToggleModeKey(object sender, ExecutedRoutedEventArgs e)
    {
        if (Shell is not { } shell) return;

        // Toggle between Health and Patching sections; update the nav highlight to match.
        shell.ToggleFleetSection();
        NavSection next = shell.ActiveFleetSection == ViewModels.FleetSection.Health
            ? NavSection.Health
            : NavSection.Patching;
        ShowNavSection(next);
    }

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
        if (sender is FrameworkElement { DataContext: WorkspaceViewModel tab }) CloseTabWithGuard(tab);
    }

    private async void CloseTabWithGuard(WorkspaceViewModel tab)
    {
        if (tab.HasWork)
        {
            int n = tab.Computers.Count;
            string detail = n > 0 ? $"{n} machine(s)" : "a running operation";
            var confirm = new MessageBox
            {
                Title = "Close tab",
                Content = $"Close \"{tab.Title}\"? It has {detail}.\n\nMachines stay in any saved list — re-open to bring them back.",
                PrimaryButtonText = "Close tab",
                CloseButtonText = "Keep open",
            };
            if (await confirm.ShowDialogAsync() != MessageBoxResult.Primary) return;
        }

        Shell?.CloseTabCommand.Execute(tab);
    }

    private void OnCloseOtherTabs(object sender, RoutedEventArgs e)
    {
        if (ActiveTabs is { } tabs && sender is FrameworkElement { DataContext: WorkspaceViewModel keep })
        {
            CloseTabs([.. tabs.Where(t => t != keep)]);
        }
    }

    private void OnCloseTabsToRight(object sender, RoutedEventArgs e)
    {
        if (ActiveTabs is { } tabs && sender is FrameworkElement { DataContext: WorkspaceViewModel anchor })
        {
            int index = tabs.IndexOf(anchor);
            if (index >= 0) CloseTabs([.. tabs.Skip(index + 1)]);
        }
    }

    private void OnCloseAllTabs(object sender, RoutedEventArgs e)
    {
        if (ActiveTabs is { } tabs)
        {
            CloseTabs([.. tabs]);
        }
    }

    private async void CloseTabs(IReadOnlyList<WorkspaceViewModel> tabs)
    {
        if (Shell is not { } shell || tabs.Count == 0) return;

        int withWork = tabs.Count(t => t.HasWork);
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

        foreach (WorkspaceViewModel tab in tabs) shell.CloseTabCommand.Execute(tab);
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
            && sender is FrameworkElement { DataContext: WorkspaceViewModel tab })
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

        var qab = FindName("QuickAddBox") as Wpf.Ui.Controls.TextBox;
        string[] names = (qab?.Text ?? string.Empty).Split(
            [',', ';', ' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (names.Length > 0)
        {
            vm.AddComputers(names);
            if (qab is not null) qab.Text = string.Empty;
        }
    }

    // --- Lists button and Update options button — open their ContextMenus on click ---

    private void OnListsButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement btn && btn.ContextMenu is { } menu)
        {
            menu.PlacementTarget = btn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void OnUpdateOptionsButtonClick(object sender, RoutedEventArgs e)
    {
        if (UpdateOptionsButton.ContextMenu is { } menu)
        {
            menu.PlacementTarget = UpdateOptionsButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    // --- Lists ▾ drop-down ---

    /// <summary>Rebuilds the Open and Delete list submenus every time the Lists flyout opens.</summary>
    private void OnListsFlyoutOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm) return;

        WorkspaceViewModel? vm = Shell?.SelectedTab as WorkspaceViewModel;
        IReadOnlyList<string> lists = vm?.SavedLists() ?? [];

        // Find the Open list and Delete list items by Tag.
        MenuItem? openMenu = cm.Items.OfType<MenuItem>().FirstOrDefault(i => i.Tag is "OpenList");
        MenuItem? deleteMenu = cm.Items.OfType<MenuItem>().FirstOrDefault(i => i.Tag is "DeleteList");

        if (openMenu is not null)
        {
            openMenu.Items.Clear();
            AddListItems(openMenu, lists, name => vm?.OpenList(name));
            openMenu.IsEnabled = vm is not null;
        }

        if (deleteMenu is not null)
        {
            deleteMenu.Items.Clear();
            AddListItems(deleteMenu, lists, name => { if (vm is not null) ConfirmDeleteList(vm, name); });
        }
    }

    private void OnSaveTabAsList(object sender, RoutedEventArgs e)
    {
        if (Shell?.SelectedTab is WorkspaceViewModel vm) SaveCurrentAsList(vm);
    }

    // --- tab context menu: New tab / Clear this tab ---

    private void OnClearThisTab(object sender, RoutedEventArgs e)
    {
        // DataContext on the ContextMenu is the WorkspaceViewModel for that tab header.
        WorkspaceViewModel? vm = null;
        if (sender is FrameworkElement { DataContext: WorkspaceViewModel tabVm })
        {
            vm = tabVm;
        }
        else
        {
            vm = Shell?.SelectedTab as WorkspaceViewModel;
        }

        vm?.SetComputers([]);
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
        // Pre-fill with the tab's current title so a renamed tab ("Region C", etc.) doesn't need
        // re-typing. The user can overtype it before saving.
        var dialog = new TextPromptWindow("Save list", "Save this tab's machines as a list named:", vm.Title) { Owner = this };
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
