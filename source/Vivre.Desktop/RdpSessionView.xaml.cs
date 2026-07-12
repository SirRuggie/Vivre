using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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
/// Sizing follows the proven pattern (Microsoft's RDPinWpf sample + the WindowsFormsHost layout docs): the Ax
/// control is <c>Dock=Fill</c> inside an intermediate WinForms <see cref="System.Windows.Forms.Panel"/>, and
/// that Panel is the host's <c>Child</c> — so the host sizes the (flexible) Panel rather than the ActiveX
/// wrapper, which has a ~150px default it won't shrink below. WindowsFormsHost ignores Stretch, so we set its
/// Width/Height explicitly from its WPF container on every resize. We never size the Ax control ourselves; we
/// only reconnect the remote desktop to the new pixel size so it stays pixel-accurate (no scaling/letterbox).
/// </remarks>
public partial class RdpSessionView : UserControl
{
    private AxMsRdpClient9NotSafeForScripting? _rdp;
    private System.Windows.Forms.Panel? _hostPanel;
    private RdpSessionViewModel? _vm;
    private bool _connectStarted;
    private bool _connected; // reached the remote desktop at least once (login completed)
    private bool _closing;   // tearing the control down ourselves — ignore its own disconnect event
    private readonly System.Windows.Threading.DispatcherTimer _resizeTimer;

    // SPIKE-0B - REMOVE: embedded ZoomLevel verification state (Step 0b). Default zoom is derived
    // from the actual display scale in OnLoaded (150 on the jump box).
    private uint _spikeZoom = 100;
    private string _spikeRefit = "refit=none";
    private string _spikeZoomStatus = "zoom=none";
    private string _spikeFbConnect = "?";           // EXACTLY what DesktopWidth/Height received at connect
    private string _spikeManualRefit = string.Empty; // timestamp proof the Re-fit button fired

    // SPIKE-0B - REMOVE: fallback-mode toggle (static so a NEW session picks it up — EnableZoom is
    // write-only and NOT changeable after connect, so the mode must be chosen before Connect()).
    // Bare-zoom mode: SmartSizing OFF + ZoomLevel post-login. EnableZoom mode: SmartSizing ON +
    // EnableZoom=true pre-connect — the documented unlock for SmartSizing UPSCALING, carrying
    // SmartSizing's proven input mapping (round-1 V2 was the only clickable variant).
    private static bool _spikeEnableZoomMode;

    public RdpSessionView()
    {
        InitializeComponent();
        // Debounce resizes: re-fit the actual remote resolution to the new pane size once resizing settles.
        _resizeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _resizeTimer.Tick += OnResizeSettled;
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

        // SPIKE-0B - REMOVE: derive the default zoom from the actual display scale; sync the mode
        // checkbox with the process-wide toggle.
        _spikeZoom = (uint)Math.Round(System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX * 100);
        SpikeEnableZoomCheck.IsChecked = _spikeEnableZoomMode;

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

        _rdp.OnConnecting += OnRdpConnecting;
        _rdp.OnConnected += OnRdpConnected;
        _rdp.OnLoginComplete += OnRdpLoginComplete;
        _rdp.OnDisconnected += OnRdpDisconnected;
        _rdp.OnFatalError += OnRdpFatalError;
        _rdp.OnAutoReconnecting += OnRdpAutoReconnecting;
        _rdp.OnAutoReconnected += OnRdpAutoReconnected;
        _rdp.OnEnterFullScreenMode += OnEnterFullScreen;
        _rdp.OnLeaveFullScreenMode += OnLeaveFullScreen;
    }

