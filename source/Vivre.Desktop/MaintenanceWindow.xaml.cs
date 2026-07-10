using System.Security;
using System.Text;
using System.Windows;
using Vivre.Core.Models;
using Vivre.Desktop.ViewModels;
using Wpf.Ui.Controls;

namespace Vivre.Desktop;

/// <summary>
/// Collects WhatsUp Gold server + credentials and an Enter/Exit choice, then kicks off the
/// maintenance set in the background and closes — progress shows in the main window (each row's
/// Command result column + the activity log). The WUG credential is read from the
/// <see cref="PasswordBox"/> as a <see cref="SecureString"/> and handed straight through — never
/// stored; only the server address is remembered.
///
/// <para>A "Test connection" button runs a pre-flight check (module present + server reachable
/// + credentials valid) so fixable problems are caught inside the dialog before it fires, and a
/// "Check state" button reads (read-only) each in-scope machine's current WUG maintenance state.
/// OnRun also runs the same pre-flight gate and keeps the dialog open on failure.</para>
/// </summary>
public partial class MaintenanceWindow : FluentWindow
{
    private readonly WorkspaceViewModel _vm;
    private readonly IReadOnlyList<Computer> _computers;
    private readonly AppSettingsStore _settings = new();

    public MaintenanceWindow(WorkspaceViewModel vm, IReadOnlyList<Computer> computers)
    {
        InitializeComponent();
        _vm = vm;
        _computers = computers;

        Intro.Text = $"Set WhatsUp Gold maintenance for the {computers.Count} machine(s) in scope. Names are "
            + "matched to WUG devices by IP. When you click Set, this window closes and progress shows on each "
            + "row (Command result) and in the activity log.";
        ReasonBox.Text = $"SB {DateTime.Now:yyyyMMdd} | OS Updates";

        try
        {
            ServerBox.Text = _settings.Load().WugServer;
        }
        catch
        {
            ServerBox.Text = "10.70.25.111"; // a read failure just falls back to the default
        }

        // The initial Checked event during InitializeComponent hits the null-guard, so sync the
        // mode-dependent UI now rather than trusting the XAML default to match the default mode.
        OnModeChanged(this, new RoutedEventArgs());
    }

    // ── Testing state helpers ─────────────────────────────────────────────────────────────────────

