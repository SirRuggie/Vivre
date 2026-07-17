using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Vivre.Core.Configuration;
using Vivre.Core.Logging;
using Xunit;

namespace Vivre.Core.Tests.Configuration;

/// <summary>
/// Tests for the machine-wide shared operational settings store. Every test uses the
/// <c>directoryOverride</c> constructor pointed at a unique temp directory (deleted in a finally) — the real
/// <c>C:\ProgramData\Vivre</c> is never touched. ACL / DPAPI-adjacent bits run as the current user on Windows.
///
/// The write path is <see cref="SharedSettingsStore.Update(System.Action{SharedSettings})"/> — a sibling-safe
/// merge that changes ONLY the keys the delta touches and REFUSES (throws) if an existing file can't be read
/// back (so a degraded read can never stomp unread keys with defaults). The old whole-object <c>Save</c> is gone.
/// </summary>
[SupportedOSPlatform("windows")] // DirectorySecurity / SecurityIdentifier are Windows-only (the whole app is)
public class SharedSettingsStoreTests
{
    // 1 — full round-trip of the five operational keys (the two movers — install cap + WUG concurrency — left
    //     for the per-user store, so they're no longer here).
    [Fact]
    public void Round_trips_all_operational_keys()
    {
        string dir = NewTempDir();
        try
        {
            new SharedSettingsStore(dir).Update(s =>
            {
                s.WugServer = "10.1.2.3";
                s.PackagesFolder = @"D:\pkgs";
                s.LcuPackagesFolder = @"E:\lcu";
                s.MonthlyCu = new MonthlyCu { Kb = "KB5099999", Arch = "x64", TargetUbr = 9339 };
                s.StagedHosts.Add("Server01");
                s.StagedHosts.Add("SERVER02");
            });

            SharedSettings fresh = new SharedSettingsStore(dir).Load();
            Assert.Equal("10.1.2.3", fresh.WugServer);
            Assert.Equal(@"D:\pkgs", fresh.PackagesFolder);
            Assert.Equal(@"E:\lcu", fresh.LcuPackagesFolder);
            Assert.Equal("KB5099999", fresh.MonthlyCu.Kb);
            Assert.Equal("x64", fresh.MonthlyCu.Arch);
            Assert.Equal(9339, fresh.MonthlyCu.TargetUbr);
            Assert.Equal(2, fresh.StagedHosts.Count);
            Assert.Contains("Server01", fresh.StagedHosts);
            Assert.Contains("SERVER02", fresh.StagedHosts);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // 2 — the staged-host set stays case-insensitive after a JSON round-trip (comparer re-normalized on Load).
    [Fact]
    public void StagedHosts_is_case_insensitive_after_round_trip()
    {
        string dir = NewTempDir();
        try
        {
            new SharedSettingsStore(dir).Update(s => s.StagedHosts.Add("ServerAlpha"));

            SharedSettings fresh = new SharedSettingsStore(dir).Load();
            Assert.Contains("SERVERALPHA", fresh.StagedHosts); // different casing still matches
            Assert.Contains("serveralpha", fresh.StagedHosts);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // 3 — an absent file LOADS defaults and creates no file (the reader side; the Update side is test (d) below).
    [Fact]
    public void Absent_file_loads_defaults_and_creates_no_file()
    {
        string dir = NewTempDir();
        try
        {
            SharedSettings loaded = new SharedSettingsStore(dir).Load();

            Assert.Equal(string.Empty, loaded.MonthlyCu.Kb);
            Assert.Equal(0, loaded.MonthlyCu.TargetUbr);
            Assert.Empty(loaded.StagedHosts);
            Assert.False(File.Exists(Path.Combine(dir, "settings.json")));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // 4 — corrupt JSON: the READER (Load) stays tolerant — defaults + a loud Error hook, never a throw — while
    //     the WRITER (Update) is STRICT: it REFUSES (throws) and leaves the corrupt file BYTE-IDENTICAL on disk.
    //     DELIBERATE behavior change from the old "a subsequent Save self-heals": overwrite-on-unreadable was the
    //     wipe vector's cousin (a degraded read feeding a whole-file write). Recovery is now the operator fixing
    //     or deleting the file, guided by the surfaced error — not a silent defaults-overwrite.
    [Fact]
    public void Corrupt_json_loads_defaults_and_update_refuses_leaving_file_intact()
    {
        string dir = NewTempDir();
        IActivityLog? previous = SharedSettingsStore.ActivityLog;
        var fake = new CapturingLog();
        SharedSettingsStore.ActivityLog = fake;
        try
        {
            string path = Path.Combine(dir, "settings.json");
            const string corrupt = "{ this is not valid json ";
            File.WriteAllText(path, corrupt);
            byte[] before = File.ReadAllBytes(path);

            var store = new SharedSettingsStore(dir);

            // Reader stays tolerant: safe defaults + a loud Error, never a throw.
            SharedSettings loaded = store.Load();
            Assert.Equal(string.Empty, loaded.MonthlyCu.Kb);
            Assert.Empty(loaded.StagedHosts);
            Assert.NotEmpty(fake.Errors);
            Assert.Contains(fake.Errors, m => m.Contains(path));

            // Writer is strict: an unreadable existing file makes Update REFUSE (throw) — no defaults-stomp.
            Assert.ThrowsAny<Exception>(() => store.Update(s => s.WugServer = "10.9.9.9"));

            // The corrupt file is left byte-identical — the write never happened.
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal(corrupt, File.ReadAllText(path));
        }
        finally
        {
            SharedSettingsStore.ActivityLog = previous;
            Cleanup(dir);
        }
    }

    // 5 — an Update failure propagates to the caller and writes nothing (no Roaming fallback).
    [Fact]
    public void Update_failure_propagates_and_writes_nothing()
    {
        string parent = NewTempDir();
        try
        {
            // Make the store's directory unreachable: its PARENT is a FILE, so the folder can't be created.
            string fileAsParent = Path.Combine(parent, "iamafile");
            File.WriteAllText(fileAsParent, "x");
            string impossibleDir = Path.Combine(fileAsParent, "Vivre");

            var store = new SharedSettingsStore(impossibleDir);

            Assert.ThrowsAny<Exception>(() => store.Update(_ => { }));
            Assert.False(File.Exists(Path.Combine(impossibleDir, "settings.json")));
        }
        finally
        {
            Cleanup(parent);
        }
    }

    // 6 — the credential guard throws (naming the field) for a credential-shaped decoy and passes for the real shape.
    [Fact]
    public void Credential_shaped_field_is_rejected_and_clean_shape_passes()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SharedSettingsStore.ValidateNoCredentialShapedFields(typeof(DecoyWithCredential)));
        Assert.Contains("WugPassword", ex.Message);

        // The real shared-settings shape carries no credential material.
        SharedSettingsStore.ValidateNoCredentialShapedFields(typeof(SharedSettings)); // must not throw
    }

    // 7 — the first Update creates the folder with an Authenticated-Users (S-1-5-11) Modify ACE, inherited (OI)(CI).
    [Fact]
    public void Update_creates_folder_with_authenticated_users_modify_inherited()
    {
        string parent = NewTempDir();
        try
        {
            string dir = Path.Combine(parent, "Vivre"); // does not exist yet
            Assert.False(Directory.Exists(dir));

            new SharedSettingsStore(dir).Update(_ => { });
            Assert.True(Directory.Exists(dir));

            var authUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            DirectorySecurity security = new DirectoryInfo(dir).GetAccessControl();

            bool hasRule = security
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Any(r => r.AccessControlType == AccessControlType.Allow
                          && r.IdentityReference.Equals(authUsers)
                          && (r.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify
                          && r.InheritanceFlags.HasFlag(InheritanceFlags.ContainerInherit)
                          && r.InheritanceFlags.HasFlag(InheritanceFlags.ObjectInherit));
            Assert.True(hasRule, "Expected an Allow rule for Authenticated Users (S-1-5-11) with Modify, inherited (OI)(CI).");
        }
        finally
        {
            Cleanup(parent);
        }
    }

    // 8 — the month label round-trips through the store (Update then a fresh Load returns the same tag).
    [Fact]
    public void MonthTag_round_trips_through_the_store()
    {
        string dir = NewTempDir();
        try
        {
            new SharedSettingsStore(dir).Update(s =>
                s.MonthlyCu = new MonthlyCu { Kb = "KB5099999", Arch = "x64", TargetUbr = 9339, MonthTag = "July 2026" });

            SharedSettings fresh = new SharedSettingsStore(dir).Load();
            Assert.Equal("July 2026", fresh.MonthlyCu.MonthTag);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // (a) Reflection lock: the two movers are gone from the shared shape — they live in the per-user store now.
    [Fact]
    public void SharedSettings_no_longer_declares_the_moved_properties()
    {
        Assert.Null(typeof(SharedSettings).GetProperty("MaxSimultaneousInstalls"));
        Assert.Null(typeof(SharedSettings).GetProperty("WugStateConcurrency"));
    }

    // (b) THE INCIDENT TEST — a fully-populated shared file (all 5 live operational keys + 2 staged hosts + the 2
    //     STALE mover keys + one UNKNOWN future key) survives a single-key Update: only WugServer changes, every
    //     sibling is preserved, the unknown future key is kept, and ONLY the two stale movers are removed. This is
    //     the guarantee whose absence blanked StagedHosts on the live box.
    [Fact]
    public void Update_changes_only_the_edited_key_and_preserves_siblings_and_unknown_keys()
    {
        string dir = NewTempDir();
        try
        {
            string path = Path.Combine(dir, "settings.json");

            // A fully-populated shared file as an OLDER/OTHER build left it: all 5 live operational keys, 2 staged
            // hosts, the 2 STALE mover keys that moved to the per-user store, and one UNKNOWN future key.
            var original = new JsonObject
            {
                ["WugServer"] = "10.1.1.1",
                ["PackagesFolder"] = @"D:\pkgs",
                ["LcuPackagesFolder"] = @"E:\lcu",
                ["MonthlyCu"] = new JsonObject
                {
                    ["Kb"] = "KB5099999",
                    ["Arch"] = "x64",
                    ["TargetUbr"] = 9339,
                    ["MonthTag"] = "July 2026",
                },
                ["MaxSimultaneousInstalls"] = 77, // stale mover — must be REMOVED
                ["WugStateConcurrency"] = 3,       // stale mover — must be REMOVED
                ["StagedHosts"] = new JsonArray("Server01", "SERVER02"),
                ["FutureKey"] = 123,               // unknown to this build — must SURVIVE
            };
            File.WriteAllText(path, original.ToJsonString());

            // One narrow edit.
            new SharedSettingsStore(dir).Update(s => s.WugServer = "10.9.9.9");

            JsonObject after = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

            // The edited key changed.
            Assert.Equal("10.9.9.9", (string?)after["WugServer"]);

            // Siblings intact.
            Assert.Equal(@"D:\pkgs", (string?)after["PackagesFolder"]);
            Assert.Equal(@"E:\lcu", (string?)after["LcuPackagesFolder"]);
            Assert.Equal("KB5099999", (string?)after["MonthlyCu"]!["Kb"]);
            Assert.Equal(9339, (int?)after["MonthlyCu"]!["TargetUbr"]);
            Assert.Equal("July 2026", (string?)after["MonthlyCu"]!["MonthTag"]);

            // BOTH staged hosts intact.
            List<string?> staged = after["StagedHosts"]!.AsArray().Select(n => (string?)n).ToList();
            Assert.Equal(2, staged.Count);
            Assert.Contains("Server01", staged);
            Assert.Contains("SERVER02", staged);

            // The unknown future key survived with its value.
            Assert.Equal(123, (int?)after["FutureKey"]);

            // The two stale mover keys were REMOVED (ObsoleteSharedKeys cleanup).
            Assert.False(after.ContainsKey("MaxSimultaneousInstalls"));
            Assert.False(after.ContainsKey("WugStateConcurrency"));

            // Nothing else differs: the top-level key set is exactly these six.
            Assert.Equal(
                new[] { "FutureKey", "LcuPackagesFolder", "MonthlyCu", "PackagesFolder", "StagedHosts", "WugServer" },
                after.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // (c) Degraded-read refusal (IO): an EXISTING file that can't be read (exclusively locked) makes Update fail
    //     LOUDLY and leaves the file byte-identical — no stomp. Distinct from test 4's malformed-JSON parse case.
    [Fact]
    public void Update_refuses_when_the_existing_file_cannot_be_read_and_leaves_it_untouched()
    {
        string dir = NewTempDir();
        try
        {
            string path = Path.Combine(dir, "settings.json");

            // Seed a valid file, then capture its exact bytes.
            new SharedSettingsStore(dir).Update(s => s.WugServer = "10.1.1.1");
            byte[] before = File.ReadAllBytes(path);

            // Hold it open with an exclusive (no-share) lock so Update's fresh read fails.
            using (var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<Exception>(
                    () => new SharedSettingsStore(dir).Update(s => s.WugServer = "10.9.9.9"));
            }

            // The file is byte-identical — the degraded read refused the write, no stomp.
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // (d) Absent file → Update creates it with defaults + the mutation (first-run write stays legal). The
    //     Load-of-absent-stays-defaults-no-create half is test 3 above.
    [Fact]
    public void Update_on_an_absent_file_creates_it_with_defaults_plus_the_mutation()
    {
        string dir = NewTempDir();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            Assert.False(File.Exists(path)); // first run: nothing on disk

            new SharedSettingsStore(dir).Update(s => s.WugServer = "10.9.9.9");

            Assert.True(File.Exists(path)); // created

            SharedSettings fresh = new SharedSettingsStore(dir).Load();
            Assert.Equal("10.9.9.9", fresh.WugServer);                        // the mutation
            Assert.Equal(@"C:\Vivre\VivrePackages", fresh.LcuPackagesFolder); // an untouched default
            Assert.Empty(fresh.StagedHosts);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "VivreSharedSettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // A stray lock in teardown must not fail the test; the temp dir is disposable.
        }
    }

    /// <summary>A decoy config shape with a credential-shaped property name — the guard must reject it.</summary>
    private sealed class DecoyWithCredential
    {
        public string WugServer { get; set; } = string.Empty;
        public string WugPassword { get; set; } = string.Empty; // (?i)password → rejected
    }

    /// <summary>Captures Error messages so a test can assert the read-failure hook fired.</summary>
    private sealed class CapturingLog : IActivityLog
    {
        public List<string> Errors { get; } = [];
        public System.Collections.ObjectModel.ObservableCollection<LogEntry> Entries { get; } = [];

        public void Info(string? machine, string message) { }
        public void Warn(string? machine, string message) { }
        public void Error(string? machine, string message) => Errors.Add(message);
        public void Clear() { }
    }
}
