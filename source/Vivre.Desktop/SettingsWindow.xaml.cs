using System.Windows;
using Vivre.Core.Credentials;
using Vivre.Desktop.ViewModels;
using Wpf.Ui.Controls;

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
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // SecurePassword is read from the PasswordBox here (it isn't bindable).
        _viewModel.Apply(PasswordBox.SecurePassword);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
