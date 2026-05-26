using System.Security;
using Vivre.Core.Credentials;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Vivre.Desktop.ViewModels;

/// <summary>
/// Backs the Settings window's credential section. Session-only: editing here just
/// updates the shared <see cref="CredentialStore"/> in memory — nothing is persisted.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly CredentialStore _store;

    public SettingsViewModel(CredentialStore store)
    {
        _store = store;
        if (store.Current is { } current)
        {
            UseExplicitCredentials = true;
            Domain = current.Domain ?? string.Empty;
            UserName = current.UserName;
        }
    }

    /// <summary>When false, remote ops use the current Windows login.</summary>
    [ObservableProperty]
    public partial bool UseExplicitCredentials { get; set; }

    [ObservableProperty]
    public partial string Domain { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UserName { get; set; } = string.Empty;

    /// <summary>
    /// Writes the chosen credential into the store. <paramref name="password"/> comes from
    /// the window's PasswordBox; if it's empty and a credential already exists, the existing
    /// password is kept (so re-opening Settings and saving doesn't wipe it).
    /// </summary>
    public void Apply(SecureString password)
    {
        if (!UseExplicitCredentials || string.IsNullOrWhiteSpace(UserName))
        {
            _store.Current = null;
            return;
        }

        SecureString effective = password.Length > 0
            ? password
            : _store.Current?.Password ?? password;

        _store.Current = new ConnectionCredential(Domain, UserName, effective);
    }
}
