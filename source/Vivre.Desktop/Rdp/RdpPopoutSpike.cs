using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AxMSTSCLib;
using MSTSCLib;
using Vivre.Core.Rdp;
using FormsTimer = System.Windows.Forms.Timer;

namespace Vivre.Desktop.Rdp;

// ============================================================================================
// SPIKE - REMOVE (Path 2 step 0). THROWAWAY — never merges to master.
//
// Proves (or kills) the Path 2 premise empirically: a top-level WinForms window created while
// the calling thread's DPI awareness context is UNAWARE gets bitmap-stretched by Windows on a
// >100% display (mixed-mode DPI) — the mechanism the shipped mRemoteNG (v1.76.20, DPI-unaware)
// actually uses for its readable image. The session's OWN scale stays pinned at (100,100) in
// every variant — the FCM context-menu bug trips at any session scale above 100%, so the
// magnification must come from the window side only. See docs/vivre-rdp-scaling-and-fcm-findings.md.
// ============================================================================================

/// <summary>Which DPI awareness context the spike window is created under.</summary>
internal enum SpikeDpiContext
{
    /// <summary>No switch — the window inherits the process's System-DPI-Aware context (the control variant).</summary>
    InheritSystemAware,
    Unaware,
    UnawareGdiScaled,
}

/// <summary>The pre-launch toggles. Context is fixed at HWND creation, so all are chosen before launch.</summary>
internal sealed record RdpPopoutSpikeOptions(SpikeDpiContext Context, bool LogicalFramebuffer, bool SmartSizing);

/// <summary>
/// SPIKE - REMOVE (Path 2 step 0): a bare top-level WinForms window hosting the RDP OCX, connect
/// code lifted from <c>RdpSessionView.Connect()</c>. One window per launch; closing it ends the
/// session. No reconnect, no full-screen plumbing — this exists only to answer "does the image
/// read bigger, and does the in-session DPI probe still say 96?".
/// </summary>
public sealed class RdpPopoutSpike : Form
{
    // Window DPI awareness is fixed by the CALLING THREAD's context at HWND creation (mixed-mode
    // DPI); child windows inherit the parent's. Switch → create+show → restore, on the WPF UI thread.
    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    private static readonly IntPtr DpiContextUnaware = new(-1);          // DPI_AWARENESS_CONTEXT_UNAWARE
    private static readonly IntPtr DpiContextUnawareGdiScaled = new(-5); // DPI_AWARENESS_CONTEXT_UNAWARE_GDISCALED

    private readonly string _hostName;
    private readonly RdpConnectionSettings _settings;
    private readonly RdpPopoutSpikeOptions _options;
    private readonly Label _readout;
    private readonly Panel _hostPanel;
    private readonly FormsTimer _resizeTimer;
    private AxMsRdpClient9NotSafeForScripting? _rdp;
    private bool _connectStarted;
    private bool _closing; // tearing the control down ourselves — ignore its own disconnect event

