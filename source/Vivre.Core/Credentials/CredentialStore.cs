namespace Vivre.Core.Credentials;

/// <summary>
/// Holds the credential used for remote operations this session. One instance is
/// shared across the app (created at the composition root for now; becomes a DI
/// singleton later). <see cref="Current"/> is <see langword="null"/> when operations
/// should use the current Windows login (the default / jump-box case).
/// </summary>
public sealed class CredentialStore
{
    /// <summary>The active credential, or null to use the current Windows identity.</summary>
    public ConnectionCredential? Current { get; set; }

    /// <summary>True when an explicit credential is set.</summary>
    public bool HasExplicitCredential => Current is not null;
}
