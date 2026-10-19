namespace Vivre.Core.Computers;

/// <summary>
/// A library of named machine lists the user builds and reuses (e.g. ServerList,
/// UpdateList, BreachList). Mirrors the script library: each list is a plain text
/// file (one machine per line) in a folder, so lists are also editable / backup-able
/// outside the app. There is no auto-loaded list — the app opens with an empty grid.
/// </summary>
public interface IComputerListStore
{
    /// <summary>Absolute path of the folder backing the lists.</summary>
    string Directory { get; }

    /// <summary>Names of the saved lists, ordered.</summary>
    IReadOnlyList<string> List();

    /// <summary>Machine names in a saved list (empty if it doesn't exist).</summary>
    IReadOnlyList<string> Load(string name);

    /// <summary>Reads an existing list; throws if it disappeared instead of treating it as empty.</summary>
    IReadOnlyList<string> LoadRequired(string name);

    /// <summary>Fingerprint for overwrite approval, or null when the file is absent.</summary>
    string? GetRevision(string name);

    /// <summary>Writes machines to the named list; replacing an existing list requires explicit overwrite approval.</summary>
    void Save(string name, IEnumerable<string> machines, bool overwrite = false, string? expectedRevision = null);

    /// <summary>Moves the named list to local recovery storage if present.</summary>
    void Delete(string name, string? expectedRevision);

    /// <summary>Recoverable deletions, newest first.</summary>
    IReadOnlyList<DeletedComputerList> DeletedLists();

    /// <summary>Restores a deletion under a valid unused name; never overwrites.</summary>
    void Restore(string id, string name);

    /// <summary>True if a list with that name exists.</summary>
    bool Exists(string name);
}

public sealed record DeletedComputerList(string Id, string Name, DateTime DeletedAtUtc);
