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
    private static readonly IReadOnlyDictionary<string, string> LegacyBundledHashes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"Repair\Rebuild WMI repository.ps1"] = "0d0e8168a83a10c971a335a5107e1d48f4a4e62fcfcce0a9160aa42538dcb939",
            [@"Repair\Restart Windows Update (reset cache).ps1"] = "86759de80ff9de58a4c83da5882f55b1f51c23a95d7944a34dc79cbdfb55117c",
            [@"Windows Update\Count missing updates.ps1"] = "5e0ac9b667da8b3815fecace3d052df891d81bdc02ec77ad4ad0ab3b1bde084d",
            [@"Windows Update\Download and install updates.ps1"] = "445423f0fb83b6905a63a7238a3c6458383b8e59da7fab60be289f721ef34545"
        };

    private readonly List<ScriptUpdateConflict> _updateConflicts = [];
    private const string BundleManifestName = ".vivre-bundled-hashes.json";

    public event EventHandler? LibraryChanged;
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

    public IReadOnlyList<ScriptUpdateConflict> UpdateConflicts => _updateConflicts;

    public IReadOnlyList<ScriptFile> List() =>
        [.. System.IO.Directory
            .EnumerateFiles(Directory, "*.ps1", SearchOption.AllDirectories)
            .Select(path => new ScriptFile(Path.GetFileNameWithoutExtension(path), path, CategoryOf(path)))
            .OrderBy(s => s.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];

    public ScriptLibrarySnapshot Snapshot()
    {
        IReadOnlyList<ScriptFile> scripts = List();
        IReadOnlyList<string> categories = [.. scripts.Select(s => s.Category)
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)];
        return new ScriptLibrarySnapshot(scripts, categories, [.. _updateConflicts]);
    }

    public string Load(ScriptFile script)
    {
        ArgumentNullException.ThrowIfNull(script);
        _ = RelativePathOf(script);
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
        WriteTextAtomically(path, content ?? string.Empty);
        RefreshConflicts();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
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
        string relative = RelativePathOf(script);

        if (_bundleDirectory is null || !System.IO.Directory.Exists(_bundleDirectory))
        {
            return false;
        }

        // A script is a built-in default if the same relative path ships in the bundle.
        return File.Exists(Path.Combine(_bundleDirectory, relative));
    }

    public ScriptPolicy GetPolicy(ScriptFile script) => GetPolicy(script, Load(script));

    public ScriptPolicy GetPolicy(ScriptFile script, string executionContent)
    {
        ArgumentNullException.ThrowIfNull(script);
        string relative = RelativePathOf(script);
        string? bundled = BundlePath(relative);
        bool verified = bundled is not null
            && string.Equals(executionContent, File.ReadAllText(bundled), StringComparison.Ordinal);
        ScriptOperationRisk risk = verified ? RiskOf(relative) : ScriptOperationRisk.ExclusiveMutation;
        return new ScriptPolicy(verified, CanDelete: !IsDefault(script), CanReset: bundled is not null && !verified, risk);
    }

    public void ResetToDefault(ScriptFile script)
    {
        ArgumentNullException.ThrowIfNull(script);
        string relative = RelativePathOf(script);
        string? bundled = BundlePath(relative);
        if (bundled is null)
        {
            throw new InvalidOperationException($"'{script.Name}' is not a built-in script and cannot be reset.");
        }

        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(script.FullPath)!);
        ReplaceAtomically(bundled, script.FullPath);
        RefreshConflicts();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
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

        RefreshConflicts();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
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
            Dictionary<string, string> priorHashes = LoadBundleManifest();
            var currentHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string source in System.IO.Directory.EnumerateFiles(bundleDirectory, "*.ps1", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(bundleDirectory, source);
                string target = Path.Combine(Directory, relative);
                string sourceHash = HashOf(source);
                currentHashes[relative] = sourceHash;
                if (!File.Exists(target))
                {
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    ReplaceAtomically(source, target);
                }
                else
                {
                    string targetHash = HashOf(target);
                    string? priorHash = priorHashes.GetValueOrDefault(relative)
                        ?? LegacyBundledHashes.GetValueOrDefault(relative);
                    if (priorHash is not null
                        && string.Equals(targetHash, priorHash, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(sourceHash, priorHash, StringComparison.OrdinalIgnoreCase))
                    {
                        ReplaceAtomically(source, target);
                    }
                }
            }

            SaveBundleManifest(currentHashes);
            RefreshConflicts();
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

    private string? BundlePath(string relative)
    {
        if (_bundleDirectory is null || !System.IO.Directory.Exists(_bundleDirectory)) return null;
        string path = Path.Combine(_bundleDirectory, relative);
        return File.Exists(path) ? path : null;
    }

    private string RelativePathOf(ScriptFile script)
    {
        string root = Path.GetFullPath(Directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(script.FullPath);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Script path is outside the library directory.", nameof(script));
        }

        return Path.GetRelativePath(Directory, full);
    }

    private void RefreshConflicts()
    {
        _updateConflicts.Clear();
        if (_bundleDirectory is null || !System.IO.Directory.Exists(_bundleDirectory)) return;
        foreach (string bundled in System.IO.Directory.EnumerateFiles(_bundleDirectory, "*.ps1", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(_bundleDirectory, bundled);
            string current = Path.Combine(Directory, relative);
            if (File.Exists(current) && HashOf(current) != HashOf(bundled))
            {
                _updateConflicts.Add(new ScriptUpdateConflict(relative, HashOf(current), HashOf(bundled)));
            }
        }
    }

    private static string HashOf(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private Dictionary<string, string> LoadBundleManifest()
    {
        string path = Path.Combine(Directory, BundleManifestName);
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            Dictionary<string, string>? values =
                System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            return values is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new(values, StringComparer.OrdinalIgnoreCase);
        }
        catch (System.Text.Json.JsonException)
        {
            // A corrupt manifest grants no overwrite authority; existing scripts stay untouched.
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveBundleManifest(Dictionary<string, string> hashes)
    {
        string path = Path.Combine(Directory, BundleManifestName);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(hashes));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void ReplaceAtomically(string source, string target)
    {
        string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temp, overwrite: true);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void WriteTextAtomically(string target, string content)
    {
        string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, content);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static ScriptOperationRisk RiskOf(string relative)
    {
        if (relative.StartsWith("Reboot" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return ScriptOperationRisk.Reboot;
        string[] readOnly =
        [
            Path.Combine("Info", "Free disk space.ps1"),
            Path.Combine("Info", "Installed software (search).ps1"),
            Path.Combine("Info", "Logged-on user.ps1"),
            Path.Combine("Info", "OS version and build.ps1"),
            Path.Combine("Info", "Uptime and last boot.ps1"),
            Path.Combine("Info", "Why is a reboot pending.ps1"),
            Path.Combine("SCCM Client", "CM agent status.ps1"),
            Path.Combine("SCCM Client", "Get cache size.ps1"),
            Path.Combine("SCCM Inventory & Updates", "Count missing updates (SCCM).ps1"),
            Path.Combine("Windows Update", "Count missing updates.ps1")
        ];
        return readOnly.Contains(relative, StringComparer.OrdinalIgnoreCase)
            ? ScriptOperationRisk.ReadOnly
            : ScriptOperationRisk.ExclusiveMutation;
    }
}
