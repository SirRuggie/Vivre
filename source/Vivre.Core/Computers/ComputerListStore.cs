using Vivre.Core.IO;
using System.Security.Cryptography;

namespace Vivre.Core.Computers;

/// <summary>
/// File-system implementation of <see cref="IComputerListStore"/>: each named list is
/// a <c>.ps1</c>-free plain <c>&lt;name&gt;.txt</c> in a folder (default
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
        [.. System.IO.Directory
            .EnumerateFiles(Directory, "*.txt")
            .Select(Path.GetFileNameWithoutExtension)
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
            File.Delete(path);
        }
    }

    public bool Exists(string name) => File.Exists(PathFor(name));

    private string PathFor(string name)
    {
        // Legacy enumerated stems (including trailing dots) remain readable/deletable;
        // strict new-name rules apply only when saving. Never accept a path or traversal.
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c))) throw new ArgumentException("Invalid list name.", nameof(name));
        return Path.Combine(Directory, name.Trim() + ".txt");
    }

    public static string? NameError(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a list name.";
        string value = name.Trim();
        if (value.Length > 240) return "Use a list name of 240 characters or fewer.";
        if (value.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)))
            return "List names cannot contain < > : \" / \\ | ? * or control characters.";
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