    private void SetTestingState(bool active)
    {
        RunButton.IsEnabled = !active;
        TestButton.IsEnabled = !active;
        CheckStateButton.IsEnabled = !active;
        InstallButton.IsEnabled = !active;
        Spinner.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Test connection ───────────────────────────────────────────────────────────────────────────

    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        try
        {
            string server = ServerBox.Text.Trim();
            string user   = UserBox.Text.Trim();
            // Capture once — stays valid across awaits while the window is open.
            SecureString pw = PasswordBox.SecurePassword;

            if (server.Length == 0 || user.Length == 0 || pw.Length == 0)
            {
                ShowStatus("Enter the WhatsUp Gold server, username, and password.");
                return;
            }

            SetTestingState(true);
            ShowStatus("Testing connection…");

            var result = await _vm.TestWugConnectionAsync(server, user, pw);

            if (!result.ModulePresent)
            {
                ShowStatus("WhatsUpGoldPS isn't installed. Install it from the PowerShell Gallery? Needs internet/PSGallery.");
                InstallButton.Visibility = Visibility.Visible;
            }
            else if (result.Connected)
            {
                ShowStatus($"Connected to WhatsUp Gold at {server} as {user}. ✓");
                InstallButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                ShowStatus(result.Error ?? "Connection failed (no detail available).");
                InstallButton.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Test failed unexpectedly: {ex.Message}");
        }
        finally
        {
            SetTestingState(false);
        }
    }

    // ── Check current maintenance state (read-only) ────────────────────────────────────────────────

    private async void OnCheckState(object sender, RoutedEventArgs e)
    {
        try
        {
            string server = ServerBox.Text.Trim();
            string user   = UserBox.Text.Trim();
            // Capture once — stays valid across awaits while the window is open.
            SecureString pw = PasswordBox.SecurePassword;

            if (server.Length == 0 || user.Length == 0 || pw.Length == 0)
            {
                ShowStatus("Enter the WhatsUp Gold server, username, and password.");
                return;
            }

            SetTestingState(true);
            ShowStatus($"Checking current maintenance state for {_computers.Count} machine(s)…");

            var result = await _vm.GetWugMaintenanceStateAsync([.. _computers.Select(c => c.Name)], server, user, pw);

            if (result.Error is not null)
            {
                // Reserve the canned "install the module" prompt for the definite missing-module signal
                // (the same string OnTestConnection keys on); any other error surfaces verbatim.
                if (result.Error.Contains("isn't installed", StringComparison.OrdinalIgnoreCase))
                {
                    ShowStatus("WhatsUpGoldPS isn't installed. Install it from the PowerShell Gallery? Needs internet/PSGallery.");
                    InstallButton.Visibility = Visibility.Visible;
                }
                else
                {
                    ShowStatus(result.Error);
                    InstallButton.Visibility = Visibility.Collapsed;
                }

                return;
            }

            // Bucket every in-scope machine into exactly one state. Unknown = the state couldn't be read
            // (null, or the machine is absent from the map) — never assumed "not in maintenance".
            var inMaintenance    = new List<string>();
            var notInMaintenance = new List<string>();
            var notFound         = new List<string>();
            var unknown          = new List<string>();
            var unmatched = new HashSet<string>(result.Unmatched, StringComparer.OrdinalIgnoreCase);

            foreach (Computer c in _computers)
            {
                if (unmatched.Contains(c.Name))
                {
                    notFound.Add(c.Name);
                }
                else if (result.ByName.TryGetValue(c.Name, out bool? state) && state.HasValue)
                {
                    (state.Value ? inMaintenance : notInMaintenance).Add(c.Name);
                }
                else
                {
                    unknown.Add(c.Name);
                }
            }

            string summary = $"Current WUG state: {inMaintenance.Count} in maintenance · {notInMaintenance.Count} not in maintenance · "
                + $"{notFound.Count} not found in WUG · {unknown.Count} unknown";

            // Detail lines only for the actionable buckets; confirmed not-in-maintenance shows in the count only.
            var details = new List<string>();
            details.AddRange(inMaintenance.Select(n => $"  {n} — in maintenance"));
            details.AddRange(notFound.Select(n => $"  {n} — not found in WUG"));
            details.AddRange(unknown.Select(n => $"  {n} — unknown"));

            const int cap = 15;
            var sb = new StringBuilder(summary);
            foreach (string line in details.Take(cap))
            {
                sb.Append('\n').Append(line);
            }

            if (details.Count > cap)
            {
                sb.Append('\n').Append($"  +{details.Count - cap} more");
            }

            InstallButton.Visibility = Visibility.Collapsed;
            ShowStatus(sb.ToString());
        }
        catch (Exception ex)
        {
            ShowStatus($"Checking state failed unexpectedly: {ex.Message}");
        }
        finally
        {
            SetTestingState(false);
        }
    }

    // ── Install module ────────────────────────────────────────────────────────────────────────────

    private async void OnInstallModule(object sender, RoutedEventArgs e)
    {
        try
        {
            SetTestingState(true);
            ShowStatus("Installing WhatsUpGoldPS from the PowerShell Gallery…");

            var (ok, err) = await _vm.InstallWugModuleAsync();

            if (ok)
            {
                InstallButton.Visibility = Visibility.Collapsed;
                ShowStatus("Module installed. Click Test connection to verify.");
            }
            else
            {
                ShowStatus(err ?? "Installation failed (no detail available).");
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Install failed unexpectedly: {ex.Message}");
        }
        finally
        {
            SetTestingState(false);
        }
    }

    // ── Set maintenance (pre-flight gated) ───────────────────────────────────────────────────────

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        string server = ServerBox.Text.Trim();
        string user   = UserBox.Text.Trim();
        // Capture once — stays valid across awaits while the window is open; also safe after Close().
        SecureString pw = PasswordBox.SecurePassword;

        if (server.Length == 0 || user.Length == 0 || pw.Length == 0)
        {
            ShowStatus("Enter the WhatsUp Gold server, username, and password.");
            return;
        }

        bool enable = EnterRadio.IsChecked == true;

        try
        {
            SetTestingState(true);
            ShowStatus("Testing connection…");

            var pre = await _vm.TestWugConnectionAsync(server, user, pw);

            if (!pre.ModulePresent)
            {
                ShowStatus("WhatsUpGoldPS isn't installed. Install it from the PowerShell Gallery? Needs internet/PSGallery.");
                InstallButton.Visibility = Visibility.Visible;
                return; // keep dialog open
            }

            if (!pre.Connected)
            {
                ShowStatus(pre.Error ?? "Connection failed (no detail available).");
                return; // keep dialog open
            }

            // Pre-flight passed — remember the server address (never the credentials).
            try
            {
                AppSettings s = _settings.Load();
                s.WugServer = server;
                _settings.Save(s);
            }
            catch
            {
                // Best-effort — remembering the server address is a convenience, not required to proceed.
            }

            // Fire-and-forget: the VM runs it in the background and reports per-row + to the activity
            // log, so we close immediately and the user can keep working / start another. The password
            // was captured synchronously above, so closing the window can't invalidate it.
            _ = _vm.SetWugMaintenanceAsync(_computers, enable, server, user, pw, ReasonBox.Text.Trim());
            Close();
        }
        catch (Exception ex)
        {
            ShowStatus($"Unexpected error: {ex.Message}");
        }
        finally
        {
            // Only re-enable if we didn't close — IsLoaded is false once Close() has been called.
            if (IsLoaded)
            {
                SetTestingState(false);
            }
        }
    }

    // ── Misc ──────────────────────────────────────────────────────────────────────────────────────

    // Keep the action button's label and the Reason field in step with the chosen mode
    // (Enter -> "Set maintenance" + Reason shown, Exit -> "Exit maintenance" + Reason hidden).
    // EnterRadio's initial IsChecked="True" can raise Checked during InitializeComponent — before
    // RunButton/ReasonSection exist — so guard against the not-yet-realized controls.
    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (RunButton is null || EnterRadio is null || ReasonSection is null)
        {
            return;
        }

        bool enter = EnterRadio.IsChecked == true;
        RunButton.Content = enter ? "Set maintenance" : "Exit maintenance";
        ReasonSection.Visibility = enter ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
