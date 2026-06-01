using System.Security;
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
    }

    private void OnRun(object sender, RoutedEventArgs e)
    {
        string server = ServerBox.Text.Trim();
        string user = UserBox.Text.Trim();
        SecureString password = PasswordBox.SecurePassword;

        if (server.Length == 0 || user.Length == 0 || password.Length == 0)
        {
            ShowStatus("Enter the WhatsUp Gold server, username, and password.");
            return;
        }

        bool enable = EnterRadio.IsChecked == true;

        // Remember the server for next time (never the credentials).
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

        // Fire-and-forget: the VM runs it in the background and reports per-row + to the activity log,
        // so we close immediately and the user can keep working / start another. The password is read
        // (synchronously) before this returns, so closing the window can't invalidate it.
        _ = _vm.SetWugMaintenanceAsync(_computers, enable, server, user, password, ReasonBox.Text.Trim());
        Close();
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
