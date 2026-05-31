using CommunityToolkit.Mvvm.ComponentModel;

namespace Vivre.Core.Models;

/// <summary>
/// A stored credential used to authenticate to remote SCCM clients. Mirrors the
/// legacy password-manager entry (Domain + Username + secret), minus the broken
/// "encrypt with the domain name as the key" cipher.
///
/// This model deliberately does NOT hold a plaintext password; if persisted secret
/// storage is ever added it should use Windows DPAPI (<c>ProtectedData.Protect</c>,
/// CurrentUser scope). (Today credentials are session-only — see <c>CredentialStore</c>.)
/// </summary>
public partial class Credential : ObservableObject
{
    /// <summary>Windows domain (or computer name) the account belongs to. Optional.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    public partial string? Domain { get; set; }

    /// <summary>Account user name (without the domain prefix).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    public partial string UserName { get; set; } = string.Empty;

    /// <summary>Free-text label so the user can tell entries apart.</summary>
    [ObservableProperty]
    public partial string? Description { get; set; }

    /// <summary><c>DOMAIN\User</c>, or just the user name when no domain is set.</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Domain) ? UserName : $@"{Domain}\{UserName}";
}
