using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using AxMSTSCLib;
using MSTSCLib;
using Vivre.Core.Rdp;
using Vivre.Desktop.ViewModels;

namespace Vivre.Desktop;

/// <summary>
/// Hosts the Microsoft RDP ActiveX control (AxMsRdpClient9NotSafeForScripting, over mstscax.dll) inside a WPF
/// WindowsFormsHost for one Cross-Domain RDP session.
/// </summary>
/// <remarks>
/// <para><b>Rendering model (client-side zoom):</b> the session renders at 100% scale into a LOGICAL-size
/// framebuffer (the pane's DIP size), and the OCX's documented ZoomLevel feature magnifies it client-side to
/// fill the physical pane — readable at high DPI with the session scale untouched. THE PIN CARDINAL:
/// <see cref="LocalScale"/> stays hardcoded (100,100) and is read at EXACTLY TWO sites (the connect-time
/// extended-settings block and <see cref="ResizeRemote"/>) — the Failover Cluster Manager context-menu bug
/// trips at any session scale above 100% (Microsoft won't-fix), and the zoom design additionally REQUIRES the
/// session at 100%. Never raise it, never remove either read site.</para>
/// <para><b>Re-fit engine:</b> UpdateSessionDisplaySettings reports success for requests the server silently
/// drops (proven: requests arriving back-to-back are dropped or land a stale intermediate — the
/// minimize/restore repro), and MS-RDPEDISP forbids odd display-control widths. So every size change is
/// VERIFIED: sends go through one choke point with minimum spacing, a one-shot verify timer reads the session
/// size back (the OCX's DesktopWidth/Height read-back tracked reality faithfully in every recorded
/// observation) and re-sends until the session matches the CURRENT expectation, with the OCX's
/// OnRemoteDesktopSizeChange event as the applied-signal accelerator. All sizes are floored to EVEN on both
/// dimensions (width is protocol law; height is empirical caution) and to the protocol minimum of 200.
/// Sends wait for quiet hands (no live drag, no held mouse button): with SmartSizing off, an applied re-fit
/// re-lays out the OCX's client area, and doing that under a held button is the proven stuck-pointer trigger.</para>
/// <para><b>Hosting:</b> the Ax control is <c>Dock=Fill</c> inside an intermediate WinForms Panel, the Panel is
/// the host's Child (the ActiveX wrapper has a ~150px default it won't shrink below), and the host's size is
/// set explicitly from its WPF container on every resize (WindowsFormsHost ignores Stretch).</para>
/// </remarks>
public partial class RdpSessionView : UserControl
{
    private const int MaxRefitRetries = 3;
    private const int MinSendSpacingMs = 500;   // back-to-back sends are the proven drop trigger
    private const int VerifyBaseDelayMs = 700;

    private static readonly uint[] ZoomLadder = [100, 125, 150, 175, 200, 250, 300, 400, 500];

    private AxMsRdpClient9NotSafeForScripting? _rdp;
    private System.Windows.Forms.Panel? _hostPanel;
    private RdpSessionViewModel? _vm;
    private bool _connectStarted;
    private bool _connected; // reached the remote desktop at least once (login completed)
    private bool _closing;   // tearing the control down ourselves — ignore its own disconnect event
    private readonly System.Windows.Threading.DispatcherTimer _resizeTimer;
    private readonly System.Windows.Threading.DispatcherTimer _verifyTimer;
    private Window? _hostWindow; // for the StateChanged restore kick; null = treat as not minimized
    private readonly RdpFreezeInstrument _instr = new(); // THROWAWAY smoke-round-2 freeze instrument — remove before merge

    // ---- drag-deferred host sizing (see ApplyHostSize) ----
    private readonly System.Windows.Threading.DispatcherTimer _hostSizePoll; // applies a stashed host size once hands are off
    private HwndSource? _windowSource;  // fix-owned modal size/move hook — independent of the throwaway instrument
    private bool _windowSizeMoveLoop;   // inside the window's WM_ENTERSIZEMOVE..WM_EXITSIZEMOVE modal loop
    private Size? _pendingHostSize;     // host size stashed during a live drag; applied once on settle

    // ---- re-fit engine state (per view; counters/latches reset in CreateControl) ----
    private int _refitGeneration;     // bumped by every size intent; cancels the pending verify
    private int _sendSequence;        // stamped per send; queued accelerator bodies bail if outdated
    private int _retryCount;          // counts ONLY genuine-drop re-sends (fire-time expected == last sent)
    private (int Width, int Height) _lastSentSize;
    private DateTime _lastSendAt = DateTime.MinValue;
    private DateTime _lastSizeChangedAt = DateTime.MinValue; // verify won't fight a live drag
    private bool _fullScreen;         // view-side FS flag (synchronous, unlike the marshaled VM property)
    private bool _syncingFullScreen;  // view→VM FullScreen mirror in progress: don't sync it back to the OCX
    private bool _refitWarnLatched;
    private bool _zoomWarnLatched;

    // Zoom failed on this session: zoom off, SmartSizing on, framebuffer = physical (today's
    // fill-but-compact — never worse than shipping). Reset in CreateControl so Reconnect retries zoom.
    private bool _degraded;

    // The server threw on UpdateSessionDisplaySettings (pre-RDP-8.1: no dynamic resize). STICKY across
    // rebuilds (deliberately NOT reset in CreateControl — same server): the session is rebuilt once at the
    // degraded physical size with SmartSizing, and the engine stays dormant (no sends, no verifies).
    private bool _legacyServer;

    // Client-side zoom percent, derived from the display scale and snapped DOWN to the mstsc ladder.
    // 100 = zoom off (SmartSizing stays on — today's behavior on 100% displays).
    private uint _zoomPercent = 100;

