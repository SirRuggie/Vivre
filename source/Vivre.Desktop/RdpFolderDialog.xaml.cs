using System.Windows;
using Wpf.Ui.Controls;

namespace Vivre.Desktop;

/// <summary>Add/edit dialog for a Cross-Domain RDP folder (name + optional credentials inherited by its hosts). The
/// caller reads the public values after <c>ShowDialog() == true</c>.</summary>
public partial class RdpFolderDialog : FluentWindow
{
    public RdpFolderDialog(string title, string name = "", string? domain = null, string? userName = null)
    {
        InitializeComponent();
        Title = title;
        TitleBarControl.Title = title;

        NameBox.Text = name;
        DomainBox.Text = domain ?? string.Empty;
        UserBox.Text = userName ?? string.Empty;

        Loaded += (_, _) => NameBox.Focus();
    }

    public string FolderName => NameBox.Text.Trim();

    public string? Domain => string.IsNullOrWhiteSpace(DomainBox.Text) ? null : DomainBox.Text.Trim();

    public string? UserName => string.IsNullOrWhiteSpace(UserBox.Text) ? null : UserBox.Text.Trim();

    /// <summary>Null when left blank (keep existing / no password).</summary>
    public string? Password => PasswordBox.Password.Length == 0 ? null : PasswordBox.Password;

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            ErrorText.Text = "Enter a folder name.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
