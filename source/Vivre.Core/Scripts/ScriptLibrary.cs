namespace Vivre.Core.Scripts;

/// <summary>
/// File-system implementation of <see cref="IScriptLibrary"/>. Scripts live as
/// <c>.ps1</c> files in a folder (default <c>%APPDATA%\Vivre\Scripts</c>), organised
/// into category sub-folders. On first use the folder is created and seeded from the
/// curated scripts that ship with the app (copy-if-missing, so the user's own additions
/// and edits are never overwritten). If no shipped bundle is present (e.g. unit tests),
/// a couple of harmless examples are written instead so the list isn't empty.
/// </summary>
public sealed class ScriptLibrary : IScriptLibrary
{
    public ScriptLibrary() : this(DefaultDirectory())
    {
    }

    public ScriptLibrary(string directory) : this(directory, BundleDirectory())
    {
    }

    private readonly string? _bundleDirectory;

    /// <param name="directory">The live, user-writable library folder.</param>
    /// <param name="bundleDirectory">Folder of scripts that ship with the app to seed from, or null.</param>
    public ScriptLibrary(string directory, string? bundleDirectory)
    {
        Directory = directory;
        _bundleDirectory = bundleDirectory;
        EnsureSeeded(bundleDirectory);
    }

    public string Directory { get; }

    public IReadOnlyList<ScriptFile> List() =>
        [.. System.IO.Directory
            .EnumerateFiles(Directory, "*.ps1", SearchOption.AllDirectories)
            .Select(path => new ScriptFile(Path.GetFileNameWithoutExtension(path), path, CategoryOf(path)))
            .OrderBy(s => s.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];

    public string Load(ScriptFile script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return File.ReadAllText(script.FullPath);
    }

    public ScriptFile Save(string name, string content) => Save(name, content, null);

    public ScriptFile Save(string name, string content, string? category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        string? safeCategory = SanitizeCategory(category);
        string folder = safeCategory is null ? Directory : Path.Combine(Directory, safeCategory);
        System.IO.Directory.CreateDirectory(folder);

        string path = Path.Combine(folder, safeName + ".ps1");
        File.WriteAllText(path, content ?? string.Empty);
        return new ScriptFile(Path.GetFileNameWithoutExtension(path), path, CategoryOf(path));
    }

    /// <summary>
    /// Reduces a user-typed category to a single safe folder name, or null for the root. Strips
    /// invalid filename characters (which include path separators) and rejects "."/".." so a
    /// typed category can never escape the library folder.
    /// </summary>
    private static string? SanitizeCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return null;
        }

        string safe = string.Join("_", category.Split(Path.GetInvalidFileNameChars())).Trim();
        return safe.Length == 0 || safe == "." || safe == ".." ? null : safe;
    }

    public bool IsDefault(ScriptFile script)
    {
        ArgumentNullException.ThrowIfNull(script);

        if (_bundleDirectory is null || !System.IO.Directory.Exists(_bundleDirectory))
        {
            return false;
        }

        // A script is a built-in default if the same relative path ships in the bundle.
        string relative = Path.GetRelativePath(Directory, script.FullPath);
        return File.Exists(Path.Combine(_bundleDirectory, relative));
    }

    public void Delete(ScriptFile script)
    {
        ArgumentNullException.ThrowIfNull(script);

        if (IsDefault(script))
        {
            throw new InvalidOperationException($"'{script.Name}' is a built-in script and cannot be deleted.");
        }

        if (File.Exists(script.FullPath))
        {
            File.Delete(script.FullPath);
        }

        // Tidy up a now-empty user folder (never the library root).
        string? folder = Path.GetDirectoryName(script.FullPath);
        if (folder is not null
            && !PathsEqual(folder, Directory)
            && System.IO.Directory.Exists(folder)
            && !System.IO.Directory.EnumerateFileSystemEntries(folder).Any())
        {
            System.IO.Directory.Delete(folder);
        }
    }

    /// <summary>Relative folder of <paramref name="path"/> under the library root (empty for root).</summary>
    private string CategoryOf(string path)
    {
        string dir = Path.GetDirectoryName(path) ?? Directory;
        string relative = Path.GetRelativePath(Directory, dir);
        return relative == "." ? string.Empty : relative;
    }

    private static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vivre", "Scripts");

    /// <summary>Curated scripts copied next to the app by the build (<c>Scripts\</c> beside the exe).</summary>
    private static string BundleDirectory() => Path.Combine(AppContext.BaseDirectory, "Scripts");

    private void EnsureSeeded(string? bundleDirectory)
    {
        System.IO.Directory.CreateDirectory(Directory);

        // Top up from the shipped bundle without overwriting the user's own files: copy each
        // bundled script only when nothing exists at the same relative path. This also brings
        // in newly-added bundled scripts on later runs.
        if (bundleDirectory is not null
            && !PathsEqual(bundleDirectory, Directory)
            && System.IO.Directory.Exists(bundleDirectory))
        {
            foreach (string source in System.IO.Directory.EnumerateFiles(bundleDirectory, "*.ps1", SearchOption.AllDirectories))
            {
                string target = Path.Combine(Directory, Path.GetRelativePath(bundleDirectory, source));
                if (!File.Exists(target))
                {
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target);
                }
            }

            return;
        }

        // No bundle available — seed a couple of harmless, read-only examples.
        if (List().Count == 0)
        {
            File.WriteAllText(
                Path.Combine(Directory, "Get running services.ps1"),
                "Get-Service | Where-Object Status -eq 'Running' | Select-Object Name, DisplayName" + Environment.NewLine);
            File.WriteAllText(
                Path.Combine(Directory, "Pending reboot check.ps1"),
                "Test-Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Component Based Servicing\\RebootPending'" + Environment.NewLine);
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