    public RdpSessionView()
    {
        InitializeComponent();
        // Debounce resizes: re-fit the actual remote resolution to the new pane size once resizing settles.
        _resizeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _resizeTimer.Tick += OnResizeSettled;
        // One-shot verify: reads the session size back after a send and re-sends until it matches the CURRENT
        // expectation. Tick handlers are hooked HERE ONLY and never unhooked — DisposeSession only Stop()s them,
        // so a re-loaded view keeps working (the old unhook-in-Dispose left reloads with a dead timer).
        _verifyTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(VerifyBaseDelayMs) };
        _verifyTimer.Tick += OnVerifyTick;
        // Applies a drag-stashed host size as soon as hands are off (covers GridSplitter drags, which raise
        // no modal size/move messages — the button release is the only signal). Hooked here only, like the rest.
        _hostSizePoll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _hostSizePoll.Tick += OnHostSizePollTick;
        // Hidden sessions (keep-alive ItemsControl collapses non-active ones) reconnect/resize against stale
        // layout — kick a re-fit + zoom re-assert when the view becomes visible again.
        IsVisibleChanged += OnViewVisibleChanged;
        Loaded += OnLoaded;
        // Unloaded fires only when the session is truly removed (its tab closed, or the Cross-Domain RDP tab closed):
        // the shell uses TabControlEx + an ItemsControl, so a tab/session SWITCH collapses the view, not unload.
        Unloaded += (_, _) => DisposeSession();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_rdp is not null || DataContext is not RdpSessionViewModel vm)
        {
            return;
        }

        _vm = vm;
        // Fresh control — clear any teardown/connection flags in case this view is ever re-loaded after a
        // previous DisposeSession, so close-on-logoff isn't silently disabled by a stale _closing.
        _closing = false;
        _connected = false;
        _connectStarted = false;

        _zoomPercent = DeriveZoomPercent();

        // Minimize→restore can return the window to IDENTICAL bounds, firing no SizeChanged — hook the
        // window's StateChanged so a restore always kicks one settle (the engine converges from there).
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is not null)
        {
            _hostWindow.StateChanged += OnHostWindowStateChanged;

            // WM_ENTERSIZEMOVE/WM_EXITSIZEMOVE mark the window's modal border-drag loop — the primary
            // suppress window for ApplyHostSize (the OCX must not be re-laid-out per drag tick).
            if (PresentationSource.FromVisual(_hostWindow) is HwndSource windowSource)
            {
                _windowSource = windowSource;
                _windowSource.AddHook(OnWindowMessage);
            }
        }

        _instr.Start(vm.Log, vm.Title, _hostWindow); // THROWAWAY freeze instrument

        CreateControl();

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.ReconnectRequested += OnReconnectRequested;

        Connect();
    }

    /// <summary>Builds a FRESH RDP ActiveX control + its host panel and wires its events. Used both on first
    /// load and on Reconnect — the OCX can't be reliably re-Connect()ed after a drop, so reconnect tears the old
    /// one down and rebuilds through this same path, so first-connect and reconnect can't drift.</summary>
    private void CreateControl()
    {
        var createStopwatch = System.Diagnostics.Stopwatch.StartNew(); // THROWAWAY freeze instrument

        // Ax control fills an intermediate WinForms Panel; the Panel (which accepts any size) is the host's
        // Child — NOT the Ax control (whose fixed default size makes the host collapse).
        _hostPanel = new System.Windows.Forms.Panel();
        _rdp = new AxMsRdpClient9NotSafeForScripting();
        ((ISupportInitialize)_rdp).BeginInit();
        _rdp.Dock = System.Windows.Forms.DockStyle.Fill;
        _hostPanel.Controls.Add(_rdp);
        RdpHostElement.Child = _hostPanel;
        ((ISupportInitialize)_rdp).EndInit();

        ApplyHostSize(); // give the host an explicit size now (Stretch is ignored on WindowsFormsHost)

        // Fresh control = fresh engine bookkeeping. _legacyServer deliberately survives (same server).
        _retryCount = 0;
        _lastSentSize = default;
        _lastSendAt = DateTime.MinValue;
        _refitWarnLatched = false;
        _zoomWarnLatched = false;
        _degraded = false;
        _fullScreen = false;
        _verifyTimer.Stop();

        _rdp.OnConnecting += OnRdpConnecting;
        _rdp.OnConnected += OnRdpConnected;
        _rdp.OnLoginComplete += OnRdpLoginComplete;
        _rdp.OnDisconnected += OnRdpDisconnected;
        _rdp.OnFatalError += OnRdpFatalError;
        _rdp.OnAutoReconnecting += OnRdpAutoReconnecting;
        _rdp.OnAutoReconnected += OnRdpAutoReconnected;
        _rdp.OnEnterFullScreenMode += OnEnterFullScreen;
        _rdp.OnLeaveFullScreenMode += OnLeaveFullScreen;
        // Applied-signal accelerator: the OCX raises this when the session size ACTUALLY changes — run the
        // verify immediately instead of waiting out the timer. Subscribed per control (a subscribe-once slip
        // would silently kill it after the first Reconnect); unhooked in TearDownControl with the rest.
        _rdp.OnRemoteDesktopSizeChange += OnRdpRemoteDesktopSizeChange;

        _instr.ReportTimed("CreateControl", createStopwatch.ElapsedMilliseconds); // THROWAWAY freeze instrument
    }

    /// <summary>Unsubscribes the control's events (BEFORE disconnecting, so its Disconnect() can't re-enter our
    /// handlers), disconnects if live, then disposes the control + host panel. Used by Reconnect (rebuild) and
    /// DisposeSession (final teardown).</summary>
    private void TearDownControl()
    {
        _verifyTimer.Stop();

        if (_rdp is null)
        {
            return;
        }

        AxMsRdpClient9NotSafeForScripting rdp = _rdp; // non-null local so instrument lambdas stay warning-free
        _instr.Timed("UnhookEvents(teardown)", () =>
        {
            rdp.OnConnecting -= OnRdpConnecting;
            rdp.OnConnected -= OnRdpConnected;
            rdp.OnLoginComplete -= OnRdpLoginComplete;
            rdp.OnDisconnected -= OnRdpDisconnected;
            rdp.OnFatalError -= OnRdpFatalError;
            rdp.OnAutoReconnecting -= OnRdpAutoReconnecting;
            rdp.OnAutoReconnected -= OnRdpAutoReconnected;
            rdp.OnEnterFullScreenMode -= OnEnterFullScreen;
            rdp.OnLeaveFullScreenMode -= OnLeaveFullScreen;
            rdp.OnRemoteDesktopSizeChange -= OnRdpRemoteDesktopSizeChange;
        });

        try
        {
            if (_instr.Timed("Connected(teardown)", () => rdp.Connected) != 0)
            {
                _instr.Timed("Disconnect(teardown)", () => { rdp.Disconnect(); });
            }
        }
        catch (Exception)
        {
            // Best-effort teardown: the control may already be disconnecting.
        }

        RdpHostElement.Child = null;
        _instr.Timed("Dispose(teardown)", () => { rdp.Dispose(); });
        _rdp = null;
        _hostPanel?.Dispose();
        _hostPanel = null;
    }

    private void Connect()
    {
        if (_rdp is null || _vm is null || _connectStarted)
        {
            return;
        }

        _connectStarted = true;
        RdpConnectionSettings s = _vm.Settings;
        var setupStopwatch = System.Diagnostics.Stopwatch.StartNew(); // THROWAWAY freeze instrument
        try
        {
            _rdp.Server = s.Server;
            _rdp.AdvancedSettings9.RDPPort = s.Port;
            _rdp.UserName = s.UserName;
            _rdp.Domain = s.Domain ?? string.Empty;

            // Connect at the current expectation: LOGICAL pane size normally (the zoom magnifies it to fill),
            // or the degraded/legacy PHYSICAL size (SmartSizing fills, compact — today's behavior).
            (int width, int height) = ExpectedSize();
            _rdp.DesktopWidth = width;
            _rdp.DesktopHeight = height;
            _lastSentSize = (width, height); // connect counts as a send for spacing/verify bookkeeping
            _lastSendAt = DateTime.UtcNow;

            // NLA via CredSSP: on by default; OFF lets a server on another domain accept the supplied
            // credentials instead of rejecting delegated/saved ones (the cross-domain 0x2107 rejection).
            _rdp.AdvancedSettings9.EnableCredSspSupport = s.NlaEnabled;

            // SmartSizing and ZoomLevel are mutually exclusive (Microsoft docs): zoom sessions run with
            // SmartSizing OFF; 100%-scale, degraded, and legacy sessions keep SmartSizing ON (today's model).
            _rdp.AdvancedSettings9.SmartSizing = _zoomPercent <= 100 || _degraded || _legacyServer;
            _rdp.AdvancedSettings9.ContainerHandledFullScreen = 0;

            // Let the client transparently recover a transient drop (brief network blip) on its own before we
            // surface a disconnect; and grab keyboard/mouse focus on (re)connect so input goes to the remote.
            _rdp.AdvancedSettings9.EnableAutoReconnect = true;
            _rdp.AdvancedSettings9.GrabFocusOnConnect = true;

            // THE PIN CARDINAL: the session's display + device scale are pinned to (100,100) — FCM's context
            // menus collapse at any session scale above 100% (Microsoft won't-fix), and the client-side zoom
            // REQUIRES the session at 100% (readability comes from ZoomLevel, never from the session scale).
            // This connect-time block and ResizeRemote are the ONLY two readers of LocalScale(); never remove
            // either. Must be set before Connect, via the extended settings.
            (uint desktopScale, uint deviceScale) = LocalScale();
            if (_rdp.GetOcx() is IMsRdpExtendedSettings ext)
            {
                object desktop = desktopScale;
                ext.set_Property("DesktopScaleFactor", ref desktop);
                object device = deviceScale;
                ext.set_Property("DeviceScaleFactor", ref device);
            }

            if (_rdp.GetOcx() is IMsTscNonScriptable nonScriptable)
            {
                nonScriptable.ClearTextPassword = s.Password;
            }

            _rdp.Connect();
            _instr.ReportTimed("Connect(setup+connect)", setupStopwatch.ElapsedMilliseconds);
            SetStatus(RdpConnectionState.Connecting, "Connecting…");
        }
        catch (Exception ex)
        {
            _instr.ReportTimed("Connect(setup+connect)", setupStopwatch.ElapsedMilliseconds);
            SetStatus(RdpConnectionState.Failed, $"Couldn't start the session: {ex.Message}");
        }
    }

    private void OnReconnect(object sender, RoutedEventArgs e) => OnReconnectRequested();

    private void OnReconnectRequested()
    {
        if (_rdp is null)
        {
            return;
        }

        // After a transient drop the control's own auto-reconnect (EnableAutoReconnect) often brings the session
        // back, so by the time the user clicks Reconnect it may already be live. Rebuilding a still-live control
        // would tear down a working session — so when it's still connected/connecting, just resync the status bar
        // to the control's real state (clears the stale error/overlay); only rebuild when it's actually down.
        AxMsRdpClient9NotSafeForScripting reconnectRdp = _rdp; // non-null local for the instrument lambda
        int connected = _instr.Timed("Connected(reconnect)", () => reconnectRdp.Connected);
        if (connected != 0)
        {
            bool live = connected == 1;
            SetStatus(live ? RdpConnectionState.Connected : RdpConnectionState.Connecting,
                live ? "Connected." : "Reconnecting…");
            return;
        }

        // Fully down: the OCX can't be reliably re-Connect()ed after a drop, so tear it down and build a FRESH
        // control on the same creation path, then reconnect with the same per-host settings (_vm.Settings,
        // resolved once when the session was opened). This keeps reconnect and first-connect identical.
        TearDownControl();
        _connected = false;
        _connectStarted = false;
        CreateControl();
        Connect();
    }

    // ==================================================================================================
    // The re-fit engine: verified, spaced, retried size changes.
    // ==================================================================================================

    private void OnHostContainerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyHostSize();                        // resize the host/control — or STASH it while a drag is live
        _lastSizeChangedAt = DateTime.UtcNow;   // the verify must not fight a live drag
        BumpIntent();                           // new size intent: supersede any pending verify/retries
        _resizeTimer.Stop();                    // ...and re-fit once resizing settles
        _resizeTimer.Start();
    }

    /// <summary>A new size INTENT: supersedes pending verifies and their retry budget. Every path that wants
    /// the session at a (potentially) new size goes through here — settle, kicks, full-screen transitions.</summary>
    private void BumpIntent()
    {
        _refitGeneration++;
        _retryCount = 0;
        _verifyTimer.Stop();
    }

    /// <summary>Kicks one debounced settle (a size intent). Idempotent — used post-login (absorbs the
    /// status-bar collapse), on auto-reconnect, on visible-change, and on window restore.</summary>
    private void KickSettle()
    {
        BumpIntent();
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void OnResizeSettled(object? sender, EventArgs e)
    {
        _resizeTimer.Stop();
        TryApplyPendingHostSize(); // third apply trigger for a drag-stashed host size (belt and braces)
        TrySendRefit();
    }

    /// <summary>The one path to a re-fit send. Enforces the guards (connected, not minimized, sane pane,
    /// hands off — no live drag, no held mouse button) and the minimum inter-send spacing (back-to-back sends
    /// are the proven drop trigger) — blocked sends reschedule the verify instead of dying, so the chain
    /// never silently ends.</summary>
    private void TrySendRefit()
    {
        if (_closing || _rdp is null || _legacyServer)
        {
            return; // legacy sessions are engine-dormant: connect-time size + SmartSizing, like today
        }

        AxMsRdpClient9NotSafeForScripting rdp = _rdp; // non-null local so instrument lambdas stay warning-free
        if (_instr.Timed("Connected(send-guard)", () => rdp.Connected) != 1 || IsHostWindowMinimized() || IsPaneDegenerate())
        {
            ScheduleVerify(VerifyBaseDelayMs); // re-check later; costs no retry budget
            return;
        }

        if ((DateTime.UtcNow - _lastSizeChangedAt).TotalMilliseconds < 450 || IsMouseButtonDown())
        {
            // Hands still on: a re-fit reconfigures the OCX's client area (SmartSizing is OFF on zoom
            // sessions), and reconfiguring under a live drag or a held button is the proven freeze /
            // stuck-pointer trigger. Defer — costs no retry budget.
            ScheduleVerify(VerifyBaseDelayMs);
            return;
        }

        double sinceLastSend = (DateTime.UtcNow - _lastSendAt).TotalMilliseconds;
        if (sinceLastSend < MinSendSpacingMs)
        {
            ScheduleVerify((int)Math.Max(100, MinSendSpacingMs - sinceLastSend + 50));
            return;
        }

        (int width, int height) = ExpectedSize();

        // Already there? The idempotent kicks (login, restore, visible-change, auto-reconnect) mostly land on
        // an unchanged pane — converge without sending, so kicks never burn send spacing or collide.
        try
        {
            (int currentWidth, int currentHeight) = _instr.Timed("DesktopWH(send-bail)", () => (rdp.DesktopWidth, rdp.DesktopHeight));
            if (currentWidth == width && currentHeight == height)
            {
                _retryCount = 0;
                if (!_fullScreen)
                {
                    ReassertZoom();
                }

                return;
            }
        }
        catch (Exception)
        {
            // Transient COM state on the read-back — fall through and send; the verify sorts it out.
        }

        ResizeRemote(width, height);
        if (_rdp is null || _legacyServer)
        {
            return; // ResizeRemote threw: the legacy rebuild has been scheduled
        }

        _lastSentSize = (width, height);
        _lastSendAt = DateTime.UtcNow;
        _sendSequence++;
        if (!_fullScreen)
        {
            ReassertZoom(); // send-time re-assert (idempotent; the verified-applied re-assert follows)
        }

        ScheduleVerify(VerifyBaseDelayMs << Math.Min(_retryCount, 2)); // backoff 700/1400/2800
    }

    private void ScheduleVerify(int delayMs)
    {
        _verifyTimer.Stop();
        _verifyTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
        _verifyTimer.Start();
    }

    private void OnVerifyTick(object? sender, EventArgs e) => VerifyNow();

    /// <summary>Reads the session size back and re-sends until it matches the CURRENT expectation (recomputed
    /// at fire time — never a value captured at send time, so a stale verify can't re-send a wrong-mode size).
    /// A re-send costs retry budget ONLY when the expectation still equals the last-sent target (genuine
    /// evidence of a dropped send); a moved target is a new intent with a fresh budget.</summary>
    private void VerifyNow()
    {
        _verifyTimer.Stop();

        if (_closing || _rdp is null || _legacyServer)
        {
            return;
        }

        AxMsRdpClient9NotSafeForScripting rdp = _rdp; // non-null local so instrument lambdas stay warning-free
        if (_instr.Timed("Connected(verify-guard)", () => rdp.Connected) != 1 || IsHostWindowMinimized() || IsPaneDegenerate())
        {
            ScheduleVerify(VerifyBaseDelayMs); // guard-skips reschedule; they never consume the cap
            return;
        }

        if ((DateTime.UtcNow - _lastSizeChangedAt).TotalMilliseconds < 450)
        {
            ScheduleVerify(VerifyBaseDelayMs); // live drag in progress — let the settle own it
            return;
        }

        (int expectedWidth, int expectedHeight) = ExpectedSize();
        int actualWidth;
        int actualHeight;
        try
        {
            (actualWidth, actualHeight) = _instr.Timed("DesktopWH(verify)", () => (rdp.DesktopWidth, rdp.DesktopHeight));
        }
        catch (Exception)
        {
            ScheduleVerify(VerifyBaseDelayMs); // transient COM state — re-check later
            return;
        }

        if (actualWidth == expectedWidth && actualHeight == expectedHeight)
        {
            _retryCount = 0;
            if (!_fullScreen)
            {
                ReassertZoom(); // applied-anchored re-assert: zoom re-established only on a landed size
            }

            return; // converged
        }

        if ((expectedWidth, expectedHeight) != _lastSentSize)
        {
            // The target moved since the last send (pane changed, mode changed): a new intent, not a drop.
            _retryCount = 0;
            TrySendRefit();
            return;
        }

        if (_retryCount >= MaxRefitRetries)
        {
            LatchRefitWarning((expectedWidth, expectedHeight), (actualWidth, actualHeight));
            return; // engine stops until the next user-driven intent
        }

        _retryCount++;
        TrySendRefit();
    }

    // The OCX raises this when the session size ACTUALLY changes — the applied signal. Queued bodies bail if
    // any send happened after the event was raised (the verify would be judging a superseded state).
    private void OnRdpRemoteDesktopSizeChange(object? sender, IMsTscAxEvents_OnRemoteDesktopSizeChangeEvent e)
    {
        int sequenceAtEvent = _sendSequence;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing || _rdp is null || sequenceAtEvent != _sendSequence)
            {
                return;
            }

            VerifyNow();
        });
    }

    /// <summary>Live resolution change (RDP 8.1+): the remote desktop reflows to the new size with NO reconnect
    /// (unlike Reconnect(), which protocol-errors on some servers). The ONLY UpdateSessionDisplaySettings call
    /// site, and one of the two LocalScale() readers (THE PIN CARDINAL — always sends 100,100). A throw means
    /// the server has no dynamic resize (pre-8.1): the session flips to the sticky legacy mode and is rebuilt
    /// once at the physical size with SmartSizing — today's fill-but-compact behavior.</summary>
    private void ResizeRemote(int width, int height)
    {
        if (_rdp is not { } rdp || _instr.Timed("Connected(resize)", () => rdp.Connected) != 1)
        {
            return;
        }

        (uint desktopScale, uint deviceScale) = LocalScale();
        try
        {
            _instr.RecordLanded($"send {width}x{height}"); // THROWAWAY freeze instrument (manual stopwatch:
            var sendStopwatch = System.Diagnostics.Stopwatch.StartNew(); // a lambda would rename the receiver
            try                                                          // and break the cardinal gate grep)
            {
                _rdp.UpdateSessionDisplaySettings((uint)width, (uint)height, 0, 0, 0, desktopScale, deviceScale);
            }
            finally
            {
                _instr.ReportTimed("UpdateSessionDisplaySettings", sendStopwatch.ElapsedMilliseconds);
            }
        }
        catch (Exception)
        {
            OnRefitThrew();
        }
    }

    /// <summary>The remote session's display + device scale, both pinned to 100 (100%). THE PIN CARDINAL: the
    /// Failover Cluster Manager context-menu bug trips at any session scale above 100% (Microsoft won't-fix),
    /// and the client-side zoom requires the session at 100% — readability comes from ZoomLevel, never from
    /// this. Read by the connect-time extended-settings block and by ResizeRemote (every re-fit, including
    /// verify re-sends), so the session stays at 100% on connect and on every resize.</summary>
    private (uint Desktop, uint Device) LocalScale()
    {
        return (100u, 100u);
    }

    /// <summary>Pre-8.1 server (UpdateSessionDisplaySettings threw): set the sticky legacy flag and rebuild
    /// ONCE at the degraded physical size with SmartSizing — exactly today's shipping behavior for old servers.
    /// Because the post-login kick always fires, an old server throws seconds after first connect, so the one
    /// rebuild happens at session start, not mid-work.</summary>
    private void OnRefitThrew()
    {
        if (_legacyServer)
        {
            return; // already known — engine is dormant; nothing sends again
        }

        _legacyServer = true;
        _verifyTimer.Stop();
        if (_vm is { } vm)
        {
            (int width, int height) = DegradedPixelSize();
            vm.Log.Info(vm.Title,
                $"This server doesn't support live resolution changes (pre-RDP-8.1) — reconnecting once at " +
                $"{width}x{height} with scale-to-fit. Zoom is off for this session.");
        }

        // Defer the rebuild so the OCX's COM stack unwinds first (same rule as the deliberate-close path).
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing || _rdp is null)
            {
                return;
            }

            _instr.RecordLanded("rebuild(legacy)");
            TearDownControl();
            _connected = false;
            _connectStarted = false;
            CreateControl();
            Connect();
        });
    }

    // ==================================================================================================
    // Sizes: the THREE computing sites. The even-both-dims rule and the protocol floors live inside them
    // so no call site can bypass either. Width MUST NOT be odd and MUST be >= 200 (MS-RDPEDISP) — an odd
    // width is silently dropped by the server while the API reports success; every applied re-fit ever
    // recorded also had an even height, so height gets the same floor at zero cost.
    // ==================================================================================================

    /// <summary>The current size the session SHOULD be, from current state — full-screen, degraded/legacy, or
    /// normal windowed. The verify recomputes this at fire time, so stale timers can't enforce old targets.</summary>
    private (int Width, int Height) ExpectedSize() =>
        _fullScreen ? MonitorPixelSize()
        : _degraded || _legacyServer ? DegradedPixelSize()
        : RemotePixelSize();

    /// <summary>Windowed framebuffer: the pane size in LOGICAL units (DIPs as-is) — the session renders at
    /// 100% into this and ZoomLevel magnifies it to fill the physical pane.</summary>
    private (int Width, int Height) RemotePixelSize()
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        int width = Math.Max(Math.Max(200, (int)Math.Ceiling(640 / dpi.DpiScaleX)), (int)Math.Round(HostContainer.ActualWidth));
        int height = Math.Max(Math.Max(200, (int)Math.Ceiling(480 / dpi.DpiScaleY)), (int)Math.Round(HostContainer.ActualHeight));
        return (width & ~1, height & ~1);
    }

    /// <summary>Degraded/legacy framebuffer: the pane size in PHYSICAL pixels (today's shipping model) —
    /// SmartSizing fills, image is compact, never worse than the pre-zoom baseline.</summary>
    private (int Width, int Height) DegradedPixelSize()
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        int width = Math.Max(640, (int)Math.Round(HostContainer.ActualWidth * dpi.DpiScaleX));
        int height = Math.Max(480, (int)Math.Round(HostContainer.ActualHeight * dpi.DpiScaleY));
        return (width & ~1, height & ~1);
    }

    /// <summary>The pixel size of the monitor the session is on — the resolution to request when full-screen
    /// (ZoomLevel has no effect in full-screen; it renders crisp-filled-compact, as it always has).</summary>
    private (int Width, int Height) MonitorPixelSize()
    {
        if (_rdp is not null)
        {
            System.Drawing.Rectangle bounds = System.Windows.Forms.Screen.FromControl(_rdp).Bounds;
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                return (bounds.Width & ~1, bounds.Height & ~1);
            }
        }

        return _degraded || _legacyServer ? DegradedPixelSize() : RemotePixelSize();
    }

    private bool IsHostWindowMinimized() => _hostWindow?.WindowState == WindowState.Minimized;

    private bool IsPaneDegenerate() =>
        !_fullScreen && (HostContainer.ActualWidth < 50 || HostContainer.ActualHeight < 50);

    /// <summary>Any mouse button held, anywhere — read via the async key state because the OCX has its own
    /// HWND (WPF's Mouse class can't see a button held inside the session) and a border drag runs the modal
    /// resize loop (no WPF input events at all while the border is held still).</summary>
    private static bool IsMouseButtonDown() =>
        (GetAsyncKeyState(0x01) & 0x8000) != 0 ||   // VK_LBUTTON
        (GetAsyncKeyState(0x02) & 0x8000) != 0 ||   // VK_RBUTTON
        (GetAsyncKeyState(0x04) & 0x8000) != 0;     // VK_MBUTTON

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    // ==================================================================================================
    // Client-side zoom (the OCX's documented ZoomLevel — mstsc's System-menu Zoom).
    // ==================================================================================================

    /// <summary>The zoom percent for this display: the real scale, snapped DOWN to the mstsc ladder (only
    /// ladder values are field-proven; a rejected value would strand a logical framebuffer un-zoomed).</summary>
    private uint DeriveZoomPercent()
    {
        int raw = (int)Math.Round(System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX * 100);
        int clamped = Math.Clamp(raw, 100, 500);
        uint snapped = 100;
        foreach (uint step in ZoomLadder)
        {
            if (step <= clamped)
            {
                snapped = step;
            }
            else
            {
                break;
            }
        }

        return snapped;
    }

    /// <summary>Sets ZoomLevel and VERIFIES it via read-back (a clean return proves nothing — the lesson of
    /// this feature's whole investigation). Gated INSIDE the helper: no call site can poke ZoomLevel on a
    /// 100%-scale, degraded, legacy, or full-screen session (SmartSizing and ZoomLevel are mutually
    /// exclusive). A failed set/read-back degrades the session to today's fill-but-compact model.</summary>
    private void ReassertZoom()
    {
        if (_zoomPercent <= 100 || _degraded || _legacyServer || _fullScreen || _rdp is not { } rdp
            || _instr.Timed("Connected(zoom-guard)", () => rdp.Connected) != 1)
        {
            return;
        }

        try
        {
            if (rdp.GetOcx() is not IMsRdpExtendedSettings ext)
            {
                DegradeZoom("the control exposes no extended settings");
                return;
            }

            _instr.RecordLanded($"ZoomLevel.set({_zoomPercent}) reassert");
            object zoom = _zoomPercent;
            _instr.Timed("ZoomLevel.set(reassert)", () => { ext.set_Property("ZoomLevel", ref zoom); });
            object readBack = _instr.Timed("ZoomLevel.get(readback)", () => ext.get_Property("ZoomLevel"));
            if (readBack is not uint applied || applied != _zoomPercent)
            {
                DegradeZoom($"requested {_zoomPercent}, control reports {readBack ?? "null"}");
            }
        }
        catch (Exception ex)
        {
            DegradeZoom($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Zoom failed on this session: reset ZoomLevel to 100 FIRST (mutual exclusivity), then enable
    /// SmartSizing, flag degraded, and re-fit to the physical size — today's fill-but-compact, never worse
    /// than shipping. One latched warning with the numbers (post-ship forensics).</summary>
    private void DegradeZoom(string reason)
    {
        if (_degraded)
        {
            return;
        }

        _degraded = true;

        if (_rdp is { } rdp)
        {
            try
            {
                if (rdp.GetOcx() is IMsRdpExtendedSettings ext)
                {
                    _instr.RecordLanded("ZoomLevel.set(100) degrade");
                    object zoom = 100u;
                    _instr.Timed("ZoomLevel.set(degrade)", () => { ext.set_Property("ZoomLevel", ref zoom); });
                }

                _instr.RecordLanded("SmartSizing.set(true) degrade");
                _instr.Timed("SmartSizing.set(degrade)", () => { rdp.AdvancedSettings9.SmartSizing = true; });
            }
            catch (Exception)
            {
                // Best effort — the degraded re-fit below still moves the framebuffer to physical, and
                // SmartSizing state only affects how an interim mismatch looks.
            }
        }

        if (!_zoomWarnLatched && _vm is { } vm)
        {
            _zoomWarnLatched = true;
            (int width, int height) = DegradedPixelSize();
            vm.Log.Warn(vm.Title,
                $"Client-side zoom couldn't be applied ({reason}) — falling back to the compact view " +
                $"({width}x{height}, pane {(int)HostContainer.ActualWidth}x{(int)HostContainer.ActualHeight} DIPs).");
        }

        KickSettle(); // the engine re-fits to the degraded (physical) size via the normal verified path
    }

    private void LatchRefitWarning((int Width, int Height) expected, (int Width, int Height) actual)
    {
        if (_refitWarnLatched)
        {
            return;
        }

        _refitWarnLatched = true;
        if (_vm is { } vm)
        {
            vm.Log.Warn(vm.Title,
                $"The remote session didn't take the new size after {MaxRefitRetries} retries — expected " +
                $"{expected.Width}x{expected.Height}, session reports {actual.Width}x{actual.Height}, pane " +
                $"{(int)HostContainer.ActualWidth}x{(int)HostContainer.ActualHeight} DIPs, zoom {_zoomPercent}. " +
                $"The image may show borders until the window is resized again.");
        }
    }

    // ==================================================================================================
    // Lifecycle kicks: every path that can leave the session at a stale size gets a re-fit + zoom re-assert.
    // ==================================================================================================

    private void OnRdpLoginComplete(object? sender, EventArgs e)
    {
        _connected = true;
        SetStatus(RdpConnectionState.Connected, "Connected.");

        // Marshal per the file's rule (control events can arrive off the UI thread); the deferred body
        // re-checks teardown state before touching COM. The kick absorbs the status-bar collapse: the bar
        // hides on Connected, the pane grows, and the settle re-fits the session to the final size.
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing || _rdp is null)
            {
                return;
            }

            ReassertZoom();
            KickSettle();
        });
    }

    // The control's own auto-reconnect recovered a blip WITHOUT the rebuild path — nothing re-negotiated, so
    // re-assert zoom and re-fit in case the recovery reset either.
    private void OnRdpAutoReconnected(object? sender, EventArgs e)
    {
        SetStatus(RdpConnectionState.Connected, "Connected.");
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing || _rdp is null)
            {
                return;
            }

            ReassertZoom();
            KickSettle();
        });
    }

    // Hidden sessions (Visibility-collapsed by the keep-alive ItemsControl) can connect or auto-reconnect
    // against stale layout — converge when the view becomes visible. The kick runs the DEBOUNCED settle, so
    // the size is read after the un-hide layout pass.
    private void OnViewVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && _rdp is not null && _connected && !_closing)
        {
            ReassertZoom();
            KickSettle();
        }
    }

    // Minimize→restore to IDENTICAL bounds fires no SizeChanged — this hook is the only converger there.
    private void OnHostWindowStateChanged(object? sender, EventArgs e)
    {
        if (_hostWindow?.WindowState != WindowState.Minimized && _rdp is not null && _connected && !_closing)
        {
            KickSettle();
        }
    }

    // ==================================================================================================
    // Full-screen: monitor-physical resolution, zoom parked at 100 BEFORE entry in the VM sync handler
    // (mstsc's own order — mstsc resets its zoom before switching, and a live zoom at the moment of entry is
    // the prime suspect for the OCX refusing to enter at all; ZoomLevel is inert in full-screen per the
    // docs). Zoom is restored by the verify's applied-anchored re-assert after the leave re-fit lands, or
    // immediately when an entry attempt fails.
    // ==================================================================================================

    private void OnEnterFullScreen(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing || _rdp is null)
            {
                return;
            }

            _fullScreen = true;
            BumpIntent();
            TrySendRefit(); // expectation is now monitor-physical (zoom was parked before entry)
            SetFullScreenFlag(true);
        });
    }

    private void OnLeaveFullScreen(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing || _rdp is null)
            {
                return;
            }

            _fullScreen = false;
            BumpIntent();
            TrySendRefit(); // back to windowed; zoom re-asserts when the verify confirms the size landed
            SetFullScreenFlag(false);
        });
    }

    /// <summary>Best-effort ZoomLevel → 100 BEFORE a full-screen entry attempt (mstsc's own order). A park
    /// failure doesn't block the attempt and gets no degrade/warning — a refused ENTRY is what's caught,
    /// logged, and recovered in <see cref="OnViewModelPropertyChanged"/>.</summary>
    private void ParkZoomForFullScreen()
    {
        if (_zoomPercent <= 100 || _degraded || _legacyServer || _rdp is not { } rdp)
        {
            return;
        }

        try
        {
            if (rdp.GetOcx() is IMsRdpExtendedSettings ext)
            {
                _instr.RecordLanded("ZoomLevel.set(100) park");
                object zoom = 100u;
                _instr.Timed("ZoomLevel.set(park)", () => { ext.set_Property("ZoomLevel", ref zoom); });
            }
        }
        catch (Exception)
        {
            // Inert-in-full-screen either way; the leave path re-asserts through the verified helper.
        }
    }

    // ==================================================================================================
    // WPF host plumbing + status/state handlers (unchanged model).
    // ==================================================================================================

    /// <summary>WindowsFormsHost ignores Stretch and otherwise sizes to the hosted control's default — so set
    /// its size explicitly from its WPF container (DIPs; the host converts to device pixels and sizes the
    /// Dock=Fill panel/control to match).
    /// <para>THE DRAG RULE: never push a new size into the OCX HWND while the size is still moving. Resizing
    /// the hosted HWND makes the control repaint its whole image synchronously on the UI thread (with
    /// client-side zoom, a full rescale of the framebuffer), and a border drag fires SizeChanged dozens of
    /// times a second — pushing per tick froze the app for the length of the drag (measured: 12.4s blocked,
    /// ZERO COM calls — a pure render stall; master, which repaints 1:1 with SmartSizing, does not freeze).
    /// While the window is inside its modal size loop OR a physical mouse button is held (a GridSplitter
    /// drag raises no modal messages — the button is the only signal), the size is STASHED and applied once
    /// on settle: the same discipline as the send debounce, one layer down, applied to the control layout
    /// instead of the session resize. The pane deliberately shows a stale image during the drag and snaps
    /// on release.</para></summary>
    private void ApplyHostSize()
    {
        if (HostContainer.ActualWidth <= 0 || HostContainer.ActualHeight <= 0)
        {
            return;
        }

        if (_windowSizeMoveLoop || IsMouseButtonDown())
        {
            _pendingHostSize = new Size(HostContainer.ActualWidth, HostContainer.ActualHeight);
            _hostSizePoll.Start(); // applied on button release; WM_EXITSIZEMOVE and the settle also trigger
            return;
        }

        _pendingHostSize = null;
        RdpHostElement.Width = HostContainer.ActualWidth;
        RdpHostElement.Height = HostContainer.ActualHeight;
    }

    /// <summary>Applies the drag-stashed host size once hands are off. Triggered by ALL of: WM_EXITSIZEMOVE,
    /// the 200ms poll (a GridSplitter/button release has no modal message — the poll is its only trigger),
    /// and the resize settle — belt and braces, so the OCX is never left stranded at a stale size if one
    /// trigger misses. Applies the CURRENT container size (always at least as fresh as the stash), then
    /// kicks one settle so the session converges through the normal verified path.</summary>
    private void TryApplyPendingHostSize()
    {
        if (_pendingHostSize is null)
        {
            _hostSizePoll.Stop();
            return;
        }

        if (_windowSizeMoveLoop || IsMouseButtonDown())
        {
            return; // still dragging — keep polling
        }

        _hostSizePoll.Stop();
        _pendingHostSize = null;
        ApplyHostSize();
        KickSettle();
    }

    private void OnHostSizePollTick(object? sender, EventArgs e) => TryApplyPendingHostSize();

    // The window's modal size/move loop markers — the primary suppress window for ApplyHostSize. A pure
    // title-bar MOVE also raises these; no SizeChanged fires then, so nothing stashes and EXIT is a no-op.
    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmEnterSizeMove = 0x0231;
        const int WmExitSizeMove = 0x0232;
        if (msg == WmEnterSizeMove)
        {
            _windowSizeMoveLoop = true;
        }
        else if (msg == WmExitSizeMove)
        {
            _windowSizeMoveLoop = false;
            TryApplyPendingHostSize();
        }

        return IntPtr.Zero;
    }

    /// <summary>Syncs the VM's FullScreen flag (the toolbar sets it true) to the control, in mstsc's own
    /// order: zoom parked to 100 BEFORE the switch. A switch that doesn't take — a throw, a silent refusal,
    /// or the session being down — is LOGGED (the old silent catch hid a dead full-screen button completely)
    /// and the flag is put back to the control's real state, so the next click raises a fresh change instead
    /// of a dead true→true no-op (the one-click latch, guarded in BOTH directions).</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RdpSessionViewModel.FullScreen) || _rdp is null || _vm is null
            || _syncingFullScreen)
        {
            return;
        }

        AxMsRdpClient9NotSafeForScripting rdp = _rdp; // non-null local so instrument lambdas stay warning-free
        bool wanted = _vm.FullScreen;
        try
        {
            if (_instr.Timed("Connected(fs)", () => rdp.Connected) == 0)
            {
                SetFullScreenFlag(!wanted); // session down: no switch happened — un-latch for the next click
                return;
            }

            if (wanted)
            {
                ParkZoomForFullScreen();
            }

            _instr.RecordLanded($"FullScreen.set({wanted})");
            _instr.Timed("FullScreen.set", () => { rdp.FullScreen = wanted; });
            if (_instr.Timed("FullScreen.get(readback)", () => rdp.FullScreen) == wanted)
            {
                return; // took — OnEnter/OnLeaveFullScreen drive the re-fit from here
            }

            OnFullScreenSwitchFailed(wanted, null);
        }
        catch (Exception ex)
        {
            OnFullScreenSwitchFailed(wanted, ex);
        }
    }

    /// <summary>A full-screen switch didn't take: one Warn with the OCX state (zoom / SmartSizing /
    /// Connected), the VM flag back to the control's real state (the un-latch — both directions), and on a
    /// failed ENTRY the zoom re-asserted (the park had already dropped it on a still-windowed session).</summary>
    private void OnFullScreenSwitchFailed(bool wanted, Exception? ex)
    {
        if (_vm is { } vm)
        {
            string error = ex is null ? "the control refused without an error" : $"{ex.GetType().Name}: {ex.Message}";
            vm.Log.Warn(vm.Title,
                $"Full screen {(wanted ? "entry" : "exit")} didn't take ({error}) — {DescribeOcxState()}. " +
                "Click Full screen to try again.");
        }

        SetFullScreenFlag(!wanted);
        if (wanted)
        {
            ReassertZoom();
        }
    }

    /// <summary>Best-effort OCX state for the full-screen failure Warn — each read independently guarded so
    /// a dead control still yields a useful line ("?" marks what couldn't be read).</summary>
    private string DescribeOcxState()
    {
        string zoom = "?";
        string smartSizing = "?";
        string connected = "?";
        try
        {
            connected = _instr.Timed("Connected(state)", () => _rdp?.Connected.ToString() ?? "gone");
        }
        catch (Exception)
        {
            // Best-effort read for the log line only.
        }

        try
        {
            smartSizing = _instr.Timed("SmartSizing.get(state)", () => _rdp?.AdvancedSettings9.SmartSizing.ToString() ?? "?");
        }
        catch (Exception)
        {
            // Best-effort read for the log line only.
        }

        try
        {
            if (_rdp?.GetOcx() is IMsRdpExtendedSettings ext)
            {
                zoom = _instr.Timed("ZoomLevel.get(state)", () => ext.get_Property("ZoomLevel")?.ToString() ?? "null");
            }
        }
        catch (Exception)
        {
            // Best-effort read for the log line only.
        }

        return $"zoom {zoom}, SmartSizing {smartSizing}, Connected {connected}";
    }

    private void OnRdpConnecting(object? sender, EventArgs e) =>
        SetStatus(RdpConnectionState.Connecting, "Connecting…");

    private void OnRdpConnected(object? sender, EventArgs e) =>
        SetStatus(RdpConnectionState.Connecting, "Authenticating…");

    private void OnRdpDisconnected(object? sender, IMsTscAxEvents_OnDisconnectedEvent e)
    {
        if (_closing)
        {
            return; // we're tearing the control down ourselves
        }

        // Only a DELIBERATE sign-out closes the tab (like mstsc closing its window on logoff). An INVOLUNTARY
        // drop — network blip, server reboot, idle timeout, admin disconnect, protocol error — KEEPS the session
        // + tab open in a disconnected state with the Reconnect button enabled, so the operator can rebuild it.
        // (Brief blips are usually recovered first by the control's own auto-reconnect.)
        if (_connected && IsUserLogoff())
        {
            // Defer the close so the control's own disconnect callback unwinds before we dispose it.
            Dispatcher.BeginInvoke(() => _vm?.RequestClose());
            return;
        }

        string message = DescribeDisconnect(e.discReason);

        // The classic cross-domain stumbling block: a server on another domain rejects the credentials with
        // NLA on. Nudge toward the fix (turning NLA off) instead of leaving the user guessing.
        if (_vm is { Settings.NlaEnabled: true } && LooksLikeAuthFailure(e.discReason))
        {
            message += "  If this is a cross-domain host, edit it, turn NLA off, and reconnect.";
        }

        SetStatus(RdpConnectionState.Disconnected, message);
    }

    private void OnRdpFatalError(object? sender, IMsTscAxEvents_OnFatalErrorEvent e) =>
        SetStatus(RdpConnectionState.Failed, $"RDP fatal error (code {e.errorCode}).");

    // The control's built-in auto-reconnect (after a network blip) — keep the status bar honest so it clears
    // itself when the session comes back, instead of stranding a stale "disconnected" bar over a live desktop.
    private void OnRdpAutoReconnecting(object? sender, IMsTscAxEvents_OnAutoReconnectingEvent e) =>
        SetStatus(RdpConnectionState.Connecting, "Reconnecting…");

    private string DescribeDisconnect(int reason)
    {
        if (_rdp is not { } rdp)
        {
            return $"Disconnected (reason {reason}).";
        }

        try
        {
            string? description = _instr.Timed("GetErrorDescription", () =>
                rdp.GetErrorDescription((uint)reason, (uint)rdp.ExtendedDisconnectReason));
            return string.IsNullOrWhiteSpace(description) ? $"Disconnected (reason {reason})." : description;
        }
        catch (Exception)
        {
            return $"Disconnected (reason {reason}).";
        }
    }

    // Heuristic for the NLA hint — credential/authentication-ish disconnect reasons. Advisory only.
    private static bool LooksLikeAuthFailure(int reason) =>
        reason is 2825 or 3334 or 2055 or 5639 or 264 or 516;

    /// <summary>True when the disconnect was a DELIBERATE sign-out (the session ended on purpose), read from the
    /// control's <c>ExtendedDisconnectReason</c>: API-initiated logoff (2), server-initiated logoff (4), or
    /// logoff-by-user (6). Everything else (network / idle timeout / server- or admin-initiated disconnect /
    /// protocol error) is treated as involuntary, so the session stays open for Reconnect.</summary>
    private bool IsUserLogoff()
    {
        if (_rdp is not { } rdp)
        {
            return false;
        }

        try
        {
            // ExtendedDisconnectReasonCode → int: 2 = API-initiated logoff, 4 = server-initiated logoff,
            // 6 = logoff-by-user. Everything else (network / idle / disconnect / error) is an involuntary drop.
            int reason = _instr.Timed("ExtDiscReason(logoff)", () => (int)rdp.ExtendedDisconnectReason);
            return reason is 2 or 4 or 6;
        }
        catch (Exception)
        {
            return false; // can't read the reason → treat as involuntary (keep the session open)
        }
    }

    /// <summary>Mirrors the control's REAL full-screen state into the VM (marshaled). The write is
    /// suppression-flagged: the mirror must never poke the OCX back through the sync handler (a revert after
    /// a failed switch would otherwise re-enter the very switch that just failed).</summary>
    private void SetFullScreenFlag(bool value)
    {
        if (_vm is not null)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_vm is null)
                {
                    return;
                }

                _syncingFullScreen = true;
                try
                {
                    _vm.FullScreen = value;
                }
                finally
                {
                    _syncingFullScreen = false;
                }
            });
        }
    }

    private void SetStatus(RdpConnectionState state, string text)
    {
        if (_vm is null)
        {
            return;
        }

        // RDP control events can arrive off the UI thread — marshal before touching the bound VM. BeginInvoke
        // (not Invoke) so an event raised while the UI thread is busy in a COM call can't deadlock.
        Dispatcher.BeginInvoke(() =>
        {
            _vm.State = state;
            _vm.StatusText = text;
        });
    }

    /// <summary>Disconnects and tears the control down (called via Unloaded — see the ctor).</summary>
    public void DisposeSession()
    {
        _closing = true;
        _instr.Stop(); // THROWAWAY freeze instrument
        _resizeTimer.Stop();
        _verifyTimer.Stop();
        _hostSizePoll.Stop();
        _windowSource?.RemoveHook(OnWindowMessage);
        _windowSource = null;

        if (_hostWindow is not null)
        {
            _hostWindow.StateChanged -= OnHostWindowStateChanged;
            _hostWindow = null;
        }

        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.ReconnectRequested -= OnReconnectRequested;
        }

        TearDownControl();
    }
}
