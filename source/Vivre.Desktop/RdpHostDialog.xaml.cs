using System.Windows;
using Wpf.Ui.Controls;

namespace Vivre.Desktop;

/// <summary>Add/edit dialog for a Cross-Domain RDP host (name, server, port, NLA, and per-host credentials). The
/// caller reads the public values after <c>ShowDialog() == true</c>. A blank username means "inherit the
/// folder's credentials"; a blank password on edit means "keep the existing one".</summary>
public partial class RdpHostDialog : FluentWindow
{
    public RdpHostDialog(
        string title,
        string name = "",
        string server = "",
        int port = 3389,
        bool nlaEnabled = true,
        string? domain = null,
        string? userName = null)
    {
        InitializeComponent();
        Title = title;
        TitleBarControl.Title = title;

        NameBox.Text = name;
        ServerBox.Text = server;
        PortBox.Text = port.ToString();
        NlaSwitch.IsChecked = nlaEnabled;
        DomainBox.Text = domain ?? string.Empty;
        UserBox.Text = userName ?? string.Empty;

        Loaded += (_, _) => ServerBox.Focus();
    }

    public string HostName => NameBox.Text.Trim();

    public string Server => ServerBox.Text.Trim();

    public int Port => int.TryParse(PortBox.Text, out int p) ? p : 3389;

    public bool NlaEnabled => NlaSwitch.IsChecked == true;

    public string? Domain => string.IsNullOrWhiteSpace(DomainBox.Text) ? null : DomainBox.Text.Trim();

    public string? UserName => string.IsNullOrWhiteSpace(UserBox.Text) ? null : UserBox.Text.Trim();

    /// <summary>Null when left blank (keep existing / no password).</summary>
    public string? Password => PasswordBox.Password.Length == 0 ? null : PasswordBox.Password;

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ServerBox.Text))
        {
            ShowError("Enter a server address.");
            return;
        }

        if (!int.TryParse(PortBox.Text, out int p) || p is < 1 or > 65535)
        {
            ShowError("Port must be a number between 1 and 65535.");
            return;
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
