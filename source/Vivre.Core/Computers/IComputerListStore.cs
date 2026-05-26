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

    /// <summary>Writes <paramref name="machines"/> to the named list (one per line).</summary>
    void Save(string name, IEnumerable<string> machines);

    /// <summary>Deletes the named list if present.</summary>
    void Delete(string name);

    /// <summary>True if a list with that name exists.</summary>
    bool Exists(string name);
}
