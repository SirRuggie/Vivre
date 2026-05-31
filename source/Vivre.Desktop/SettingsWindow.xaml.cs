using System.Windows;
using Vivre.Core.Credentials;
using Vivre.Desktop.ViewModels;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;

namespace Vivre.Desktop;

/// <summary>Settings window — currently just the session-only remote credential.</summary>
public partial class SettingsWindow : FluentWindow
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(CredentialStore credentials)
    {
        InitializeComponent();
        _viewModel = new SettingsViewModel(credentials);
        DataContext = _viewModel;

        // Focus the field the user is most likely to edit (Enter saves, Esc cancels).
        Loaded += (_, _) =>
        {
            if (_viewModel.UseExplicitCredentials)
            {
                UserNameBox.Focus();
            }
            else
            {
                UseLoginRadio.Focus();
            }
        };
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        // Don't silently wipe the stored credential when "Use these credentials" is chosen but the
        // username is blank — make the user fix it or switch to Windows login explicitly.
        if (_viewModel.UseExplicitCredentials && string.IsNullOrWhiteSpace(_viewModel.UserName))
        {
            var warn = new MessageBox
            {
                Title = "Username required",
                Content = "Enter a username for the explicit credential, or choose \"Use my Windows login\".",
                CloseButtonText = "OK",
            };
            await warn.ShowDialogAsync();
            UserNameBox.Focus();
            return;
        }

        // SecurePassword is read from the PasswordBox here (it isn't bindable).
        _viewModel.Apply(PasswordBox.SecurePassword);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
