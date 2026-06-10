using System.Collections.ObjectModel;
using Vivre.Core.Scripts;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Vivre.Desktop.ViewModels;

/// <summary>
/// View model for the Scripts nav section — a library manager (edit / add / delete).
/// No run, no targets, no output. Running scripts against machines stays in
/// <see cref="ScriptRunnerWindow"/> (opened from the Computers grid right-click menu).
/// </summary>
public partial class ScriptsViewModel : ObservableObject
{
    private readonly IScriptLibrary _library;

    public ScriptsViewModel(IScriptLibrary library)
    {
        _library = library;
        ReloadScripts();
    }

    // ── Script list ────────────────────────────────────────────────────────

    /// <summary>All scripts in the library, bound to the grouped ListView.</summary>
    public ObservableCollection<ScriptFile> Scripts { get; } = [];

    /// <summary>Existing category folders offered in the Save category combo.</summary>
    public ObservableCollection<string> Categories { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelectedScript))]
    public partial ScriptFile? SelectedScript { get; set; }

    /// <summary>True when a user-created script is selected (built-ins can't be deleted).</summary>
    public bool CanDeleteSelectedScript =>
        SelectedScript is { } script && !_library.IsDefault(script);

    // ── Save fields ────────────────────────────────────────────────────────

    /// <summary>Name of the script being edited (for the Name box).</summary>
    [ObservableProperty]
    public partial string ScriptName { get; set; } = string.Empty;

    /// <summary>Category/folder to save into.</summary>
    [ObservableProperty]
    public partial string SaveCategory { get; set; } = "My Scripts";

    // ── Operations ─────────────────────────────────────────────────────────

    /// <summary>Reads a script's content from the library.</summary>
    public string LoadScript(ScriptFile script) => _library.Load(script);

    /// <summary>
    /// Saves the editor content into the library under the given name/category.
    /// Returns the saved <see cref="ScriptFile"/> so the caller can re-select it.
    /// </summary>
    public ScriptFile SaveScript(string name, string content)
    {
        string? category = string.IsNullOrWhiteSpace(SaveCategory) ? null : SaveCategory;
        ScriptFile saved = _library.Save(name, content, category);
        ReloadScripts();
        SelectedScript = Scripts.FirstOrDefault(s => s.FullPath == saved.FullPath);
        return saved;
    }

    /// <summary>
    /// Deletes the currently selected user script. Throws
    /// <see cref="InvalidOperationException"/> if the script is a built-in default — the
    /// Delete button should already be disabled in that case, but this guards the code path.
    /// </summary>
    public void DeleteSelectedScript()
    {
        if (SelectedScript is not { } script)
        {
            return;
        }

        // Delegate to the library — it throws InvalidOperationException for defaults.
        _library.Delete(script);

        SelectedScript = null;
        ScriptName = string.Empty;
        ReloadScripts();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    public void ReloadScripts()
    {
        Scripts.Clear();
        foreach (ScriptFile f in _library.List())
        {
            Scripts.Add(f);
        }

        Categories.Clear();
        foreach (string cat in Scripts
                     .Select(s => s.Category)
                     .Where(c => !string.IsNullOrEmpty(c))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            Categories.Add(cat);
        }
    }
}
