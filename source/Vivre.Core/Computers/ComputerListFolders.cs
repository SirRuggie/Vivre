using System.Text.Json;
using Vivre.Core.IO;

namespace Vivre.Core.Computers;

/// <summary>Bookmark-style organization for saved lists. The list files and their names remain flat;
/// only their display folder is stored in a small adjacent JSON file.</summary>
public sealed class ComputerListFolders(IComputerListStore lists)
{
    private readonly string _path = Path.Combine(lists.Directory, "list-folders.json");

    public ListFolderSnapshot Snapshot(IReadOnlyList<string> availableLists)
    {
        FolderDocument document = Read();
        var available = availableLists.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assignments = document.Lists
            .Where(entry => available.Contains(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        return new ListFolderSnapshot(document.Folders.ToArray(), assignments);
    }

    public void Create(string name)
    {
        name = ValidName(name);
        Change(document =>
        {
            if (document.Folders.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new IOException("A folder already uses that name.");
            document.Folders.Add(name);
        });
    }

    public void Rename(string oldName, string newName)
    {
        newName = ValidName(newName);
        Change(document =>
        {
            int index = document.Folders.FindIndex(folder => folder.Equals(oldName, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new IOException("That folder no longer exists. Reopen Lists and try again.");
            if (document.Folders.Where((_, i) => i != index).Contains(newName, StringComparer.OrdinalIgnoreCase))
                throw new IOException("A folder already uses that name.");
            string previous = document.Folders[index];
            document.Folders[index] = newName;
            foreach (string list in document.Lists.Keys.ToArray())
                if (document.Lists[list].Equals(previous, StringComparison.OrdinalIgnoreCase)) document.Lists[list] = newName;
        });
    }

    /// <summary>Removing a folder only moves its list labels back to the top level.</summary>
    public void Remove(string name)
    {
        Change(document =>
        {
            if (document.Folders.RemoveAll(folder => folder.Equals(name, StringComparison.OrdinalIgnoreCase)) == 0)
                throw new IOException("That folder no longer exists. Reopen Lists and try again.");
            foreach (string list in document.Lists.Keys.ToArray())
                if (document.Lists[list].Equals(name, StringComparison.OrdinalIgnoreCase)) document.Lists.Remove(list);
        });
    }

    public void Move(string listName, string? folder)
    {
        Change(document =>
        {
            if (!lists.Exists(listName)) throw new IOException("That saved list no longer exists. Reopen Lists and try again.");
            if (folder is null)
            {
                document.Lists.Remove(listName);
                return;
            }
            string? actual = document.Folders.FirstOrDefault(item => item.Equals(folder, StringComparison.OrdinalIgnoreCase));
            if (actual is null) throw new IOException("That folder no longer exists. Reopen Lists and try again.");
            document.Lists[listName] = actual;
        });
    }

    private void Change(Action<FolderDocument> edit)
    {
        using FileStream lease = ExclusiveFileLease.Acquire(_path + ".lock", TimeSpan.Zero);
        FolderDocument document = Read();
        edit(document);
        AtomicFileWriter.Write(_path, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
    }

    private FolderDocument Read()
    {
        if (!File.Exists(_path)) return new FolderDocument();
        if ((File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The list-folder file cannot be a linked file.");
        if (new FileInfo(_path).Length > 4 * 1024 * 1024)
            throw new InvalidDataException("The list-folder file is too large to read safely.");
        FolderDocument document = JsonSerializer.Deserialize<FolderDocument>(File.ReadAllText(_path))
            ?? throw new InvalidDataException("The list-folder file is empty or invalid.");
        if (document.Folders is null || document.Lists is null)
            throw new InvalidDataException("The list-folder file is missing required fields.");
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in document.Folders)
            if (ComputerListStore.NameError(folder) is not null || folder.Length > 80
                || folder != folder.Trim() || !folders.Add(folder))
                throw new InvalidDataException("The list-folder file contains an invalid or duplicate folder.");
        var assignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in document.Lists)
            if (string.IsNullOrWhiteSpace(entry.Key) || entry.Value is null || !folders.Contains(entry.Value)
                || !assignments.TryAdd(entry.Key, entry.Value))
                throw new InvalidDataException("The list-folder file contains an invalid or duplicate assignment.");
        document.Lists = assignments;
        return document;
    }

    private static string ValidName(string name)
    {
        string? error = ComputerListStore.NameError(name);
        if (error is not null) throw new ArgumentException(error, nameof(name));
        if (name.Trim().Length > 80) throw new ArgumentException("Use a folder name of 80 characters or fewer.", nameof(name));
        return name.Trim();
    }

    private sealed class FolderDocument
    {
        public List<string> Folders { get; set; } = [];
        public Dictionary<string, string> Lists { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record ListFolderSnapshot(IReadOnlyList<string> Folders, IReadOnlyDictionary<string, string> Assignments)
{
    public string? FolderFor(string list) => Assignments.TryGetValue(list, out string? folder) ? folder : null;
}
