using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Vivre.Core.IO;
using Vivre.Core.Logging;
using Vivre.Core.Updates;

namespace Vivre.Core.Configuration;

/// <summary>
/// Reads/writes <see cref="SharedSettings"/> as the machine-wide <c>C:\ProgramData\Vivre\settings.json</c>
/// (<see cref="Environment.SpecialFolder.CommonApplicationData"/>) so every operator on the box shares the
/// operational settings (this month's CU, package folders, WUG server, staged-machine list). Personal
/// preferences stay per-user in <c>AppSettingsStore</c> (the Roaming per-user store); this class NEVER
/// touches the Roaming location — there is no per-user fallback.
///
/// <para><b>The read/write contract is deliberately SPLIT — tolerant for readers, strict for writers:</b></para>
///
/// <para><b>Load</b> (READERS) does a FRESH disk read every call (no static cache — operator B's running Vivre
/// must see operator A's save; the file is ~2 KB). An absent file, corrupt JSON, or an IO error returns safe
/// defaults and never throws — Load is called from unguarded constructors — but a read failure is reported
/// loudly via <see cref="ActivityLog"/> every time (never cached).</para>
///
/// <para><b>Update</b> (WRITERS) is synchronous, takes a <see cref="Action{SharedSettings}"/> DELTA (never a
/// whole object), and is <b>structurally incapable of dropping a key it didn't intend to change</b>: it reads
/// the CURRENT file fresh and — if the file EXISTS but can't be read or parsed — REFUSES (throws) rather than
/// stomping unread settings with defaults (the exact wipe vector this replaces: a degraded read must never feed
/// a save). It then applies the delta and writes back by MERGING the typed shape onto the raw on-disk JSON, so
/// only the POCO-declared keys are overwritten, only the <see cref="ObsoleteSharedKeys"/> are removed, and every
/// OTHER key — including keys an older/newer build wrote and this one doesn't know — is preserved verbatim. It
/// validates that no credential-shaped field has crept into the shape and ensures the shared folder exists with
/// an Authenticated-Users Modify ACL so the file inherits it (ProgramData's owner-only-write default would
/// otherwise let the first creator lock everyone else out). ANY failure propagates — no swallow, no fallback.</para>
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

    // Property NAMES that look like they carry a secret — the Update-time guard refuses any such field.
    private static readonly Regex CredentialNamePattern =
        new("password|secret|credential|token|pwd", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Keys that USED to live in the shared file but have moved to the per-user store (the install cap and the
    // WUG state-check concurrency, now personal in AppSettings). Update REMOVES these on every write so a stale
    // shared value can't linger and mislead a reader — this is the ONLY set of keys a merge ever deletes. Every
    // OTHER key the POCO doesn't own (an older/newer build's key) is preserved verbatim.
    private static readonly string[] ObsoleteSharedKeys = ["MaxSimultaneousInstalls", "WugStateConcurrency"];

    // Indented output so the shared file stays human-readable/diffable (matches the prior Save format).
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

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

    /// <summary>Sibling-safe mutate-and-save (this REPLACES the old whole-object Save, which could stomp keys it
    /// never read). <paramref name="mutate"/> expresses only the DELTA; Update reads the CURRENT file fresh,
    /// applies the delta to the typed view, and writes back by MERGING onto the raw on-disk JSON so ONLY the keys
    /// this build declares are overwritten. Every other key (including keys an older/newer build wrote) is
    /// preserved; only the <see cref="ObsoleteSharedKeys"/> are removed. If the file EXISTS but can't be read or
    /// parsed the write is REFUSED (throws) — a degraded read must NEVER feed a save (that is the wipe vector this
    /// replaces). An ABSENT file starts from defaults (first-run creation stays legal). Synchronous; ALL failures
    /// propagate (the credential guard, a refused read, a folder/ACL failure, a write failure) — no swallow, no
    /// fallback. Contrast <see cref="Load"/>, which stays tolerant (defaults, never throws) for unguarded readers.</summary>
    public void Update(Action<SharedSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        // (1) Runtime credential block — refuse to persist a shape that carries (or could carry) a secret.
        ValidateNoCredentialShapedFields(typeof(SharedSettings));

        // (2) Read the CURRENT file fresh. Absent → an empty object (defaults fill in on merge). Present but
        //     unreadable/unparseable → REFUSE (throw): never let a degraded read feed a save.
        JsonObject onDisk = ReadRawForWriteOrRefuse();
        SharedSettings typed = onDisk.Deserialize<SharedSettings>() ?? new SharedSettings();
        // Rebuild the staged-host set with the OrdinalIgnoreCase comparer (a JSON round-trip resets it to
        // ordinal) so the caller's case-insensitive Add/Remove behaves exactly as it did through the typed API.
        typed.StagedHosts = StagedHostMatching.Normalize(typed.StagedHosts);

        // (3) Apply the caller's change to the typed view.
        mutate(typed);

        // (4) Merge the typed view back onto the raw object: overwrite ONLY POCO-declared keys, drop ONLY the
        //     obsolete keys, preserve everything else (unknown/future keys survive).
        JsonObject merged = MergeTypedOntoRaw(typed, onDisk);

        // (5) Ensure the shared folder + ACL, then write atomically. Any failure propagates.
        EnsureSharedFolder();
        AtomicFileWriter.Write(_path, merged.ToJsonString(SerializerOptions));
    }

    /// <summary>Reads the current file as a <see cref="JsonObject"/> for a write. Absent → an empty object.
    /// Present but unreadable (IO) or unparseable (bad JSON / non-object root) → THROWS with the real cause so
    /// the write is refused rather than stomping settings that couldn't be read back.</summary>
    private JsonObject ReadRawForWriteOrRefuse()
    {
        if (!File.Exists(_path))
        {
            return new JsonObject(); // first run — start from an empty object; defaults fill in on the merge.
        }

        string raw;
        try
        {
            raw = File.ReadAllText(_path);
        }
        catch (Exception ex)
        {
            throw new IOException(
                $"The shared settings file ({_path}) exists but couldn't be read, so the save was refused rather "
                + "than overwriting settings that couldn't be read back — fix or free the file, then retry "
                + $"({ex.GetType().Name}: {ex.Message}).", ex);
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(raw);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"The shared settings file ({_path}) exists but couldn't be parsed as JSON, so the save was refused "
                + "rather than overwriting settings that couldn't be read back — fix or delete the file, then retry "
                + $"({ex.GetType().Name}: {ex.Message}).", ex);
        }

        return node as JsonObject
               ?? throw new InvalidDataException(
                   $"The shared settings file ({_path}) exists but its root isn't a JSON object, so the save was "
                   + "refused rather than overwriting it — fix or delete the file, then retry.");
    }

    /// <summary>Overlays the typed shape's own (POCO-declared) keys onto the raw on-disk object, removes ONLY the
    /// <see cref="ObsoleteSharedKeys"/>, and leaves every other key exactly as it was on disk (so an older/newer
    /// build's unknown keys survive a save by this build).</summary>
    private static JsonObject MergeTypedOntoRaw(SharedSettings typed, JsonObject onDisk)
    {
        // The typed object's own keys — exactly the POCO-declared shape.
        JsonObject typedObj = JsonSerializer.SerializeToNode(typed, SerializerOptions) as JsonObject
                              ?? new JsonObject();

        // Overlay each POCO key onto the raw object (overwrites the known keys, adds any missing). DeepClone so
        // the node detaches from typedObj — a JsonNode can't have two parents.
        foreach (KeyValuePair<string, JsonNode?> kv in typedObj)
        {
            onDisk[kv.Key] = kv.Value?.DeepClone();
        }

        // Remove ONLY the obsolete keys — the sole deletion a merge ever performs.
        foreach (string obsolete in ObsoleteSharedKeys)
        {
            onDisk.Remove(obsolete);
        }

        return onDisk;
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