    /// <summary>Shows the variant chooser, then creates the spike window under the chosen thread
    /// DPI context and restores the context immediately after. Called from the temporary tree
    /// context-menu item with an already-resolved credential bundle — no resolution happens here.</summary>
    public static void Launch(string hostName, RdpConnectionSettings settings)
    {
        RdpPopoutSpikeOptions options;
        using (var chooser = new RdpPopoutSpikeChooser())
        {
            if (chooser.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            options = chooser.Options;
        }

        IntPtr previous = IntPtr.Zero;
        bool switched = options.Context != SpikeDpiContext.InheritSystemAware;
        if (switched)
        {
            previous = SetThreadDpiAwarenessContext(
                options.Context == SpikeDpiContext.Unaware ? DpiContextUnaware : DpiContextUnawareGdiScaled);
        }

        try
        {
            var form = new RdpPopoutSpike(hostName, settings, options);
            form.Show(); // creates the HWND under the chosen context; WndProc auto-switches thereafter
        }
        finally
        {
            if (switched && previous != IntPtr.Zero)
            {
                SetThreadDpiAwarenessContext(previous); // restore the WPF thread's System-Aware context
            }
        }
    }

    private RdpPopoutSpike(string hostName, RdpConnectionSettings settings, RdpPopoutSpikeOptions options)
    {
        _hostName = hostName;
        _settings = settings;
        _options = options;

        Text = $"{hostName} — pop-out spike (connecting…)";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1280, 800);
        MinimumSize = new Size(700, 500);

        // Readout strip: the numbers the operator pastes back per variant.
        _readout = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 8, 0),
            BackColor = SystemColors.Info,
            ForeColor = SystemColors.InfoText,
            Text = VariantText,
        };

        // Same structure as the shipping control: OCX Dock=Fill inside an intermediate Panel.
        _hostPanel = new Panel { Dock = DockStyle.Fill };
        Controls.Add(_hostPanel);
        Controls.Add(_readout);

        // Debounced resize → re-fit the remote resolution once resizing settles (shipping pattern,
        // ported from DispatcherTimer to a WinForms timer so ticks run in THIS window's DPI context).
        _resizeTimer = new FormsTimer { Interval = 450 };
        _resizeTimer.Tick += OnResizeSettled;
        Resize += (_, _) => { _resizeTimer.Stop(); _resizeTimer.Start(); };

        Shown += OnShownConnect;
        FormClosing += OnSpikeFormClosing;
    }

    private void OnShownConnect(object? sender, EventArgs e)
    {
        if (_connectStarted)
        {
            return;
        }

        _connectStarted = true;
        try
        {
            // Fresh OCX, created after the form's HWND exists (AxHost needs a live handle before
            // its properties are touched). BeginInit/EndInit mirrors the shipping CreateControl.
            _rdp = new AxMsRdpClient9NotSafeForScripting();
            ((ISupportInitialize)_rdp).BeginInit();
            _rdp.Dock = DockStyle.Fill;
            _hostPanel.Controls.Add(_rdp);
            ((ISupportInitialize)_rdp).EndInit();

            _rdp.OnLoginComplete += OnRdpLoginComplete;
            _rdp.OnDisconnected += OnRdpDisconnected;
            _rdp.OnFatalError += OnRdpFatalError;

            RdpConnectionSettings s = _settings;
            _rdp.Server = s.Server;
            _rdp.AdvancedSettings9.RDPPort = s.Port;
            _rdp.UserName = s.UserName;
            _rdp.Domain = s.Domain ?? string.Empty;

            (int width, int height) = Framebuffer();
            _rdp.DesktopWidth = width;
            _rdp.DesktopHeight = height;

            _rdp.AdvancedSettings9.EnableCredSspSupport = s.NlaEnabled;
            _rdp.AdvancedSettings9.SmartSizing = _options.SmartSizing; // the one toggled connect setting
            _rdp.AdvancedSettings9.ContainerHandledFullScreen = 0;
            _rdp.AdvancedSettings9.EnableAutoReconnect = true;
            _rdp.AdvancedSettings9.GrabFocusOnConnect = true;

            // FCM pin — NON-NEGOTIABLE, identical in every variant: the session's display + device
            // scale stay 100. The readability under test comes from the WINDOW being OS-stretched,
            // never from the session scale (which would re-break FCM at >100%).
            ApplyScalePin(_rdp);

            if (_rdp.GetOcx() is IMsTscNonScriptable nonScriptable)
            {
                nonScriptable.ClearTextPassword = s.Password;
            }

            _rdp.Connect();
            UpdateReadout(width, height);
        }
        catch (Exception ex)
        {
            Text = $"{_hostName} — pop-out spike (failed)";
            _readout.Text = $"Couldn't start the session: {ex.Message}";
        }
    }

    /// <summary>The single place the session scale factors are written: always (100, 100).</summary>
    private static void ApplyScalePin(AxMsRdpClient9NotSafeForScripting rdp)
    {
        if (rdp.GetOcx() is IMsRdpExtendedSettings ext)
        {
            object desktop = 100u;
            ext.set_Property("DesktopScaleFactor", ref desktop);
            object device = 100u;
            ext.set_Property("DeviceScaleFactor", ref device);
        }
    }

    /// <summary>
    /// The framebuffer to request, in this window's own coordinate space. An UNAWARE window reports
    /// virtualized (96-DPI) units and <c>DeviceDpi</c> = 96, so both toggles coincide there — that IS
    /// the mechanism under test. In a System-Aware window <c>ClientSize</c> is physical device pixels,
    /// so "physical" = ClientSize as-is (today's shipping baseline, the control variant) and
    /// "logical" = ClientSize ÷ (DeviceDpi/96) (the old ÷1.5 experiment).
    /// </summary>
    private (int Width, int Height) Framebuffer()
    {
        Size client = _hostPanel.ClientSize;
        double scale = DeviceDpi / 96.0;
        int width = _options.LogicalFramebuffer ? (int)Math.Round(client.Width / scale) : client.Width;
        int height = _options.LogicalFramebuffer ? (int)Math.Round(client.Height / scale) : client.Height;
        return (Math.Max(640, width), Math.Max(480, height));
    }

    private void OnResizeSettled(object? sender, EventArgs e)
    {
        _resizeTimer.Stop();
        if (_rdp is null || _rdp.Connected == 0)
        {
            return;
        }

        (int width, int height) = Framebuffer();
        try
        {
            // Scale factors stay pinned at (100,100) on every re-fit — same rule as connect.
            _rdp.UpdateSessionDisplaySettings((uint)width, (uint)height, 0, 0, 0, 100u, 100u);
        }
        catch (Exception)
        {
            // Pre-8.1 / unsupported server — SmartSizing (if on) scales the image instead;
            // otherwise the session stays at its connect-time resolution.
        }

        UpdateReadout(width, height);
    }

    private string VariantText =>
        $"ctx={_options.Context}  fb={(_options.LogicalFramebuffer ? "logical" : "physical")}  smart={(_options.SmartSizing ? "on" : "off")}";

    private void UpdateReadout(int framebufferWidth, int framebufferHeight)
    {
        OnUi(() => _readout.Text =
            $"{VariantText}  |  ClientSize={ClientSize.Width}x{ClientSize.Height}  panel={_hostPanel.ClientSize.Width}x{_hostPanel.ClientSize.Height}  " +
            $"DeviceDpi={DeviceDpi}  |  framebuffer {framebufferWidth}x{framebufferHeight}");
    }

    private void OnRdpLoginComplete(object? sender, EventArgs e) =>
        OnUi(() => Text = $"{_hostName} — pop-out spike (connected)");

    private void OnRdpDisconnected(object? sender, IMsTscAxEvents_OnDisconnectedEvent e)
    {
        if (_closing)
        {
            return; // we're tearing the control down ourselves
        }

        OnUi(() =>
        {
            Text = $"{_hostName} — pop-out spike (disconnected, reason {e.discReason})";
            _readout.Text = $"Disconnected (reason {e.discReason}). Close this window and relaunch to retry.";
        });
    }

    private void OnRdpFatalError(object? sender, IMsTscAxEvents_OnFatalErrorEvent e) =>
        OnUi(() =>
        {
            Text = $"{_hostName} — pop-out spike (fatal error {e.errorCode})";
            _readout.Text = $"RDP fatal error (code {e.errorCode}).";
        });

    // OCX events can arrive off the owning thread — marshal, and BeginInvoke (not Invoke) so an
    // event raised while the thread is busy in a COM call can't deadlock (shipping-code rule).
    private void OnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(action);
        }
        else
        {
            action();
        }
    }

    private void OnSpikeFormClosing(object? sender, FormClosingEventArgs e)
    {
        _closing = true;
        _resizeTimer.Stop();
        _resizeTimer.Tick -= OnResizeSettled;
        TearDownOcx();
    }

    /// <summary>Unsubscribes the OCX's events BEFORE disconnecting (so its Disconnect() can't
    /// re-enter our handlers), disconnects if live, then disposes — the shipping TearDownControl
    /// discipline.</summary>
    private void TearDownOcx()
    {
        if (_rdp is null)
        {
            return;
        }

        _rdp.OnLoginComplete -= OnRdpLoginComplete;
        _rdp.OnDisconnected -= OnRdpDisconnected;
        _rdp.OnFatalError -= OnRdpFatalError;

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

        _hostPanel.Controls.Remove(_rdp);
        _rdp.Dispose();
        _rdp = null;
    }
}

