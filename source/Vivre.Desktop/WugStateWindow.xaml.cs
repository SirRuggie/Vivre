using System.Security;
using System.Windows;
using Vivre.Core.Models;
using Vivre.Desktop.ViewModels;
using Wpf.Ui.Controls;

namespace Vivre.Desktop;

/// <summary>
/// Collects the WhatsUp Gold server + credentials, then kicks off a read-only per-row state check in
/// the background and closes — progress shows in the main window (each row's Command result column +
/// the activity log). The WUG credential is read from the <see cref="PasswordBox"/> as a
/// <see cref="SecureString"/> and handed straight through — never stored; the server is pre-filled
/// read-only from Settings.
///
/// <para>A pre-flight check (module present + server reachable + credentials valid) runs before the
/// check fires, so fixable problems are caught inside the dialog; on a missing module an
/// "Install module" button appears. This window only ever reads state — it never sets maintenance.</para>
/// </summary>
public partial class WugStateWindow : FluentWindow
{
    private readonly WorkspaceViewModel _vm;
    private readonly IReadOnlyList<Computer> _computers;
    private readonly AppSettingsStore _settings = new();

    public WugStateWindow(WorkspaceViewModel vm, IReadOnlyList<Computer> computers)
    {
        InitializeComponent();
        _vm = vm;
        _computers = computers;

        Intro.Text = $"Check the current WhatsUp Gold maintenance state for the {computers.Count} machine(s) in scope. "
            + "Results land on each row (Command result) and in the activity log.";

        try
        {
            ServerBox.Text = _settings.Load().WugServer;
        }
        catch
        {
            ServerBox.Text = "10.70.25.111"; // a read failure just falls back to the default
        }

        // No save-back: a read-only check deliberately can't retarget the saved server
        // (Settings / the maintenance dialog are where it's edited).
    }

    // ── Testing state helpers ─────────────────────────────────────────────────────────────────────

    private void SetTestingState(bool active)
    {
        CheckButton.IsEnabled = !active;
        InstallButton.IsEnabled = !active;
        Spinner.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Check state (pre-flight gated) ───────────────────────────────────────────────────────────

    private async void OnCheck(object sender, RoutedEventArgs e)
    {
        try
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
                InstallButton.Visibility = Visibility.Collapsed;
                return; // keep dialog open
            }

            // Fire-and-forget: the VM runs the read-only check in the background and reports per-row + to
            // the activity log, so we close immediately. The password was captured synchronously above, so
            // closing the window can't invalidate it.
            _ = _vm.CheckWugStateAsync(_computers, server, user, pw);
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
                ShowStatus("Module installed. Click Check to verify.");
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

    // ── Misc ──────────────────────────────────────────────────────────────────────────────────────

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
