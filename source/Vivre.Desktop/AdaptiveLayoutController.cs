using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace Vivre.Desktop;

/// <summary>
/// Drives the NavView across three width-based layout states and manages the ContentHost margin,
/// and also owns the toolbar compact/expanded decision (hiding button labels at narrow widths).
///
/// <list type="table">
///   <item><term>Wide  (&gt;1200 px)</term>
///         <description>PaneDisplayMode=Left, IsPaneOpen=true (or user's saved preference),
///         Fleet parent expanded showing Health + Patching children.</description></item>
///   <item><term>Medium (600–1200 px)</term>
///         <description>PaneDisplayMode=Left, IsPaneOpen=false (icons only, 48 px compact strip).
///         Fleet's Health/Patching remain reachable because WPF-UI raises ItemInvoked for every
///         NavigationViewItem click regardless of pane state — no flyout required in this mode;
///         clicking the Fleet icon expands/collapses its children inline.</description></item>
///   <item><term>Narrow (&lt;600 px)</term>
///         <description>PaneDisplayMode=LeftMinimal — the pane hides entirely (0 px), the
///         hamburger opens an overlay. ContentHost.Margin is 0.</description></item>
/// </list>
///
/// Hysteresis (~50 px) on each boundary prevents flicker when the window straddles a threshold.
/// At Wide, the user's manual open/close preference (NavPaneOpen setting) is honoured and
/// persisted. At Medium/Narrow the pane is forced compact/hidden — the saved preference is NOT
/// overwritten. Returning to Wide restores the saved preference.
///
/// Toolbar compact/expanded is MEASURE-BASED: labels are hidden only when the labelled action
/// cluster would genuinely overflow the space available to it (the left * column of the command
/// bar grid). The labelled desired width is cached whenever the bar is expanded and re-evaluated
/// on every SizeChanged and on mode changes. A small hysteresis margin (50 px) prevents flicker
/// at the collapse/expand boundary.
/// </summary>
internal sealed class AdaptiveLayoutController
{
    // ── threshold constants (all widths in device-independent pixels) ──────────

    /// <summary>Grow from Medium → Wide once the window reaches this width.</summary>
    private const double WideUpThreshold   = 1250;

    /// <summary>Shrink from Wide → Medium once the window drops to this width.</summary>
    private const double WideDownThreshold = 1150;

    /// <summary>Grow from Narrow → Medium once the window reaches this width.</summary>
    private const double MediumUpThreshold = 650;

    /// <summary>Shrink from Medium → Narrow once the window drops to this width.</summary>
    private const double MediumDownThreshold = 550;

    // ── toolbar compact thresholds ────────────────────────────────────────────

    /// <summary>Re-expand only once there's this much comfort margin over the cached labelled width, to suppress flicker.</summary>
    private const double ToolbarHysteresis = 50;

    // ── layout states ─────────────────────────────────────────────────────────

    private enum LayoutState { Wide, Medium, Narrow }

    private LayoutState _state = LayoutState.Medium; // will be set correctly on first Evaluate

    // ── toolbar compact state ─────────────────────────────────────────────────

    /// <summary>
    /// Whether the toolbar is currently in compact (icon-only) mode.
    /// Initialised to <see langword="false"/> so the first <see cref="Evaluate"/> call's
    /// compact-check always produces a state-change notification (since nearly all realistic
    /// startup widths are below the expand threshold, the first call sets compact = true and
    /// fires the callback, which sets <see cref="MainWindow.ToolbarCompact"/> correctly).
    /// </summary>
    private bool _toolbarCompact = false;

    /// <summary>
    /// The last-measured desired width of the action cluster with labels VISIBLE.
    /// Captured whenever the cluster is in expanded (labelled) mode so we know how wide
    /// the cluster needs to be before we collapse labels.  -1 means "not yet measured".
    /// </summary>
    private double _labelledWidth = -1;

