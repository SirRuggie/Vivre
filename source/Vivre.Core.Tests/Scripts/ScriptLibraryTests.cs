using Vivre.Core.Scripts;
using Xunit;

namespace Vivre.Core.Tests.Scripts;

public class ScriptLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vivre-scripts-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _extraDirs = [];

    [Fact]
    public void Atomic_backup_preserves_exact_displaced_contents_and_requires_existing_target()
    {
        System.IO.Directory.CreateDirectory(_dir);
        string target = Path.Combine(_dir, "target.ps1"), backup = Path.Combine(_dir, "recovery.previous");
        File.WriteAllText(target, "external edit");
        Vivre.Core.IO.AtomicFileWriter.Write(target, "my edit", true, backup);
        Assert.Equal("external edit", File.ReadAllText(backup));
        Assert.Equal("my edit", File.ReadAllText(target));
        File.Delete(target);
        Assert.ThrowsAny<IOException>(() => Vivre.Core.IO.AtomicFileWriter.Write(target, "resurrect", true, backup + "2"));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void Colliding_names_require_approval_and_stale_revision_is_rejected()
    {
        var library = new ScriptLibrary(_dir);
        var saved = library.Save("a:b", "original", "Custom");
        var conflict = Assert.Throws<ScriptOverwriteRequiredException>(() => library.Save("a?b", "replacement", "Custom"));
        Assert.Equal(saved.FullPath, conflict.Destination);
        Assert.Equal("original", library.Load(saved));
        library.Save("a?b", "approved", "Custom", conflict.Revision);
        Assert.Equal("approved", library.Load(saved));
        Assert.Throws<IOException>(() => library.Save("a?b", "stale", "Custom", conflict.Revision));
        Assert.Equal("approved", library.Load(saved));
        var second = Assert.Throws<ScriptOverwriteRequiredException>(() => library.Save("a?b", "new", "Custom"));
        File.Delete(saved.FullPath);
        Assert.Throws<IOException>(() => library.Save("a?b", "resurrect", "Custom", second.Revision));
        Assert.False(File.Exists(saved.FullPath));
    }

    [Fact]
    public void New_directory_is_seeded_with_examples()
    {
        var library = new ScriptLibrary(_dir);

        Assert.NotEmpty(library.List());
    }

    [Fact]
    public void Save_then_List_round_trips()
    {
        var library = new ScriptLibrary(_dir);

        ScriptFile saved = library.Save("My Script", "hostname");

        Assert.Equal("My Script", saved.Name);
        Assert.Contains(library.List(), s => s.Name == "My Script");
        Assert.Equal("hostname", library.Load(saved));
    }

    [Fact]
    public void Save_sanitizes_invalid_filename_characters()
    {
        var library = new ScriptLibrary(_dir);

        ScriptFile saved = library.Save("bad:/name", "x");

        Assert.DoesNotContain(':', saved.Name);
        Assert.True(File.Exists(saved.FullPath));
    }

    [Fact]
    public void List_only_returns_ps1_files()
    {
        var library = new ScriptLibrary(_dir);
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "ignore me");

        Assert.DoesNotContain(library.List(), s => s.Name == "notes");
    }

    [Fact]
    public void Save_into_category_creates_the_subfolder()
    {
        var library = new ScriptLibrary(_dir, bundleDirectory: null);

        ScriptFile saved = library.Save("My Script", "hostname", "My Scripts");

        Assert.Equal("My Scripts", saved.Category);
        Assert.True(File.Exists(saved.FullPath));
        Assert.Contains(library.List(), s => s.Name == "My Script" && s.Category == "My Scripts");
    }

    [Theory]
    [InlineData("..")]
    [InlineData("  ")]
    public void Save_with_dot_or_blank_category_falls_back_to_root(string category)
    {
        var library = new ScriptLibrary(_dir, bundleDirectory: null);

        ScriptFile saved = library.Save("X", "y", category);

        Assert.Equal(string.Empty, saved.Category);
        Assert.Equal(Path.GetFullPath(_dir), Path.GetFullPath(Path.GetDirectoryName(saved.FullPath)!));
    }

    [Fact]
    public void Save_category_never_escapes_the_library_folder()
    {
        var library = new ScriptLibrary(_dir, bundleDirectory: null);

        // Path separators are stripped to a single safe folder name — no directory traversal.
        ScriptFile saved = library.Save("X", "y", "../escape");

        string full = Path.GetFullPath(saved.FullPath);
        Assert.StartsWith(Path.GetFullPath(_dir) + Path.DirectorySeparatorChar, full);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, saved.Category);
    }

    [Fact]
    public void List_reports_subfolder_as_category()
    {
        var library = new ScriptLibrary(_dir, bundleDirectory: null);
        Directory.CreateDirectory(Path.Combine(_dir, "Reboot"));
        File.WriteAllText(Path.Combine(_dir, "Reboot", "Force.ps1"), "Restart-Computer -Force");

        ScriptFile force = library.List().Single(s => s.Name == "Force");

        Assert.Equal("Reboot", force.Category);
    }

    [Fact]
    public void Seeds_categorised_scripts_from_the_bundle()
    {
        string bundle = NewTempDir();
        Directory.CreateDirectory(Path.Combine(bundle, "Info"));
        File.WriteAllText(Path.Combine(bundle, "Info", "Uptime.ps1"), "uptime");

        var library = new ScriptLibrary(_dir, bundle);

        Assert.Contains(library.List(), s => s.Name == "Uptime" && s.Category == "Info");
    }

    [Fact]
    public void Bundled_scripts_are_default_and_user_scripts_are_not()
    {
        string bundle = NewTempDir();
        Directory.CreateDirectory(Path.Combine(bundle, "Reboot"));
        File.WriteAllText(Path.Combine(bundle, "Reboot", "Force.ps1"), "x");
        var library = new ScriptLibrary(_dir, bundle);

        ScriptFile bundled = library.List().Single(s => s.Name == "Force");
        ScriptFile mine = library.Save("Mine", "y", "My Scripts");

        Assert.True(library.IsDefault(bundled));
        Assert.False(library.IsDefault(mine));
    }

    [Fact]
    public void Delete_removes_a_user_script()
    {
        var library = new ScriptLibrary(_dir, bundleDirectory: null);
        ScriptFile mine = library.Save("Mine", "y", "My Scripts");

        library.Delete(mine);

        Assert.False(File.Exists(mine.FullPath));
        Assert.DoesNotContain(library.List(), s => s.Name == "Mine");
    }

    [Fact]
    public void Delete_refuses_a_built_in_default()
    {
        string bundle = NewTempDir();
        Directory.CreateDirectory(Path.Combine(bundle, "Reboot"));
        File.WriteAllText(Path.Combine(bundle, "Reboot", "Force.ps1"), "x");
        var library = new ScriptLibrary(_dir, bundle);
        ScriptFile bundled = library.List().Single(s => s.Name == "Force");

        Assert.Throws<InvalidOperationException>(() => library.Delete(bundled));
        Assert.True(File.Exists(bundled.FullPath)); // left intact
    }

    [Fact]
    public void Bundle_seeding_does_not_overwrite_user_edited_scripts()
    {
        string bundle = NewTempDir();
        Directory.CreateDirectory(Path.Combine(bundle, "Reboot"));
        File.WriteAllText(Path.Combine(bundle, "Reboot", "Force.ps1"), "bundled");

        Directory.CreateDirectory(Path.Combine(_dir, "Reboot"));
        File.WriteAllText(Path.Combine(_dir, "Reboot", "Force.ps1"), "user-edited");

        var library = new ScriptLibrary(_dir, bundle);

        ScriptFile force = library.List().Single(s => s.Name == "Force");
        Assert.Equal("user-edited", library.Load(force));
    }

    [Fact]
    public void Legacy_unmodified_built_in_is_updated_but_an_edit_is_preserved_and_visible()
    {
        string bundle = NewTempDir();
        string relative = Path.Combine("Repair", "Rebuild WMI repository.ps1");
        Directory.CreateDirectory(Path.Combine(bundle, "Repair"));
        File.WriteAllText(Path.Combine(bundle, relative), "corrected");
        Directory.CreateDirectory(Path.Combine(_dir, "Repair"));
        File.WriteAllText(Path.Combine(_dir, relative), "user edit");

        var library = new ScriptLibrary(_dir, bundle);

        Assert.Equal("user edit", File.ReadAllText(Path.Combine(_dir, relative)));
        ScriptUpdateConflict conflict = Assert.Single(library.UpdateConflicts);
        Assert.Equal(relative, conflict.RelativePath);
    }

    [Fact]
    public void Exact_legacy_built_in_is_migrated_to_current_bundle()
    {
        string legacy = string.Join(Environment.NewLine,
            "# Verify the WMI repository; salvage it only if it reports inconsistent. Safe first step for WMI corruption.",
            "$check = (winmgmt /verifyrepository) | Out-String",
            "if ($check -match 'consistent') {",
            "    'WMI repository is consistent. No action needed.'",
            "} else {",
            "    winmgmt /salvagerepository | Out-Null",
            "    'WMI repository was inconsistent - salvage attempted. Re-check: ' + ((winmgmt /verifyrepository) | Out-String).Trim()",
            "}", "");
        string bundle = NewTempDir();
        string relative = Path.Combine("Repair", "Rebuild WMI repository.ps1");
        Directory.CreateDirectory(Path.Combine(bundle, "Repair"));
        File.WriteAllText(Path.Combine(bundle, relative), "corrected");
        Directory.CreateDirectory(Path.Combine(_dir, "Repair"));
        File.WriteAllText(Path.Combine(_dir, relative), legacy);

        var library = new ScriptLibrary(_dir, bundle);

        Assert.Equal("corrected", File.ReadAllText(Path.Combine(_dir, relative)));
        Assert.Empty(library.UpdateConflicts);
    }

    [Fact]
    public void Reset_to_default_replaces_a_conflict_and_notifies()
    {
        string bundle = NewTempDir();
        Directory.CreateDirectory(Path.Combine(bundle, "Info"));
        File.WriteAllText(Path.Combine(bundle, "Info", "Uptime and last boot.ps1"), "bundled");
        var library = new ScriptLibrary(_dir, bundle);
        ScriptFile script = library.List().Single(s => s.Name == "Uptime and last boot");
        File.WriteAllText(script.FullPath, "edited");
        var reopened = new ScriptLibrary(_dir, bundle);
        script = reopened.List().Single(s => s.Name == "Uptime and last boot");
        int notifications = 0;
        reopened.LibraryChanged += (_, _) => notifications++;

        reopened.ResetToDefault(script);

        Assert.Equal("bundled", reopened.Load(script));
        Assert.Empty(reopened.UpdateConflicts);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void Policy_verifies_exact_execution_content_and_is_conservative_for_edits()
    {
        string bundle = NewTempDir();
        Directory.CreateDirectory(Path.Combine(bundle, "Info"));
        File.WriteAllText(Path.Combine(bundle, "Info", "Uptime and last boot.ps1"), "bundled");
        var library = new ScriptLibrary(_dir, bundle);
        ScriptFile script = library.List().Single(s => s.Name == "Uptime and last boot");

        ScriptPolicy verified = library.GetPolicy(script, "bundled");
        ScriptPolicy edited = library.GetPolicy(script, "bundled`n# editor change");

        Assert.True(verified.IsVerifiedBuiltIn);
        Assert.Equal(ScriptOperationRisk.ReadOnly, verified.OperationRisk);
        Assert.False(edited.IsVerifiedBuiltIn);
        Assert.Equal(ScriptOperationRisk.ExclusiveMutation, edited.OperationRisk);
    }

    [Fact]
    public void Persisted_bundle_hash_updates_an_unchanged_script_on_the_next_version()
    {
        string bundle = NewTempDir();
        Directory.CreateDirectory(Path.Combine(bundle, "Info"));
        string bundled = Path.Combine(bundle, "Info", "Uptime.ps1");
        File.WriteAllText(bundled, "version one");
        _ = new ScriptLibrary(_dir, bundle);
        File.WriteAllText(bundled, "version two");

        var upgraded = new ScriptLibrary(_dir, bundle);

        Assert.Equal("version two", upgraded.Load(upgraded.List().Single(s => s.Name == "Uptime")));
    }

    [Fact]
    public void Corrupt_bundle_manifest_never_authorizes_overwriting_an_edit()
    {
        string bundle = NewTempDir();
        Directory.CreateDirectory(Path.Combine(bundle, "Info"));
        string bundled = Path.Combine(bundle, "Info", "Uptime.ps1");
        File.WriteAllText(bundled, "version one");
        _ = new ScriptLibrary(_dir, bundle);
        string current = Path.Combine(_dir, "Info", "Uptime.ps1");
        File.WriteAllText(current, "user edit");
        File.WriteAllText(Path.Combine(_dir, ".vivre-bundled-hashes.json"), "not json");
        File.WriteAllText(bundled, "version two");

        var upgraded = new ScriptLibrary(_dir, bundle);

        Assert.Equal("user edit", File.ReadAllText(current));
        Assert.Single(upgraded.UpdateConflicts);
    }

    [Fact]
    public void Script_references_outside_the_library_are_rejected()
    {
        var library = new ScriptLibrary(_dir, bundleDirectory: null);
        string outsideDir = NewTempDir();
        Directory.CreateDirectory(outsideDir);
        string outside = Path.Combine(outsideDir, "outside.ps1");
        File.WriteAllText(outside, "safe");
        var forged = new ScriptFile("outside", outside, string.Empty);

        Assert.Throws<ArgumentException>(() => library.Load(forged));
        Assert.Throws<ArgumentException>(() => library.IsDefault(forged));
        Assert.Throws<ArgumentException>(() => library.GetPolicy(forged, "safe"));
        Assert.Throws<ArgumentException>(() => library.ResetToDefault(forged));
    }

    private string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vivre-scripts-" + Guid.NewGuid().ToString("N"));
        _extraDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (string dir in _extraDirs.Append(_dir))
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
