using Vivre.Core.Scripts;
using Xunit;

namespace Vivre.Core.Tests.Scripts;

public class ScriptLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vivre-scripts-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _extraDirs = [];

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
