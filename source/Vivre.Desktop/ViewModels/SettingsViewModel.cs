using System.Security;
using Vivre.Core.Credentials;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Vivre.Desktop.ViewModels;

/// <summary>
/// Backs the Settings window's credential draft. Apply updates the shared
/// <see cref="CredentialStore"/> in memory for this session — nothing is persisted.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly CredentialStore _store;
    private readonly string _windowsLoginName;

    public SettingsViewModel(CredentialStore store)
    {
        _store = store;
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        _windowsLoginName = identity.Name;
        if (store.Current is { } current)
        {
            UseExplicitCredentials = true;
            Domain = current.Domain ?? string.Empty;
            UserName = current.UserName;
        }
    }

    /// <summary>Draft choice; becomes active only on Apply.</summary>
    [ObservableProperty]
    public partial bool UseExplicitCredentials { get; set; }

    [ObservableProperty]
    public partial string Domain { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UserName { get; set; } = string.Empty;

    public string AppliedCredentialSummary => _store.Current is { } current
        ? $"Applied: {current.DisplayName}"
        : $"Applied: Windows login ({_windowsLoginName})";

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
            OnPropertyChanged(nameof(AppliedCredentialSummary));
            return;
        }

        SecureString effective = password.Length > 0
            ? password
            : _store.Current?.Password ?? password;

        _store.Current = new ConnectionCredential(Domain, UserName, effective);
        OnPropertyChanged(nameof(AppliedCredentialSummary));
    }
}