/// <summary>SPIKE - REMOVE (Path 2 step 0): the pre-launch variant chooser. Thread DPI context is
/// fixed at window creation, so every toggle must be chosen before the spike window exists.</summary>
internal sealed class RdpPopoutSpikeChooser : Form
{
    private readonly RadioButton _inherit;
    private readonly RadioButton _unaware;
    private readonly RadioButton _unawareGdi;
    private readonly CheckBox _logical;
    private readonly CheckBox _smartSizing;

    public RdpPopoutSpikeChooser()
    {
        Text = "Pop-out spike variant";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(360, 250);

        var contextLabel = new Label { Text = "Thread DPI context (at window creation):", Left = 12, Top = 12, Width = 330 };
        _inherit = new RadioButton { Text = "Inherit (System-Aware) — control variant", Left = 24, Top = 34, Width = 320 };
        _unaware = new RadioButton { Text = "UNAWARE — the mRemoteNG analog", Left = 24, Top = 57, Width = 320, Checked = true };
        _unawareGdi = new RadioButton { Text = "UNAWARE_GDISCALED", Left = 24, Top = 80, Width = 320 };

        _logical = new CheckBox { Text = "Logical framebuffer (ClientSize in the window's units)", Left = 12, Top = 112, Width = 340, Checked = true };
        _smartSizing = new CheckBox { Text = "SmartSizing", Left = 12, Top = 137, Width = 340, Checked = false };

        var note = new Label
        {
            Text = "Session scale is pinned to 100% in every variant (FCM-safe).",
            Left = 12, Top = 165, Width = 340, Height = 30,
            ForeColor = SystemColors.GrayText,
        };

        var ok = new Button { Text = "Launch", DialogResult = DialogResult.OK, Left = 184, Top = 205, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 272, Top = 205, Width = 80 };
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.AddRange([contextLabel, _inherit, _unaware, _unawareGdi, _logical, _smartSizing, note, ok, cancel]);
    }

    public RdpPopoutSpikeOptions Options => new(
        _unaware.Checked ? SpikeDpiContext.Unaware
        : _unawareGdi.Checked ? SpikeDpiContext.UnawareGdiScaled
        : SpikeDpiContext.InheritSystemAware,
        _logical.Checked,
        _smartSizing.Checked);
}
