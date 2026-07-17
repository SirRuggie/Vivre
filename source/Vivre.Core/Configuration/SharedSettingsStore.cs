using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vivre.Core.IO;
using Vivre.Core.Logging;
using Vivre.Core.Updates;

namespace Vivre.Core.Configuration;

/// <summary>
/// Reads/writes <see cref="SharedSettings"/> as the machine-wide <c>C:\ProgramData\Vivre\settings.json</c>
/// (<see cref="Environment.SpecialFolder.CommonApplicationData"/>) so every operator on the box shares the
/// operational settings (this month's CU, package folders, WUG server, install concurrency, staged-machine
/// list). Personal preferences stay per-user in <c>AppSettingsStore</c> (the Roaming per-user store); this
/// class NEVER touches the Roaming location — there is no per-user fallback.
///
/// <para><b>Load</b> does a FRESH disk read every call (no static cache — operator B's running Vivre must see
/// operator A's save; the file is ~2 KB). An absent file, corrupt JSON, or an IO error returns safe defaults
/// and never throws — Load is called from unguarded constructors — but a read failure is reported loudly via
/// <see cref="ActivityLog"/> every time (never cached).</para>
///
/// <para><b>Save</b> is synchronous (a caller must know it failed): it validates that no credential-shaped
/// field has crept into the shape, ensures the shared folder exists with an Authenticated-Users Modify ACL so
/// the file inherits it (ProgramData's owner-only-write default would otherwise let the first creator lock
/// everyone else out), then writes atomically. ANY failure propagates — no swallow, no fallback.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SharedSettingsStore
{
    private readonly string _dir;
    private readonly string _path;

    // Set once at App startup (static so every construction site shares it — the store is new()-constructed in
    // several places). Surfaces a read failure to the operator: a shared file that can't be read silently falls
    // back to defaults, and a staged-patching flag read from defaults would mis-route a 2016 box, so this must
    // not stay Debug-only. Mirrors AppSettingsStore.ActivityLog.
    internal static IActivityLog? ActivityLog { get; set; }

    // The Authenticated Users well-known SID (S-1-5-11), language-neutral. Granted Modify on the folder so any
    // operator on the box can read AND write the shared file. NOT Administrators, NOT BUILTIN\Users.
    private static readonly SecurityIdentifier AuthenticatedUsers = new(WellKnownSidType.AuthenticatedUserSid, null);

    // Property NAMES that look like they carry a secret — the Save-time guard refuses any such field.
    private static readonly Regex CredentialNamePattern =
        new("password|secret|credential|token|pwd", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <param name="directoryOverride">Test-only: point the store at a scratch directory instead of
    /// <c>C:\ProgramData\Vivre</c>. Production always passes null.</param>
    public SharedSettingsStore(string? directoryOverride = null)
    {
        // NOTE: the folder is NOT created here — an absent file must Load as defaults WITHOUT creating anything.
        // The folder (with its ACL) is created lazily on the first Save.
        _dir = directoryOverride
               ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Vivre");
        _path = Path.Combine(_dir, "settings.json");
    }

    /// <summary>Fresh disk read every call (deliberately uncached). Absent file → defaults (no file created).
    /// Corrupt JSON or an IO failure → defaults, reported loudly via <see cref="ActivityLog"/> every time —
    /// never throws (called from unguarded constructors) and never caches the failure.</summary>
    public SharedSettings Load()
    {
        try
        {
            SharedSettings settings = File.Exists(_path)
                ? JsonSerializer.Deserialize<SharedSettings>(File.ReadAllText(_path)) ?? new SharedSettings()
                : new SharedSettings();
            // A JSON round-trip resets the HashSet comparer to ordinal — rebuild with OrdinalIgnoreCase so
            // case-insensitive host lookups always work after deserialization (same as AppSettingsStore).
            settings.StagedHosts = StagedHostMatching.Normalize(settings.StagedHosts);
            return settings;
        }
        catch (Exception ex)
        {
            // Corrupt content OR a transient IO failure: return safe defaults so unguarded callers don't crash,
            // but report EVERY time (never cache) so the operator sees it and the next Load retries the file.
            ActivityLog?.Error(null,
                $"Shared settings at {_path} couldn't be read — using safe defaults until it's fixed: {ex.GetType().Name}: {ex.Message}");
            return new SharedSettings();
        }
    }

    /// <summary>Synchronous save (a caller must know it failed). Refuses credential-shaped shapes, ensures the
    /// shared folder + ACL, then writes atomically. ANY failure propagates — writing operational data to the
    /// Roaming store is forbidden, so there is no fallback of any kind.</summary>
    public void Save(SharedSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // (1) Runtime credential block — refuse to persist a shape that carries (or could carry) a secret.
        ValidateNoCredentialShapedFields(typeof(SharedSettings));

        // (2) Ensure the shared folder exists with the Authenticated-Users Modify ACL (so the file inherits it).
        EnsureSharedFolder();

        // (3) Serialize + (4) atomic write. A write failure throws straight out to the caller — no fallback.
        string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        AtomicFileWriter.Write(_path, json);
    }

    // ── Credential guard ─────────────────────────────────────────────────────────────────────────

    /// <summary>Reflection guard: throws <see cref="InvalidOperationException"/> if <paramref name="t"/> (or any
    /// of its nested Vivre config property types) declares a public property whose NAME looks credential-shaped
    /// (password/secret/credential/token/pwd) or whose TYPE is <see cref="SecureString"/> / <see cref="System.Net.NetworkCredential"/>.
    /// Exposed so a test can run it against a decoy type.</summary>
    internal static void ValidateNoCredentialShapedFields(Type t) => ValidateNoCredentialShapedFields(t, []);

    private static void ValidateNoCredentialShapedFields(Type t, HashSet<Type> visited)
    {
        if (!visited.Add(t))
        {
            return; // guard against cycles
        }

        foreach (PropertyInfo prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (CredentialNamePattern.IsMatch(prop.Name))
            {
                throw new InvalidOperationException(
                    $"Shared settings must never carry credential material: property '{t.Name}.{prop.Name}' has a "
                    + "credential-shaped name (password/secret/credential/token/pwd). Per-operator secrets stay in "
                    + "memory or in a per-user encrypted store — never in the machine-wide shared file.");
            }

            Type pt = prop.PropertyType;
            if (pt == typeof(SecureString) || pt == typeof(System.Net.NetworkCredential))
            {
                throw new InvalidOperationException(
                    $"Shared settings must never carry credential material: property '{t.Name}.{prop.Name}' is of type "
                    + $"{pt.Name}, which holds a secret. Per-operator secrets stay in memory or in a per-user encrypted "
                    + "store — never in the machine-wide shared file.");
            }

            // Recurse into our own nested config types (e.g. MonthlyCu); skip BCL/framework types and strings.
            if (pt.IsClass && pt != typeof(string) && (pt.Namespace?.StartsWith("Vivre", StringComparison.Ordinal) ?? false))
            {
                ValidateNoCredentialShapedFields(pt, visited);
            }
        }
    }

    // ── Folder + ACL ─────────────────────────────────────────────────────────────────────────────

    private void EnsureSharedFolder()
    {
        if (Directory.Exists(_dir))
        {
            // Folder already there (e.g. another operator created it). Best-effort: make sure the
            // Authenticated-Users rule is present so the first creator can't have locked others out. If we lack
            // WRITE_DAC we log the one-line remedy and carry on — a missing ACE must not fail the save.
            TryEnsureAceOnExistingFolder();
            return;
        }

        // Create the folder WITH the ACL in one step so the file inherits it — ProgramData defaults to
        // owner-only-write, which is exactly the trap that would let the first creator lock others out. A
        // creation failure (e.g. the parent is a file) throws straight out to Save's caller.
        var security = new DirectorySecurity();
        security.AddAccessRule(AuthenticatedUsersModifyRule());
        security.CreateDirectory(_dir);
    }

    private void TryEnsureAceOnExistingFolder()
    {
        try
        {
            var dirInfo = new DirectoryInfo(_dir);
            DirectorySecurity security = dirInfo.GetAccessControl();

            bool hasRule = security
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Any(r => r.AccessControlType == AccessControlType.Allow
                          && r.IdentityReference.Equals(AuthenticatedUsers)
                          && (r.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify);
            if (hasRule)
            {
                return;
            }

            security.AddAccessRule(AuthenticatedUsersModifyRule());
            dirInfo.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            // Most likely: we lack WRITE_DAC on a folder another operator created. Don't fail the save — log the
            // exact remedy so an admin can open it up for everyone.
            ActivityLog?.Warn(null,
                $"Shared settings folder {_dir} exists but its permissions couldn't be verified/updated ({ex.GetType().Name}: {ex.Message}). "
                + @"If other operators can't save patching settings, run: icacls C:\ProgramData\Vivre /grant *S-1-5-11:(OI)(CI)M");
        }
    }

    private static FileSystemAccessRule AuthenticatedUsersModifyRule() =>
        new(AuthenticatedUsers,
            FileSystemRights.Modify | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow);
}
