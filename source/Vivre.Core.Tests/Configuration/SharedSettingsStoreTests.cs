using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Vivre.Core.Configuration;
using Vivre.Core.Logging;
using Xunit;

namespace Vivre.Core.Tests.Configuration;

/// <summary>
/// Tests for the machine-wide shared operational settings store. Every test uses the
/// <c>directoryOverride</c> constructor pointed at a unique temp directory (deleted in a finally) — the real
/// <c>C:\ProgramData\Vivre</c> is never touched. ACL / DPAPI-adjacent bits run as the current user on Windows.
/// </summary>
[SupportedOSPlatform("windows")] // DirectorySecurity / SecurityIdentifier are Windows-only (the whole app is)
public class SharedSettingsStoreTests
{
    // 1 — full round-trip of all seven moved keys.
    [Fact]
    public void Round_trips_all_operational_keys()
    {
        string dir = NewTempDir();
        try
        {
            var settings = new SharedSettings
            {
                WugServer = "10.1.2.3",
                PackagesFolder = @"D:\pkgs",
                LcuPackagesFolder = @"E:\lcu",
                MonthlyCu = new MonthlyCu { Kb = "KB5099999", Arch = "x64", TargetUbr = 9339 },
                MaxSimultaneousInstalls = 77,
                WugStateConcurrency = 3,
            };
            settings.StagedHosts.Add("Server01");
            settings.StagedHosts.Add("SERVER02");

            new SharedSettingsStore(dir).Save(settings);

            SharedSettings fresh = new SharedSettingsStore(dir).Load();
            Assert.Equal("10.1.2.3", fresh.WugServer);
            Assert.Equal(@"D:\pkgs", fresh.PackagesFolder);
            Assert.Equal(@"E:\lcu", fresh.LcuPackagesFolder);
            Assert.Equal("KB5099999", fresh.MonthlyCu.Kb);
            Assert.Equal("x64", fresh.MonthlyCu.Arch);
            Assert.Equal(9339, fresh.MonthlyCu.TargetUbr);
            Assert.Equal(77, fresh.MaxSimultaneousInstalls);
            Assert.Equal(3, fresh.WugStateConcurrency);
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
            var settings = new SharedSettings();
            settings.StagedHosts.Add("ServerAlpha");
            new SharedSettingsStore(dir).Save(settings);

            SharedSettings fresh = new SharedSettingsStore(dir).Load();
            Assert.Contains("SERVERALPHA", fresh.StagedHosts); // different casing still matches
            Assert.Contains("serveralpha", fresh.StagedHosts);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // 3 — an absent file loads defaults and creates no file.
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

    // 4 — corrupt JSON loads defaults, reports an Error via the ActivityLog hook, and a later Save self-heals.
    [Fact]
    public void Corrupt_json_loads_defaults_reports_error_and_next_save_self_heals()
    {
        string dir = NewTempDir();
        IActivityLog? previous = SharedSettingsStore.ActivityLog;
        var fake = new CapturingLog();
        SharedSettingsStore.ActivityLog = fake;
        try
        {
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{ this is not valid json ");

            var store = new SharedSettingsStore(dir);
            SharedSettings loaded = store.Load();

            Assert.Equal(string.Empty, loaded.MonthlyCu.Kb); // safe defaults, not a throw
            Assert.Empty(loaded.StagedHosts);
            Assert.NotEmpty(fake.Errors);                    // reported loudly
            Assert.Contains(fake.Errors, m => m.Contains(path));

            // A subsequent Save overwrites the corrupt file (self-heal).
            store.Save(new SharedSettings { WugServer = "10.9.9.9" });
            SharedSettings reloaded = new SharedSettingsStore(dir).Load();
            Assert.Equal("10.9.9.9", reloaded.WugServer);
        }
        finally
        {
            SharedSettingsStore.ActivityLog = previous;
            Cleanup(dir);
        }
    }

    // 5 — a save failure propagates to the caller and writes nothing (no Roaming fallback).
    [Fact]
    public void Save_failure_propagates_and_writes_nothing()
    {
        string parent = NewTempDir();
        try
        {
            // Make the store's directory unreachable: its PARENT is a FILE, so the folder can't be created.
            string fileAsParent = Path.Combine(parent, "iamafile");
            File.WriteAllText(fileAsParent, "x");
            string impossibleDir = Path.Combine(fileAsParent, "Vivre");

            var store = new SharedSettingsStore(impossibleDir);

            Assert.ThrowsAny<Exception>(() => store.Save(new SharedSettings()));
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

    // 7 — the first Save creates the folder with an Authenticated-Users (S-1-5-11) Modify ACE, inherited (OI)(CI).
    [Fact]
    public void Save_creates_folder_with_authenticated_users_modify_inherited()
    {
        string parent = NewTempDir();
        try
        {
            string dir = Path.Combine(parent, "Vivre"); // does not exist yet
            Assert.False(Directory.Exists(dir));

            new SharedSettingsStore(dir).Save(new SharedSettings());
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

    // 8 — the month label round-trips through the store (Save then a fresh Load returns the same tag).
    [Fact]
    public void MonthTag_round_trips_through_the_store()
    {
        string dir = NewTempDir();
        try
        {
            var settings = new SharedSettings
            {
                MonthlyCu = new MonthlyCu { Kb = "KB5099999", Arch = "x64", TargetUbr = 9339, MonthTag = "July 2026" },
            };
            new SharedSettingsStore(dir).Save(settings);

            SharedSettings fresh = new SharedSettingsStore(dir).Load();
            Assert.Equal("July 2026", fresh.MonthlyCu.MonthTag);
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