    // ── external references (set at construction, never replaced) ─────────────

    private readonly NavigationView _navView;
    private readonly FrameworkElement _contentHost;
    private readonly AppSettingsStore? _settings;

    /// <summary>
    /// Column 0 of the ShellGrid (the nav-pane column).  Setting its <c>Width</c> shifts the
    /// content column's left edge to match the pane's current open/compact/minimal width.
    /// </summary>
    private readonly ColumnDefinition _navPaneColumn;

    /// <summary>The action cluster StackPanel — measured to determine the labelled desired width.</summary>
    private readonly FrameworkElement _actionCluster;

    /// <summary>The command-bar Grid — its first (star) column's ActualWidth is the available space.</summary>
    private readonly Grid _commandBarGrid;

    /// <summary>
    /// Stored window reference — used in <see cref="ConstrainCommandBar"/> to read the real
    /// window width and compute an explicit <see cref="Grid.Width"/> for the command-bar grid,
    /// which overrides the inflated available-width that WPF-UI's FluentWindow template supplies
    /// during the WPF measure pass.
    /// </summary>
    private Window? _window;

    /// <summary>
    /// The nav-pane width that was used in the most recent <see cref="UpdateContentHostMargin"/>
    /// call.  Cached so <see cref="ConstrainCommandBar"/> can reuse it when the window is
    /// resized without the pane width changing.
    /// </summary>
    private double _currentPaneWidth;

    /// <summary>
    /// Called whenever the toolbar compact state changes.  The argument is the new value.
    /// Wired to <see cref="MainWindow.ToolbarCompact"/> by the window code-behind.
    /// </summary>
    private readonly Action<bool> _setToolbarCompact;

    /// <summary>Returns true when the active section is Patching (Windows Update) mode, whose wider
    /// button set needs more width before labels fit. Queried each time the toolbar is evaluated.</summary>
    private readonly Func<bool> _isPatchingMode;

    /// <summary>The most recent window width, so <see cref="RefreshToolbar"/> can re-evaluate the
    /// toolbar on a mode change without a resize event.</summary>
    private double _lastWidth;

    // ── intent tracking ───────────────────────────────────────────────────────

    /// <summary>
    /// The user's last intentional open/closed choice at Wide width.
    /// Initialised from the persisted setting; kept in sync when the user toggles
    /// the pane at Wide. NOT updated when the controller forces compact/hidden.
    /// </summary>
    private bool _wideIntent;

    /// <summary>
    /// Count of controller-initiated pane-close requests whose <c>PaneClosed</c> callback has not
    /// yet fired. Incremented before we set <c>IsPaneOpen = false</c>; decremented when the
    /// callback fires. While positive, <see cref="OnPaneClosed"/> is treated as a controller-forced
    /// event and does NOT update <see cref="_wideIntent"/>.
    /// </summary>
    private int _pendingControllerCloses;

    /// <summary>
    /// Set to <see langword="true"/> when we transition to Wide and want to open the pane, but
    /// there is still a controller-forced <c>PaneClosed</c> callback in flight from the previous
    /// Medium/Narrow forced-close.  The open is deferred until <c>PaneClosed</c> fires and
    /// <see cref="_pendingControllerCloses"/> reaches 0, at which point <see cref="OnPaneClosed"/>
    /// will open the pane automatically.
    /// </summary>
    private bool _openPaneAfterClose;

    // ── public surface ────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises the controller but does NOT apply any state yet — call
    /// <see cref="Initialise"/> after the window is loaded.
    /// </summary>
    /// <param name="setToolbarCompact">
    /// Callback invoked (on the UI thread) whenever the toolbar compact state changes.
    /// Pass <c>w => w.ToolbarCompact = compact</c> or similar from the window code-behind.
    /// </param>
    public AdaptiveLayoutController(
        NavigationView navView,
        FrameworkElement contentHost,
        ColumnDefinition navPaneColumn,
        AppSettingsStore? settings,
        Action<bool> setToolbarCompact,
        Func<bool> isPatchingMode,
        FrameworkElement actionCluster,
        Grid commandBarGrid)
    {
        _navView            = navView;
        _contentHost        = contentHost;
        _navPaneColumn      = navPaneColumn;
        _settings           = settings;
        _setToolbarCompact  = setToolbarCompact;
        _isPatchingMode     = isPatchingMode;
        _actionCluster      = actionCluster;
        _commandBarGrid     = commandBarGrid;
    }