    /// <summary>Unsubscribes the control's events (BEFORE disconnecting, so its Disconnect() can't re-enter our
    /// handlers), disconnects if live, then disposes the control + host panel. Used by Reconnect (rebuild) and
    /// DisposeSession (final teardown).</summary>
    private void TearDownControl()
    {
        if (_rdp is null)
        {
            return;
        }

        _rdp.OnConnecting -= OnRdpConnecting;
        _rdp.OnConnected -= OnRdpConnected;
        _rdp.OnLoginComplete -= OnRdpLoginComplete;
        _rdp.OnDisconnected -= OnRdpDisconnected;
        _rdp.OnFatalError -= OnRdpFatalError;
        _rdp.OnAutoReconnecting -= OnRdpAutoReconnecting;
        _rdp.OnAutoReconnected -= OnRdpAutoReconnected;
        _rdp.OnEnterFullScreenMode -= OnEnterFullScreen;
        _rdp.OnLeaveFullScreenMode -= OnLeaveFullScreen;

        try
        {
            if (_rdp.Connected != 0)
            {
                _rdp.Disconnect();
            }
        }
        catch (Exception)
        {
            // Best-effort teardown: the control may already be disconnecting.
        }

        RdpHostElement.Child = null;
        _rdp.Dispose();
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
        try
        {
            _rdp.Server = s.Server;
            _rdp.AdvancedSettings9.RDPPort = s.Port;
            _rdp.UserName = s.UserName;
            _rdp.Domain = s.Domain ?? string.Empty;

            // Connect at the pane's size in physical pixels so the remote matches it 1:1 (no scaling/letterbox);
            // we reconnect to the new size on resize (OnResizeSettled). The control bitmap fills via Dock=Fill.
            (int width, int height) = RemotePixelSize();
            _rdp.DesktopWidth = width;
            _rdp.DesktopHeight = height;
            _spikeFbConnect = $"{width}x{height}"; // SPIKE-0B - REMOVE: the true connect-time request

            // NLA via CredSSP: on by default; OFF lets a server on another domain accept the supplied
            // credentials instead of rejecting delegated/saved ones (the cross-domain 0x2107 rejection).
            _rdp.AdvancedSettings9.EnableCredSspSupport = s.NlaEnabled;

            // SPIKE-0B - REMOVE: bare-zoom mode = SmartSizing OFF (mutually exclusive with ZoomLevel);
            // EnableZoom mode = SmartSizing ON (EnableZoom unlocks its upscaling). Shipping value is
            // true; restore when the hack is dropped.
            _rdp.AdvancedSettings9.SmartSizing = _spikeEnableZoomMode;
            _rdp.AdvancedSettings9.ContainerHandledFullScreen = 0;

            // Let the client transparently recover a transient drop (brief network blip) on its own before we
            // surface a disconnect; and grab keyboard/mouse focus on (re)connect so input goes to the remote.
            _rdp.AdvancedSettings9.EnableAutoReconnect = true;
            _rdp.AdvancedSettings9.GrabFocusOnConnect = true;

            // Match the remote's display scale to THIS PC's (e.g. 150%) so icons/text aren't tiny on a high-DPI
            // display — the remote's "Scale and layout". Must be set before Connect, via the extended settings.
            (uint desktopScale, uint deviceScale) = LocalScale();
            if (_rdp.GetOcx() is IMsRdpExtendedSettings ext)
            {
                object desktop = desktopScale;
                ext.set_Property("DesktopScaleFactor", ref desktop);
                object device = deviceScale;
                ext.set_Property("DeviceScaleFactor", ref device);

                // SPIKE-0B - REMOVE: EnableZoom must be set BEFORE Connect (write-only, not
                // changeable after). Guarded so an unsupported property can't fail the connect.
                if (_spikeEnableZoomMode)
                {
                    try
                    {
                        object enableZoom = true;
                        ext.set_Property("EnableZoom", ref enableZoom);
                        _spikeZoomStatus = "mode=EnableZoom+SmartSizing (set pre-connect; write-only, no read-back)";
                    }
                    catch (Exception ex)
                    {
                        _spikeZoomStatus = $"mode=EnableZoom FAILED {ex.GetType().Name}";
                    }
                }
            }

            if (_rdp.GetOcx() is IMsTscNonScriptable nonScriptable)
            {
                nonScriptable.ClearTextPassword = s.Password;
            }

            _rdp.Connect();
            SetStatus(RdpConnectionState.Connecting, "Connecting…");
            UpdateSpikeDiag(width, height); // SPIKE-0B - REMOVE
        }
        catch (Exception ex)
        {
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
        if (_rdp.Connected != 0)
        {
            bool live = _rdp.Connected == 1;
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

    private void OnHostContainerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyHostSize();      // resize the host/control now (SmartSizing scales the image meanwhile)
        _resizeTimer.Stop();  // ...and re-fit the actual remote resolution once resizing settles
        _resizeTimer.Start();
    }

    private void OnResizeSettled(object? sender, EventArgs e)
    {
        _resizeTimer.Stop();
        (int width, int height) = RemotePixelSize();
        ResizeRemote(width, height);

        // SPIKE-0B - REMOVE: re-apply the current zoom after the re-fit send (bare-zoom mode only)
        // and refresh the strip.
        if (!_spikeEnableZoomMode)
        {
            ApplySpikeZoom(_spikeZoom);
        }

        UpdateSpikeDiag(width, height);
    }

    /// <summary>Live resolution change (RDP 8.1+): the remote desktop reflows to the new size with NO reconnect
    /// (unlike Reconnect(), which protocol-errors on some servers). If the server is too old this throws and
    /// SmartSizing keeps the image scaled to fit instead — so resizing always at least looks right.</summary>
    private void ResizeRemote(int width, int height)
    {
        // SPIKE-0B: guard is Connected == 1 (shipping code uses == 0) — the OCX reports 2 while
        // connecting, and a display update sent mid-handshake fails silently (round-2 lesson).
        if (_rdp is null || _rdp.Connected != 1)
        {
            return;
        }

        (uint desktopScale, uint deviceScale) = LocalScale();
        try
        {
            _rdp.UpdateSessionDisplaySettings((uint)width, (uint)height, 0, 0, 0, desktopScale, deviceScale);
            _spikeRefit = "refit=OK"; // SPIKE-0B - REMOVE (submitted, not proven applied — read session=)
        }
        catch (Exception ex)
        {
            // Pre-8.1 / unsupported server — SmartSizing scales the image instead.
            _spikeRefit = $"refit=FAILED {ex.GetType().Name}"; // SPIKE-0B - REMOVE
        }
    }

    /// <summary>The remote session's display + device scale, both pinned to 100 (100%). The Failover Cluster
    /// Manager context-menu bug trips at any session scale above 100% (Microsoft won't-fix), so we never raise
    /// it. Fill/readability comes from the framebuffer being the pane's own pixel size (see RemotePixelSize) —
    /// the remote renders at the pane resolution and fills it natively, no scaling. Read by both Connect and the
    /// resize re-fit, so the session stays at 100% on connect and on every resize.</summary>
    private (uint Desktop, uint Device) LocalScale()
    {
        return (100u, 100u);
    }

    /// <summary>WindowsFormsHost ignores Stretch and otherwise sizes to the hosted control's default — so set
    /// its size explicitly from its WPF container (DIPs; the host converts to device pixels and sizes the
    /// Dock=Fill panel/control to match).</summary>
    private void ApplyHostSize()
    {
        if (HostContainer.ActualWidth > 0 && HostContainer.ActualHeight > 0)
        {
            RdpHostElement.Width = HostContainer.ActualWidth;
            RdpHostElement.Height = HostContainer.ActualHeight;
        }
    }

    /// <summary>SPIKE-0B: the pane size in LOGICAL units (DIPs as-is, NOT multiplied by the DPI
    /// scale) — the ZoomLevel design's framebuffer. The floor is scaled down (ceil(640/scale) x
    /// ceil(480/scale)) so the zoomed minimum footprint matches the shipping 640x480 physical.
    /// WIDTH IS FLOORED TO EVEN — HARD RULE: MS-RDPEDISP forbids odd display-control widths, and
    /// an odd width is silently dropped by the server while the API reports success (this was every
    /// "refit=OK but stale" ever recorded: 1615/2251/1801 odd = dropped; 2316/3474 even = applied).
    /// SHIPPING BODY (restore when the hack is dropped): DIPs x DpiScaleX/Y, floors 640/480 —
    /// and the even-width rule must carry into the shipping body too.</summary>
    private (int Width, int Height) RemotePixelSize()
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        int width = Math.Max((int)Math.Ceiling(640 / dpi.DpiScaleX), (int)Math.Round(HostContainer.ActualWidth));
        int height = Math.Max((int)Math.Ceiling(480 / dpi.DpiScaleY), (int)Math.Round(HostContainer.ActualHeight));
        // BOTH dims floored to even: the spec mandates even WIDTH only, but every mid-session re-fit
        // ever recorded as APPLIED had an even height too (1280x772), and 1800x1099 (legal per spec)
        // was dropped — the server's display-control path appears to require even on both axes.
        return (width & ~1, height & ~1);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RdpSessionViewModel.FullScreen) || _rdp is null || _vm is null)
        {
            return;
        }

