using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Vivre.Core.Rdp;

/// <summary>
/// Stores saved RDP logins as <c>%APPDATA%\Vivre\rdpcreds.json</c> — each password DPAPI-encrypted per Windows
/// user (see <see cref="DpapiSecret"/>). Also resolves credential INHERITANCE: a host with no credential of its
/// own uses the nearest ancestor folder's. Mirrors <c>AppSettingsStore</c>'s cached-snapshot persistence.
/// </summary>
public sealed class RdpCredentialStore
{
    private static List<RdpCredential>? _cache;
    private readonly string _path;

    public RdpCredentialStore()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vivre");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "rdpcreds.json");
    }

    private List<RdpCredential> Items => _cache ??= ReadFromDisk();

    private List<RdpCredential> ReadFromDisk() =>
        File.Exists(_path)
            ? JsonSerializer.Deserialize<List<RdpCredential>>(File.ReadAllText(_path)) ?? []
            : [];

    private void Persist()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(Items, new JsonSerializerOptions { WriteIndented = true }));
    }

    public RdpCredential? Get(string? id) =>
        string.IsNullOrEmpty(id) ? null : Items.FirstOrDefault(c => c.Id == id);

    /// <summary>Decrypts a stored credential's password (same Windows user only) — used when editing a host
    /// so an unchanged password box keeps the existing secret. Null if the credential is missing.</summary>
    [SupportedOSPlatform("windows")]
    public string? GetPassword(string? id)
    {
        RdpCredential? cred = Get(id);
        return cred is null ? null : DpapiSecret.Unprotect(cred.ProtectedPassword);
    }

    /// <summary>Creates (id null) or updates a credential, encrypting the password, and returns its id.</summary>
    [SupportedOSPlatform("windows")]
    public string Upsert(string? id, string? domain, string userName, string password)
    {
        RdpCredential? cred = string.IsNullOrEmpty(id) ? null : Items.FirstOrDefault(c => c.Id == id);
        if (cred is null)
        {
            cred = new RdpCredential();
            Items.Add(cred);
        }

        cred.Domain = string.IsNullOrWhiteSpace(domain) ? null : domain.Trim();
        cred.UserName = userName;
        cred.ProtectedPassword = DpapiSecret.Protect(password);
        Persist();
        return cred.Id;
    }

    public void Delete(string id)
    {
        if (Items.RemoveAll(c => c.Id == id) > 0)
        {
            Persist();
        }
    }

    /// <summary>
    /// Resolves the full connection settings for <paramref name="host"/>, inheriting a credential from the
    /// nearest ancestor folder when the host has none. Returns null when no credential is found anywhere on the
    /// path (the caller prompts or shows "no saved credentials"). Throws <see cref="System.Security.Cryptography.CryptographicException"/>
    /// if a found credential can't be decrypted (e.g. it was created under a different Windows user).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public RdpConnectionSettings? Resolve(RdpHost host, IReadOnlyList<RdpFolder> ancestorsNearestFirst)
    {
        RdpCredential? cred = Get(ResolveCredentialId(host, ancestorsNearestFirst));
        if (cred is null)
        {
            return null;
        }

        string password = DpapiSecret.Unprotect(cred.ProtectedPassword);
        return new RdpConnectionSettings(host.Server, host.Port, cred.Domain, cred.UserName, password, host.NlaEnabled);
    }

    /// <summary>Which credential id wins for a host: its own, else the nearest ancestor folder's, else null.
    /// Pure (no I/O) so the inheritance rule is unit-testable.</summary>
    public static string? ResolveCredentialId(RdpHost host, IReadOnlyList<RdpFolder> ancestorsNearestFirst)
    {
        if (!string.IsNullOrEmpty(host.CredentialId))
        {
            return host.CredentialId;
        }

        foreach (RdpFolder folder in ancestorsNearestFirst)
        {
            if (!string.IsNullOrEmpty(folder.CredentialId))
            {
                return folder.CredentialId;
            }
        }

        return null;
    }
}
