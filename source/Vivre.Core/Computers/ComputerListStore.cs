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

        return [.. File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)];
    }

    public void Save(string name, IEnumerable<string> machines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        IEnumerable<string> cleaned = machines
            .Select(m => m.Trim())
            .Where(m => m.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        File.WriteAllLines(PathFor(name), cleaned);
    }

    public void Delete(string name)
    {
        string path = PathFor(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public bool Exists(string name) => File.Exists(PathFor(name));

    private string PathFor(string name)
    {
        string safe = string.Join("_", name.Trim().Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(Directory, safe + ".txt");
    }

    private static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vivre", "Lists");
}
