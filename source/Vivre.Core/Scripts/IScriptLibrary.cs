namespace Vivre.Core.Scripts;

/// <summary>
/// A folder of reusable PowerShell scripts the user can pick, edit, and run against
/// selected machines (the Run Script feature).
/// </summary>
public interface IScriptLibrary
{
    /// <summary>Absolute path of the folder backing the library.</summary>
    string Directory { get; }

    /// <summary>Saved scripts, ordered by name.</summary>
    IReadOnlyList<ScriptFile> List();

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

    /// <summary>Deletes a user-created script. Throws <see cref="InvalidOperationException"/> for a built-in default.</summary>
    void Delete(ScriptFile script);
}
