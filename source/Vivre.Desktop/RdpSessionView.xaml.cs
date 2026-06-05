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

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.ReconnectRequested += OnReconnectRequested;

        Connect();
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

            // NLA via CredSSP: on by default; OFF lets a server on another domain accept the supplied
            // credentials instead of rejecting delegated/saved ones (the cross-domain 0x2107 rejection).
            _rdp.AdvancedSettings9.EnableCredSspSupport = s.NlaEnabled;

            // SmartSizing scales the remote image to the control (and to the monitor in full-screen): smooth
            // live resize with NO reconnect. Reconnecting on resize triggers a protocol-error disconnect on
            // some servers, so we scale instead. The control owns full-screen (toolbar / Ctrl+Alt+Break).
            _rdp.AdvancedSettings9.SmartSizing = true;
            _rdp.AdvancedSettings9.ContainerHandledFullScreen = 0;

            // Match the remote's display scale to THIS PC's (e.g. 150%) so icons/text aren't tiny on a high-DPI
            // display — the remote's "Scale and layout". Must be set before Connect, via the extended settings.
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
            SetStatus(RdpConnectionState.Connecting, "Connecting…");
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

        // After a transient drop the RDP control often auto-reconnects on its own, so by the time the user
        // clicks Reconnect the session may already be back. Re-running Connect() — which re-sets Server / size /
        // scale on the control — throws "Unexpected HRESULT…" unless the control is fully disconnected, even
        // though the live session is fine. So only reconnect when it's actually down; otherwise just resync the
        // status bar to the control's real state (which clears the stale error/overlay).
        if (_rdp.Connected != 0)
        {
            bool live = _rdp.Connected == 1;
            SetStatus(live ? RdpConnectionState.Connected : RdpConnectionState.Connecting,
                live ? "Connected." : "Reconnecting…");
            return;
        }

        _connectStarted = false;
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
    }

    /// <summary>Live resolution change (RDP 8.1+): the remote desktop reflows to the new size with NO reconnect
    /// (unlike Reconnect(), which protocol-errors on some servers). If the server is too old this throws and
    /// SmartSizing keeps the image scaled to fit instead — so resizing always at least looks right.</summary>
    private void ResizeRemote(int width, int height)
    {
        if (_rdp is null || _rdp.Connected == 0)
        {
            return;
        }

        (uint desktopScale, uint deviceScale) = LocalScale();
        try
        {
            _rdp.UpdateSessionDisplaySettings((uint)width, (uint)height, 0, 0, 0, desktopScale, deviceScale);
        }
        catch (Exception)
        {
            // Pre-8.1 / unsupported server — SmartSizing scales the image instead.
        }
    }

    /// <summary>This PC's display scale for the remote: DesktopScaleFactor (100–500, e.g. 150 for 150%) so the
    /// remote's icons/text match local size, plus the paired DeviceScaleFactor (only 100/140/180 are valid).</summary>
    private (uint Desktop, uint Device) LocalScale()
    {
        int percent = (int)Math.Round(System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX * 100);
        uint desktop = (uint)Math.Clamp(percent, 100, 500);
        uint device = desktop >= 180 ? 180u : desktop >= 140 ? 140u : 100u;
        return (desktop, device);
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

    /// <summary>The pane size in physical pixels — the remote desktop resolution to request (the control
    /// renders in device pixels; WPF sizes are DIPs).</summary>
    private (int Width, int Height) RemotePixelSize()
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        return (
            Math.Max(640, (int)Math.Round(HostContainer.ActualWidth * dpi.DpiScaleX)),
            Math.Max(480, (int)Math.Round(HostContainer.ActualHeight * dpi.DpiScaleY)));
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
    }

    private void OnRdpDisconnected(object? sender, IMsTscAxEvents_OnDisconnectedEvent e)
    {
        if (_closing)
        {
            return; // we're tearing the control down ourselves
        }

        // A session that had reached the desktop and is now gone — logged off, signed out, idle-timed-out,
        // kicked, or ended by the server — can't be resumed in place, so close the tab (like mstsc closing its
        // window on logoff). Brief network blips don't reach here; the control's auto-reconnect handles those.
        // Reconnect is only useful for a connect that never succeeded, so for those keep the tab + show why.
        if (_connected)
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

    /// <summary>The pixel size of the monitor the session is on — the resolution to request when full-screen.</summary>
    private (int Width, int Height) MonitorPixelSize()
    {
        if (_rdp is not null)
        {
            System.Drawing.Rectangle bounds = System.Windows.Forms.Screen.FromControl(_rdp).Bounds;
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                return (bounds.Width, bounds.Height);
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

        if (_rdp is null)
        {
            return;
        }

        // Detach every control event BEFORE disconnecting, so the COM Disconnect() can't re-enter our handlers
        // mid-teardown (the _closing guard backs this up).
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
}