    /// <summary>
    /// Reads the persisted preference, applies the correct initial state for the
    /// current window width, and wires the <see cref="Window.SizeChanged"/> and
    /// command-bar <see cref="FrameworkElement.SizeChanged"/> handlers.
    /// Call once from <c>Window.Loaded</c>.
    /// </summary>
    public void Initialise(Window window)
    {
        _window = window;

        // Load saved wide-pane intent (defaults to false — starts compact).
        try   { _wideIntent = _settings?.Load().NavPaneOpen ?? false; }
        catch { _wideIntent = false; }

        // Apply the correct state for the window's current (startup) size.
        // Force the state machine to treat _state as "unknown" so Evaluate
        // always enters a branch on the first call.
        _state = (LayoutState)(-1);
        Evaluate(window.ActualWidth);

        window.SizeChanged += (_, e) =>
        {
            ConstrainCommandBar();
            Evaluate(e.NewSize.Width);
        };

        // Re-evaluate the toolbar on the command-bar grid's first layout pass.  We also call
        // ConstrainCommandBar here to ensure the explicit Width is applied AFTER the first
        // measure pass (when _commandBarGrid.ActualWidth is first populated) — the initial
        // Evaluate() call above happens too early for the constraint to take effect.
        // We hook LayoutUpdated on the Window rather than SizeChanged on the grid to avoid
        // re-entering the Grid's own layout pass, which can corrupt the star/auto column sizing.
        bool firstLayoutDone = false;
        window.LayoutUpdated += (_, _) =>
        {
            if (!firstLayoutDone && _commandBarGrid.ActualWidth > 100)
            {
                firstLayoutDone = true;
                ConstrainCommandBar();
                EvaluateToolbarByMeasure();
            }
        };
    }

    /// <summary>
    /// Called by <see cref="NavigationView.PaneOpened"/> / <see cref="NavigationView.PaneClosed"/>
    /// — forwarded from the window's existing handlers. If the pane event was triggered by the
    /// user (not by the controller), this records the intent and persists it when at Wide.
    /// </summary>
    public void OnPaneOpened()
    {
        // PaneOpened always fires after the close animation completes; no suppression needed here.
        // Update the margin and record intent only when at Wide.

        UpdateContentHostMargin(_navView.OpenPaneLength);

        // Only treat as a user intent and persist when we're at Wide width.
        // At Medium/Narrow the pane can open as a temporary overlay (via the hamburger)
        // without changing the saved Wide preference.
        if (_state == LayoutState.Wide)
        {
            _wideIntent = true;
            PersistWideIntent(true);
        }

        // Opening the pane shrinks the content area — re-check whether the toolbar labels still fit.
        EvaluateToolbarByMeasure();
    }

