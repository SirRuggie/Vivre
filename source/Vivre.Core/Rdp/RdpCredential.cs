using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Vivre.Core.Rdp;

/// <summary>
/// A saved RDP login (domain + user + password) referenced by a host/folder's <c>CredentialId</c>. The
/// password is stored ONLY as a DPAPI-encrypted blob (see <see cref="DpapiSecret"/>) — never plaintext on disk.
/// </summary>
public sealed class RdpCredential
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Logon domain, or null/empty for a local account or one on another domain.</summary>
    public string? Domain { get; set; }

    public string UserName { get; set; } = string.Empty;

    /// <summary>Base64 of the DPAPI-protected password bytes (CurrentUser scope).</summary>
    public string ProtectedPassword { get; set; } = string.Empty;
}

/// <summary>
/// Windows DPAPI (Data Protection API) helper for the saved RDP passwords. Encryption is bound to the
/// current Windows user on the current machine (<see cref="DataProtectionScope.CurrentUser"/>): a blob written
/// by one user can't be read by another, and won't roam to another machine. Decrypt failures throw
/// <see cref="CryptographicException"/> so the caller can surface a clear message (never swallow it).
/// </summary>
[SupportedOSPlatform("windows")]
public static class DpapiSecret
{
    // App-specific secondary entropy mixed into the protection — a small extra binding to Vivre. Changing
    // this string would make existing blobs undecryptable, so it's fixed.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Vivre.Rdp.credentials.v1");

    /// <summary>Encrypts a password and returns it as a base64 DPAPI blob.</summary>
    public static string Protect(string plaintext)
    {
        byte[] data = Encoding.UTF8.GetBytes(plaintext);
        byte[] blob = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(blob);
    }

    /// <summary>Decrypts a base64 DPAPI blob produced by <see cref="Protect"/>. Throws
    /// <see cref="CryptographicException"/> if the blob wasn't created by this Windows user/machine.</summary>
    public static string Unprotect(string protectedBase64)
    {
        byte[] blob = Convert.FromBase64String(protectedBase64);
        byte[] data = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(data);
    }
}
