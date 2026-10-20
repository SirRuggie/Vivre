using Vivre.Core.IO;
using System.Security.Cryptography;

namespace Vivre.Core.Computers;

/// <summary>
/// File-system implementation of <see cref="IComputerListStore"/>: each named list is
/// a plain <c>&lt;name&gt;.txt</c> (or reversibly escaped <c>.vlist</c>) in a folder (default
/// <c>%APPDATA%\Vivre\Lists</c>), one machine per line. The folder is created on
/// first use; it is NOT seeded — machine lists are the user's own.
/// </summary>
public sealed class ComputerListStore : IComputerListStore
{
    public ComputerListStore() : this(DefaultDirectory())
    {
    }

    public ComputerListStore(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
    }

    public string Directory { get; }

    public IReadOnlyList<string> List() =>
        [.. ListFiles(Directory)
            .Select(DisplayName)
            .OfType<string>()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyList<string> Load(string name)
    {
        string path = PathFor(name);
        if (!File.Exists(path))
        {
            return [];
        }

        return LoadRequired(name);
    }

    public IReadOnlyList<string> LoadRequired(string name) => [.. File.ReadAllLines(PathFor(name))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)];

    public string? GetRevision(string name)
    {
        try { return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(PathFor(name)))); }
        catch (FileNotFoundException) { return null; }
    }

    public void Save(string name, IEnumerable<string> machines, bool overwrite = false, string? expectedRevision = null)
    {
        string? error = NameError(name);
        if (error is not null) throw new ArgumentException(error, nameof(name));
        string path = PathFor(name);
        using FileStream lease = ExclusiveFileLease.Acquire(path + ".lock", TimeSpan.Zero);
        if (overwrite && (expectedRevision is null || GetRevision(name) != expectedRevision))
            throw new IOException("The saved list changed or was removed since overwrite approval. Review it and save again.");

        IEnumerable<string> cleaned = machines
            .Select(m => m.Trim())
            .Where(m => m.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        // Same one-machine-per-line format File.WriteAllLines produced (every line newline-terminated),
        // but written crash-safe via the atomic temp-file swap so a crash mid-write can't corrupt the list.
        AtomicFileWriter.Write(path, string.Concat(cleaned.Select(m => m + Environment.NewLine)), overwrite);
    }

    public void Delete(string name, string? expectedRevision)
    {
        string path = PathFor(name);
        using FileStream lease = ExclusiveFileLease.Acquire(path + ".lock", TimeSpan.Zero);
        if (GetRevision(name) != expectedRevision)
            throw new IOException("The saved list changed since delete approval. Review it before deleting.");
        if (File.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Cannot delete a linked list file.");
            string root = RecoveryRoot();
            System.IO.Directory.CreateDirectory(root);
            string folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(folder);
            File.Move(path, Path.Combine(folder, Path.GetFileName(path))); // same-volume, no overwrite

        }
    }

    public IReadOnlyList<DeletedComputerList> DeletedLists()
    {
        string root = RecoveryRoot();
        if (!System.IO.Directory.Exists(root)) return [];
        var result = new List<DeletedComputerList>();
        foreach (string folder in System.IO.Directory.EnumerateDirectories(root))
        {
            string id = Path.GetFileName(folder);
            if (!Guid.TryParseExact(id, "N", out _) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
            string[] files = ListFiles(folder).ToArray();
            if (files.Length == 1 && (File.GetAttributes(files[0]) & FileAttributes.ReparsePoint) == 0) result.Add(new(id, DisplayName(files[0]), System.IO.Directory.GetCreationTimeUtc(folder)));
        }
        return result.OrderByDescending(item => item.DeletedAtUtc).ToArray();
    }

    public void Restore(string id, string name)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid deleted-list identifier.", nameof(id));
        string? error = NameError(name);
        if (error is not null) throw new ArgumentException(error, nameof(name));
        string root = RecoveryRoot();
        using FileStream archiveLease = ExclusiveFileLease.Acquire(Path.Combine(root, id + ".lock"), TimeSpan.Zero);
        string folder = Path.Combine(root, id);
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new IOException("Cannot restore a linked recovery folder.");
        string[] files = ListFiles(folder).ToArray();
        if (files.Length != 1) throw new IOException("That deleted list is no longer available.");
        if ((File.GetAttributes(files[0]) & FileAttributes.ReparsePoint) != 0) throw new IOException("Cannot restore a linked list file.");
        string destination = PathFor(name);
        using FileStream targetLease = ExclusiveFileLease.Acquire(destination + ".lock", TimeSpan.Zero);
        if (File.Exists(destination)) throw new IOException("A saved list already uses that name. Choose a different name to restore; nothing was overwritten.");
        File.Move(files[0], destination); // fail if another writer created the destination
        try { System.IO.Directory.Delete(folder); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string RecoveryRoot()
    {
        string root = Path.Combine(Directory, "Deleted");
        if (System.IO.Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The list recovery folder cannot be a linked folder.");
        return root;
    }

    public bool Exists(string name) => File.Exists(PathFor(name));

    private string PathFor(string name)
    {
        // Legacy enumerated stems (including trailing dots) remain readable/deletable;
        // strict new-name rules apply only when saving. Separators are encoded, never treated as paths.
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Any(c => c < 32)) throw new ArgumentException("Invalid list name.", nameof(name));
        string value = name.Trim();
        return Path.Combine(Directory, StorageName(value));
    }

    // A separate extension distinguishes reversible display names from legacy literal .txt stems.
    // Contents remain one machine per line. Encode '%' too so A/B and A%2FB never alias.
    private static string StorageName(string value)
    {
        if (!value.Any(c => "<>:\"/\\|?*".Contains(c))) return value + ".txt";
        return string.Concat(value.Select(c => c == '%' || "<>:\"/\\|?*".Contains(c)
            ? "%" + ((int)c).ToString("X2") : c.ToString())) + ".vlist";
    }

    private static string DisplayName(string path) =>
        Path.GetExtension(path).Equals(".vlist", StringComparison.OrdinalIgnoreCase)
            ? Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(path))
            : Path.GetFileNameWithoutExtension(path);

    private static IEnumerable<string> ListFiles(string folder) =>
        System.IO.Directory.EnumerateFiles(folder).Where(path =>
            Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(path).Equals(".vlist", StringComparison.OrdinalIgnoreCase));

    public static string? NameError(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a list name.";
        string value = name.Trim();
        if (value.Length > 240) return "Use a list name of 240 characters or fewer.";
        if (value.Any(c => c < 32)) return "List names cannot contain control characters.";
        if (StorageName(value).Length > 245) return "This list name is too long to store; use a shorter name.";
        if (value.EndsWith('.')) return "A list name cannot end with a dot.";
        string stem = value.Split('.')[0].TrimEnd().ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$"
            || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3])))
            return "That name is reserved by Windows; choose another list name.";
        return null;
    }

    private static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vivre", "Lists");
}