    /// <summary>See <see cref="OnPaneOpened"/>.</summary>
    public void OnPaneClosed()
    {
        // If there are pending controller-forced closes, consume one and ignore this callback —
        // it was caused by the controller itself, not by the user.
        if (_pendingControllerCloses > 0)
        {
            _pendingControllerCloses--;

            // If we transitioned to Wide while a controller-close was in flight, we deferred the
            // pane-open until this callback completes (because WPF-UI ignores IsPaneOpen=true while
            // a close animation is still running).  Now that the animation has finished, open it.
            if (_pendingControllerCloses == 0 && _openPaneAfterClose && _state == LayoutState.Wide)
            {
                _openPaneAfterClose = false;
                _navView.IsPaneOpen = true;
                // Margin already set to OpenPaneLength in Apply(Wide); leave it as-is.
            }
            return;
        }

        UpdateContentHostMargin(_navView.CompactPaneLength);

        // A PaneClosed that was not controller-forced is a user-initiated close.
        // Only record it as an intent change when at Wide width (at Medium/Narrow the
        // hamburger overlay can close without affecting the Wide preference).
        if (_state == LayoutState.Wide)
        {
            _wideIntent = false;
            PersistWideIntent(false);
        }

        // Closing the pane grows the content area — re-check whether the toolbar labels now fit.
        EvaluateToolbarByMeasure();
    }

    // ── private ───────────────────────────────────────────────────────────────

    private void Evaluate(double width)
    {
        // ── nav layout state machine (hysteresis loop) ─────────────────────────
        // Loop so a single large resize (a snap, maximize/restore, or programmatic jump straight
        // from Wide to a narrow width) steps through every intermediate state to the correct final
        // one — instead of advancing only one step per SizeChanged event and getting stuck. The
        // step-wise thresholds give hysteresis; the loop converges because the transitions are
        // monotonic for any fixed width.
        while (true)
        {
            LayoutState next = _state switch
            {
                LayoutState.Wide   => width <= WideDownThreshold   ? LayoutState.Medium : LayoutState.Wide,
                LayoutState.Narrow => width >= MediumUpThreshold   ? LayoutState.Medium : LayoutState.Narrow,
                // Medium (and unknown initial value): check both boundaries
                _                  => width >= WideUpThreshold   ? LayoutState.Wide
                                    : width <= MediumDownThreshold ? LayoutState.Narrow
                                    :                               LayoutState.Medium,
            };

            if (next == _state) break;

            _state = next;
            Apply(next);
        }

        // ── toolbar compact (measure-based) ───────────────────────────────────
        _lastWidth = width;
        EvaluateToolbarByMeasure();
    }

