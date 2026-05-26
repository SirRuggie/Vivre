using System.Management.Automation;
using System.Security;

namespace Vivre.Core.Credentials;

/// <summary>
/// A credential used for remote operations. Session-only — held in memory, never
/// written to disk (per the current decision; DPAPI-backed persistence / a per-machine
/// Password Manager can come later, REBUILD_PLAN.md §3/§13).
/// </summary>
public sealed class ConnectionCredential
{
    public ConnectionCredential(string? domain, string userName, SecureString password)
    {
        Domain = string.IsNullOrWhiteSpace(domain) ? null : domain;
        UserName = userName;
        Password = password;
    }

    public string? Domain { get; }

    public string UserName { get; }

    public SecureString Password { get; }

    /// <summary><c>DOMAIN\User</c> (or just <c>User</c> when no domain) for display.</summary>
    public string DisplayName => Domain is null ? UserName : $@"{Domain}\{UserName}";

    /// <summary>Builds a <see cref="PSCredential"/> for the PowerShell remoting host.</summary>
    public PSCredential ToPowerShellCredential() => new(DisplayName, Password);
}
