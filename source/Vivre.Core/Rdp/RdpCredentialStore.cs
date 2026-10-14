using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using Vivre.Core.IO;

namespace Vivre.Core.Rdp;

/// <summary>
/// Stores saved RDP logins as <c>%APPDATA%\Vivre\rdpcreds.json</c> — each password DPAPI-encrypted per Windows
/// user (see <see cref="DpapiSecret"/>). Also resolves credential INHERITANCE: a host with no credential of its
/// own uses the nearest ancestor folder's. Mirrors <c>AppSettingsStore</c>'s cached-snapshot persistence.
/// </summary>
public sealed class RdpCredentialStore
{
    private List<RdpCredential>? _cache;
    private readonly string _path;
    private readonly Action<string, string, bool> _write;
    private bool _loadFailed;
    private bool _loadedFromBackup;

    public string? RecoveryMessage { get; private set; }

    public RdpCredentialStore()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vivre");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "rdpcreds.json");
        _write = RecoverableFileWriter.Write;
    }

    internal RdpCredentialStore(string path, Action<string, string, bool>? write = null)
    {
        _path = path;
        _write = write ?? RecoverableFileWriter.Write;
    }

    private List<RdpCredential> Items => _cache ??= ReadFromDisk();

    private List<RdpCredential> ReadFromDisk()
    {
        if (!PathExists(_path))
        {
            if (!PathExists(_path + ".bak"))
            {
                return [];
            }

            try
            {
                List<RdpCredential> backup = ReadRequired(_path + ".bak");
                _loadedFromBackup = true;
                RecoveryMessage = "The saved RDP credential store was missing. Vivre loaded its encrypted last-known-good credential backup; the next successful credential save will restore the primary file. Host-tree and credential backups may be from different saves; re-enter any login reported as unavailable.";
                return backup;
            }
            catch (Exception backupError)
            {
                _loadFailed = true;
                throw new InvalidDataException("The RDP credential primary file is missing and its last-known-good encrypted credential backup is unreadable. Saving is blocked to protect the backup.", backupError);
            }
        }

        try
        {
            return ReadRequired(_path);
        }
        catch (Exception primaryError)
        {
            if (PathExists(_path + ".bak") && TryReadBackup(out List<RdpCredential>? recovered))
            {
                RecoveryMessage = "The saved RDP credential store was unreadable. Vivre loaded its encrypted last-known-good credential backup; the next successful credential save will repair the primary file. Host-tree and credential backups may be from different saves; re-enter any login reported as unavailable.";
                _loadedFromBackup = true;
                return recovered!;
            }

            _loadFailed = true;
            throw new InvalidDataException("The saved RDP credential store is unreadable and no valid last-known-good credential backup is available. Saving is blocked to protect the existing encrypted files.", primaryError);
        }
    }

    private void Persist(List<RdpCredential> items)
    {
        if (_loadFailed)
        {
            throw new InvalidOperationException("Saving RDP credentials is blocked because the existing encrypted files could not be loaded. Restore or remove the damaged files before saving.");
        }

        string json = JsonSerializer.Serialize(items, SerializerOptions);
        bool repairedFromBackup = _loadedFromBackup;
        bool preserveBackup = repairedFromBackup || (PathExists(_path) && !CanRead(_path));
        _write(_path, json, preserveBackup);
        _cache = items;
        _loadedFromBackup = false;
        RecoveryMessage = repairedFromBackup
            ? "Vivre loaded the encrypted last-known-good credential backup and repaired the primary credential file. Host-tree and credential backups may be from different saves; re-enter any login reported as unavailable."
            : null;
    }

    public RdpCredential? Get(string? id)
    {
        RdpCredential? credential = string.IsNullOrEmpty(id) ? null : Items.FirstOrDefault(c => c.Id == id);
        return credential is null ? null : Clone([credential]).Single();
    }

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
        List<RdpCredential> updated = Clone(Items);
        RdpCredential? cred = string.IsNullOrEmpty(id) ? null : updated.FirstOrDefault(c => c.Id == id);
        if (cred is null)
        {
            cred = new RdpCredential();
            updated.Add(cred);
        }

        cred.Domain = string.IsNullOrWhiteSpace(domain) ? null : domain.Trim();
        cred.UserName = userName;
        cred.ProtectedPassword = DpapiSecret.Protect(password);
        Persist(updated);
        return cred.Id;
    }

    public void Delete(string id)
    {
        List<RdpCredential> updated = Clone(Items);
        if (updated.RemoveAll(c => c.Id == id) > 0)
        {
            Persist(updated);
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

    private bool TryReadBackup(out List<RdpCredential>? credentials)
    {
        try
        {
            credentials = ReadRequired(_path + ".bak");
            return true;
        }
        catch (Exception) when (PathExists(_path + ".bak"))
        {
            credentials = null;
            return false;
        }
    }

    private static bool CanRead(string path)
    {
        try { _ = ReadRequired(path); return true; }
        catch { return false; }
    }

    private static List<RdpCredential> ReadRequired(string path)
    {
        if (!PathExists(path)) throw new FileNotFoundException("RDP credential store not found.", path);
        string json = File.ReadAllText(path);
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("RDP credential store is not an array.");
        foreach (JsonElement credential in document.RootElement.EnumerateArray())
            if (credential.ValueKind != JsonValueKind.Object
                || !credential.TryGetProperty(nameof(RdpCredential.Id), out _)
                || !credential.TryGetProperty(nameof(RdpCredential.UserName), out _)
                || !credential.TryGetProperty(nameof(RdpCredential.ProtectedPassword), out _))
                throw new InvalidDataException("RDP credential store has an incomplete credential schema.");
        List<RdpCredential> credentials = JsonSerializer.Deserialize<List<RdpCredential>>(json)
            ?? throw new InvalidDataException("RDP credential store contained JSON null.");
        if (credentials.Any(c => c is null || string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.UserName)
            || string.IsNullOrWhiteSpace(c.ProtectedPassword) || !IsBase64(c.ProtectedPassword)))
            throw new InvalidDataException("RDP credential store contains an invalid credential record.");
        foreach (RdpCredential credential in credentials)
        {
            // A syntactically valid base64 value can still be truncated or bit-flipped DPAPI data.
            // Decrypt only to validate backup eligibility; never retain or report the plaintext.
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("RDP credential validation requires Windows DPAPI.");
            _ = DpapiSecret.Unprotect(credential.ProtectedPassword);
        }
        return credentials;
    }

    private static bool IsBase64(string value)
    {
        try { _ = Convert.FromBase64String(value); return true; }
        catch (FormatException) { return false; }
    }

    private static bool PathExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static List<RdpCredential> Clone(List<RdpCredential> credentials) =>
        JsonSerializer.Deserialize<List<RdpCredential>>(JsonSerializer.Serialize(credentials, SerializerOptions))
        ?? throw new InvalidDataException("RDP credentials could not be copied.");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
}