    /// <summary>
    /// Decides whether the command bar must drop its button labels (icon-only) based on a REAL
    /// measurement of the labelled cluster's desired width vs. the available star-column width.
    ///
    /// <para>
    /// Algorithm:
    /// <list type="bullet">
    ///   <item>When EXPANDED: measure the action cluster's <c>DesiredSize.Width</c> and cache it as
    ///         <see cref="_labelledWidth"/>.  If the cluster's desired width exceeds the available
    ///         star-column width → collapse.</item>
    ///   <item>When COMPACT: re-expand only once the available star-column width is at least
    ///         <c>_labelledWidth + <see cref="ToolbarHysteresis"/></c> — the hysteresis prevents
    ///         flicker when the window straddles the threshold.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Chicken-and-egg avoidance: while compact, <c>DesiredSize.Width</c> reflects the icon-only
    /// (small) layout, NOT the labelled one.  We therefore only update the cache when expanded.
    /// When switching modes (Health↔Patching, which changes the visible button set), we temporarily
    /// expand to get a fresh measurement, then immediately re-collapse if needed.
    /// </para>
    /// </summary>
    private void EvaluateToolbarByMeasure()
    {
        // Ignore pre-layout passes where ActualWidth is still ~0.
        if (_commandBarGrid.ActualWidth < 100) return;

        // The available space for the left action cluster is the star column's actual width.
        // We prefer to compute this directly from the window width (which ConstrainCommandBar
        // already uses) rather than reading ColumnDefinitions[0].ActualWidth, which is stale
        // during the SizeChanged handler because the grid has not yet re-laid-out with the new
        // explicit Width that ConstrainCommandBar just set.
        //
        // Formula: commandBarWidth = windowWidth - paneWidth - commandBarHMargin
        //          starColumnWidth  = commandBarWidth - autoColumnWidth (RightCluster)
        //
        // The RightCluster (Auto column) width is stable — we read it from ColumnDefinitions[1].
        // If the grid hasn't laid out yet we fall back to ColumnDefinitions[0].ActualWidth.
        double available;
        if (_window is not null && _window.ActualWidth > 1)
        {
            const double commandBarHMargin = 24;
            double commandBarWidth = Math.Max(0, _window.ActualWidth - _currentPaneWidth - commandBarHMargin);
            double rightColWidth = _commandBarGrid.ColumnDefinitions.Count > 1
                ? _commandBarGrid.ColumnDefinitions[1].ActualWidth
                : 0;
            available = Math.Max(0, commandBarWidth - rightColWidth);
        }
        else
        {
            available = _commandBarGrid.ColumnDefinitions.Count > 0
                ? _commandBarGrid.ColumnDefinitions[0].ActualWidth
                : _commandBarGrid.ActualWidth;
        }

        if (available < 10) return;

        if (!_toolbarCompact)
        {
            // Currently expanded: DesiredSize.Width reflects the labelled (unconstrained) desired
            // width of the action cluster — safe to read without an explicit Measure() call.
            double desiredWidth = _actionCluster.DesiredSize.Width;

            // Only cache if the measurement looks plausible (> 0 with labels visible).
            if (desiredWidth > 0)
            {
                _labelledWidth = desiredWidth;
            }

            // Collapse if the labelled cluster won't fit in the available star-column width.
            bool shouldCollapse = _labelledWidth > 0 && _labelledWidth > available;
            if (shouldCollapse)
            {
                _toolbarCompact = true;
                _setToolbarCompact(true);
            }
        }
        else
        {
            // Currently compact: re-expand only when there's enough room (hysteresis).
            if (_labelledWidth > 0 && available >= _labelledWidth + ToolbarHysteresis)
            {
                _toolbarCompact = false;
                _setToolbarCompact(false);
                // _labelledWidth stays valid; it will be refreshed on the next layout pass
                // once the labelled cluster renders and EvaluateToolbarByMeasure is called again.
            }
        }
    }

    /// <summary>
    /// Re-evaluates the toolbar compact state without a resize — call when the active section's mode
    /// changes (Health ↔ Patching), since Patching's wider button set changes the width needed.
    /// <para>
    /// The mode switch changes the set of Visible buttons, which changes the labelled desired width.
    /// We invalidate the cached width and re-evaluate so the new mode's actual needs are measured.
    /// If currently compact, we temporarily expand to allow WPF to measure the labelled layout,
    /// then collapse again if the new mode's buttons don't fit the available width.
    /// </para>
    /// </summary>
    public void RefreshToolbar()
    {
        // Invalidate the cached labelled width so the next layout pass re-caches it.
        _labelledWidth = -1;

        if (_toolbarCompact)
        {
            // Temporarily set compact=false so the cluster shows its labelled layout.
            _toolbarCompact = false;
            _setToolbarCompact(false);

            // Force a synchronous layout pass so DesiredSize reflects the newly-labelled state
            // before we read it in EvaluateToolbarByMeasure.
            _commandBarGrid.UpdateLayout();
        }

        // Re-evaluate: if labels don't fit the available width, this will collapse again.
        EvaluateToolbarByMeasure();
    }

