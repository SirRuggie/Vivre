namespace Vivre.Core.Scripts;

/// <summary>
/// A folder of reusable PowerShell scripts the user can pick, edit, and run against
/// selected machines (the Run Script feature).
/// </summary>
public interface IScriptLibrary
{
    /// <summary>Raised after scripts or built-in update/conflict state changes.</summary>
    event EventHandler? LibraryChanged;

    /// <summary>Absolute path of the folder backing the library.</summary>
    string Directory { get; }

    /// <summary>Saved scripts, ordered by name.</summary>
    IReadOnlyList<ScriptFile> List();

    /// <summary>One consistent scripts/categories/conflicts projection for every UI surface.</summary>
    ScriptLibrarySnapshot Snapshot();

    /// <summary>Reads a script's contents.</summary>
    string Load(ScriptFile script);

    /// <summary>Writes <paramref name="content"/> to <c>&lt;name&gt;.ps1</c> at the library root and returns the saved file.</summary>
    ScriptFile Save(string name, string content);

    /// <summary>
    /// Writes <paramref name="content"/> to <c>&lt;category&gt;\&lt;name&gt;.ps1</c>, creating the
    /// category folder if needed (null/blank category = the library root). Returns the saved file.
    /// </summary>
    ScriptFile Save(string name, string content, string? category);

    /// <summary>True if the script is one of the built-in defaults shipped with the app (not user-created).</summary>
    bool IsDefault(ScriptFile script);

    /// <summary>Hash-verified identity and conservative execution policy for a script.</summary>
    ScriptPolicy GetPolicy(ScriptFile script);

    /// <summary>Classifies the exact editor content that will execute, preventing file/editor policy races.</summary>
    ScriptPolicy GetPolicy(ScriptFile script, string executionContent);

    /// <summary>User-edited files that occupy a bundled path and were therefore preserved.</summary>
    IReadOnlyList<ScriptUpdateConflict> UpdateConflicts { get; }

    /// <summary>Replaces a built-in-path script with the current shipped copy.</summary>
    void ResetToDefault(ScriptFile script);

    /// <summary>Deletes a user-created script. Throws <see cref="InvalidOperationException"/> for a built-in default.</summary>
    void Delete(ScriptFile script);
}

public enum ScriptOperationRisk
{
    ReadOnly,
    ExclusiveMutation,
    Reboot
}

public sealed record ScriptPolicy(bool IsVerifiedBuiltIn, bool CanDelete, bool CanReset, ScriptOperationRisk OperationRisk);

public sealed record ScriptUpdateConflict(string RelativePath, string CurrentHash, string BundledHash);

public sealed record ScriptLibrarySnapshot(
    IReadOnlyList<ScriptFile> Scripts,
    IReadOnlyList<string> Categories,
    IReadOnlyList<ScriptUpdateConflict> Conflicts);