        try
        {
            if (_rdp.Connected != 0)
            {
                _rdp.FullScreen = _vm.FullScreen;
            }
        }
        catch (Exception)
        {
            // The control rejects FullScreen unless connected; ignore transient COM state.
        }
    }

    private void OnRdpConnecting(object? sender, EventArgs e) =>
        SetStatus(RdpConnectionState.Connecting, "Connecting…");

    private void OnRdpConnected(object? sender, EventArgs e) =>
        SetStatus(RdpConnectionState.Connecting, "Authenticating…");

    private void OnRdpLoginComplete(object? sender, EventArgs e)
    {
        _connected = true;
        SetStatus(RdpConnectionState.Connected, "Connected.");

        // SPIKE-0B - REMOVE: apply client-side zoom post-login (documented as settable after the
        // connection starts) and kick one settle so a resize that happened mid-connect (skipped by
        // the Connected==1 guard) is applied now. Marshaled per the file's rule (events can arrive
        // off the UI thread); the deferred body re-checks teardown state before touching COM.
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing || _rdp is null)
            {
                return;
            }

            if (!_spikeEnableZoomMode)
            {
                ApplySpikeZoom(_spikeZoom); // ZoomLevel only in bare-zoom mode (exclusive with SmartSizing)
            }

            (int width, int height) = RemotePixelSize();
            UpdateSpikeDiag(width, height);
            _resizeTimer.Stop();
            _resizeTimer.Start();
        });
    }

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

    private void OnRdpAutoReconnected(object? sender, EventArgs e) =>
        SetStatus(RdpConnectionState.Connected, "Connected.");

    private string DescribeDisconnect(int reason)
    {
        if (_rdp is null)
        {
            return $"Disconnected (reason {reason}).";
        }

        try
        {
            string? description = _rdp.GetErrorDescription((uint)reason, (uint)_rdp.ExtendedDisconnectReason);
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
        if (_rdp is null)
        {
            return false;
        }

        try
        {
            // ExtendedDisconnectReasonCode → int: 2 = API-initiated logoff, 4 = server-initiated logoff,
            // 6 = logoff-by-user. Everything else (network / idle / disconnect / error) is an involuntary drop.
            int reason = (int)_rdp.ExtendedDisconnectReason;
            return reason is 2 or 4 or 6;
        }
        catch (Exception)
        {
            return false; // can't read the reason → treat as involuntary (keep the session open)
        }
    }

    private void OnEnterFullScreen(object? sender, EventArgs e)
    {
        SetFullScreenFlag(true);
        // Reflow the remote to the MONITOR resolution so full-screen is sharp + fully filled (not the smaller
        // windowed size scaled up).
        (int width, int height) = MonitorPixelSize();
        ResizeRemote(width, height);
    }

    private void OnLeaveFullScreen(object? sender, EventArgs e)
    {
        SetFullScreenFlag(false);
        (int width, int height) = RemotePixelSize(); // back to the embedded pane size
        ResizeRemote(width, height);
    }

    /// <summary>The pixel size of the monitor the session is on — the resolution to request when full-screen.
    /// SPIKE-0B: width floored to even (MS-RDPEDISP hard rule — see RemotePixelSize); monitor widths
    /// are virtually always even already, but one odd width anywhere reintroduces the silent-drop bug.</summary>
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

        return RemotePixelSize();
    }

    private void SetFullScreenFlag(bool value)
    {
        if (_vm is not null)
        {
            Dispatcher.BeginInvoke(() => _vm.FullScreen = value);
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

    // SPIKE-0B - REMOVE: sets ZoomLevel via the extended settings, then READS IT BACK — the strip
    // reports request->read-back, never the submission alone (the refit lesson). Never throws.
    private void ApplySpikeZoom(uint percent)
    {
        if (_rdp is null)
        {
            return;
        }

        try
        {
            if (_rdp.GetOcx() is IMsRdpExtendedSettings ext)
            {
                object zoom = percent;
                ext.set_Property("ZoomLevel", ref zoom);
                object readBack = ext.get_Property("ZoomLevel");
                _spikeZoomStatus = $"zoom={percent}->{readBack ?? "null"}";
                _spikeZoom = percent;
            }
            else
            {
                _spikeZoomStatus = "zoom=FAILED no-IMsRdpExtendedSettings";
            }
        }
        catch (Exception ex)
        {
            _spikeZoomStatus = $"zoom={percent}->FAILED {ex.GetType().Name}";
        }
    }

    // SPIKE-0B - REMOVE: refreshes the diagnostic strip. session= is the OCX's own
    // DesktopWidth/Height report read live — never the value we submitted.
    private void UpdateSpikeDiag(int framebufferWidth, int framebufferHeight)
    {
        Dispatcher.BeginInvoke(() =>
        {
            string session = _rdp is null ? "?" : $"{_rdp.DesktopWidth}x{_rdp.DesktopHeight}";
            string mode = _spikeEnableZoomMode ? "MODE=EnableZoom+SmartSizing  " : string.Empty;
            // fbConnect = what connect actually sent; fbNow = the latest recomputed request. The
            // two were previously conflated as one "fb=", which misread the status-bar collapse as
            // a 32px server-side deficit. Every number says what it IS, not what we assume.
            SpikeDiag.Text =
                $"{mode}paneDIPs={(int)HostContainer.ActualWidth}x{(int)HostContainer.ActualHeight}  " +
                $"fbConnect={_spikeFbConnect}  fbNow={framebufferWidth}x{framebufferHeight}  session={session}  " +
                $"{_spikeManualRefit}{_spikeRefit}  {_spikeZoomStatus}";
        });
    }

    // SPIKE-0B - REMOVE: live zoom-value probe (Q4) — ZoomLevel is documented changeable after connect.
    private void OnSpikeApplyZoom(object sender, RoutedEventArgs e)
    {
        if (_spikeEnableZoomMode)
        {
            _spikeZoomStatus = "zoom=n/a (EnableZoom mode — ZoomLevel is exclusive with SmartSizing)";
        }
        else if (SpikeZoomCombo.SelectedItem is ComboBoxItem { Content: string s } && uint.TryParse(s, out uint percent))
        {
            ApplySpikeZoom(percent);
        }

        (int width, int height) = RemotePixelSize();
        UpdateSpikeDiag(width, height);
    }

    // SPIKE-0B - REMOVE: mode toggle — applies to the NEXT connect (EnableZoom is write-only and
    // fixed at connect time). Switch modes by toggling, then closing and reopening the session.
    private void OnSpikeModeChanged(object sender, RoutedEventArgs e) =>
        _spikeEnableZoomMode = SpikeEnableZoomCheck.IsChecked == true;

    // SPIKE-0B - REMOVE: manual re-fit trigger — the timing discriminator. The post-login re-fit
    // fires ~450ms after login, possibly before the DisplayControl channel is open (silently
    // discarded, nothing retries). If the session is stale at connect size but snaps to fb when
    // this is pressed LATE, the drop was timing — and verify-and-retry is the real build's cure.
    private void OnSpikeRefit(object sender, RoutedEventArgs e)
    {
        _spikeManualRefit = $"manual-refit@{DateTime.Now:HH:mm:ss}  "; // proves the click fired
        OnResizeSettled(this, EventArgs.Empty);
    }

    /// <summary>Disconnects and tears the control down (called via Unloaded — see the ctor).</summary>
    public void DisposeSession()
    {
        _closing = true;
        _resizeTimer.Stop();
        _resizeTimer.Tick -= OnResizeSettled;

        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.ReconnectRequested -= OnReconnectRequested;
        }

        TearDownControl();
    }
}