    private void Apply(LayoutState state)
    {
        switch (state)
        {
            case LayoutState.Wide:
                // Restore the user's saved intent; keep pane in Left mode.
                _navView.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
                if (_wideIntent && !_navView.IsPaneOpen)
                {
                    // We want the pane open, but it is currently closed.
                    // WPF-UI's NavigationView ignores IsPaneOpen=true while a close animation
                    // is still running.  If there are pending controller-close callbacks in
                    // flight (from the Medium forced-close), defer the open until the last one
                    // fires — OnPaneClosed will open the pane once _pendingControllerCloses
                    // reaches 0.  If there are no pending closes (the animation already
                    // completed), open immediately.
                    UpdateContentHostMargin(_navView.OpenPaneLength);
                    if (_pendingControllerCloses > 0)
                    {
                        // Deferred open: OnPaneClosed will do it once the animation finishes.
                        _openPaneAfterClose = true;
                    }
                    else
                    {
                        // No animation in flight — open now.
                        _navView.IsPaneOpen = true;
                    }
                }
                else if (!_wideIntent && _navView.IsPaneOpen)
                {
                    // We're about to force-close (shouldn't happen at Wide normally, but guard).
                    _openPaneAfterClose = false;
                    _pendingControllerCloses++;
                    _navView.IsPaneOpen = false;
                    UpdateContentHostMargin(_navView.CompactPaneLength);
                }
                else
                {
                    // Pane state already matches intent — just ensure margin is correct.
                    _openPaneAfterClose = false;
                    UpdateContentHostMargin(_wideIntent
                        ? _navView.OpenPaneLength
                        : _navView.CompactPaneLength);
                }
                break;

            case LayoutState.Medium:
                // Compact icons-only strip; pane stays Left but forced closed.
                // Cancel any pending deferred open (we're going smaller, not larger).
                _openPaneAfterClose = false;
                _navView.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
                if (_navView.IsPaneOpen)
                {
                    // The pane is currently open — the forced IsPaneOpen=false below will trigger
                    // a PaneClosed event asynchronously. Register it so OnPaneClosed ignores it.
                    _pendingControllerCloses++;
                }
                _navView.IsPaneOpen = false;
                UpdateContentHostMargin(_navView.CompactPaneLength);
                break;

            case LayoutState.Narrow:
                // Overlay: pane hides entirely; hamburger opens it as a temporary overlay.
                // Cancel any pending deferred open.
                _openPaneAfterClose = false;
                if (_navView.IsPaneOpen)
                {
                    _pendingControllerCloses++;
                }
                _navView.PaneDisplayMode = NavigationViewPaneDisplayMode.LeftMinimal;
                _navView.IsPaneOpen      = false;
                UpdateContentHostMargin(0);
                break;
        }
    }

    /// <summary>
    /// Sets the nav-pane column width to <paramref name="paneWidth"/> pixels, which shifts the
    /// content column (column 1) left edge to that position, then explicitly constrains the
    /// command-bar grid's width to prevent WPF-UI's FluentWindow from inflating it during the
    /// WPF layout measure pass.
    /// </summary>
    private void UpdateContentHostMargin(double paneWidth)
    {
        _currentPaneWidth = paneWidth;
        _navPaneColumn.Width = new GridLength(paneWidth);
        ConstrainCommandBar();
    }

    /// <summary>
    /// Pins <see cref="_commandBarGrid"/> to an explicit <see cref="FrameworkElement.Width"/> equal
    /// to the window's content width minus the nav-pane width and the command-bar's left+right
    /// margins.  This prevents WPF-UI's <c>FluentWindow</c> template from inflating the measure
    /// context beyond the physical window width, which would push the Auto-column right cluster
    /// (RightCluster) off-screen.
    /// </summary>
    private void ConstrainCommandBar()
    {
        if (_window is null) return;
        double windowWidth = _window.ActualWidth;
        if (windowWidth < 1) return;

        // CommandBarGrid.Margin = "12,2,12,6" → 12 left + 12 right = 24 total horizontal margin.
        const double commandBarHMargin = 24;
        double width = Math.Max(0, windowWidth - _currentPaneWidth - commandBarHMargin);
        _commandBarGrid.Width = width;
        _commandBarGrid.HorizontalAlignment = HorizontalAlignment.Left;
    }

    private void PersistWideIntent(bool open)
    {
        if (_settings is null) return;
        try
        {
            AppSettings s = _settings.Load();
            s.NavPaneOpen = open;
            _settings.Save(s);
        }
        catch
        {
            // Settings write failure is non-fatal; the preference just won't survive restart.
        }
    }
}
