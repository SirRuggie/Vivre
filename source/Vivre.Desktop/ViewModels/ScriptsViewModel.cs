using System.Collections.ObjectModel;
using System.IO;
using Vivre.Core.Scripts;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Vivre.Desktop.ViewModels;

/// <summary>
/// View model for the Scripts nav section — a library manager (edit / add / delete).
/// No run, no targets, no output. Running scripts against machines stays in
/// <see cref="ScriptRunnerWindow"/> (opened from the Computers grid right-click menu).
/// </summary>
public partial class ScriptsViewModel : ObservableObject, IDisposable
{
    private readonly IScriptLibrary _library;
    public event EventHandler? LibraryRefreshed;

    public ScriptsViewModel(IScriptLibrary library)
    {
        _library = library;
        ReloadScripts();
        _library.LibraryChanged += OnLibraryChanged;
    }

    // ── Script list ────────────────────────────────────────────────────────

    /// <summary>All scripts in the library, bound to the grouped ListView.</summary>
    public ObservableCollection<ScriptFile> Scripts { get; } = [];

    /// <summary>Existing category folders offered in the Save category combo.</summary>
    public ObservableCollection<string> Categories { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelectedScript))]
    [NotifyPropertyChangedFor(nameof(CanResetSelectedScript))]
    [NotifyPropertyChangedFor(nameof(SelectedScriptNotice))]
    public partial ScriptFile? SelectedScript { get; set; }

    /// <summary>True when a user-created script is selected (built-ins can't be deleted).</summary>
    public bool CanDeleteSelectedScript =>
        SelectedScript is { } script && !_library.IsDefault(script);

    public bool CanResetSelectedScript =>
        SelectedScript is { } script && _library.GetPolicy(script).CanReset;

    public string SelectedScriptNotice
    {
        get
        {
            if (SelectedScript is not { } script) return string.Empty;
            ScriptPolicy policy = _library.GetPolicy(script);
            return policy.CanReset && !policy.IsVerifiedBuiltIn
                ? "Your saved copy differs from the current built-in. Reset it to use the shipped version."
                : string.Empty;
        }
    }

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
    public ScriptFile SaveScript(string name, string content, string? expectedRevision = null, string? categoryOverride = null)
    {
        string? category = categoryOverride ?? SaveCategory;
        if (string.IsNullOrWhiteSpace(category)) category = null;
        ScriptFile saved = _library.Save(name, content, category, expectedRevision);
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

        DeleteScript(script);
    }

    public void DeleteScript(ScriptFile script)
    {
        _library.Delete(script);

        if (SelectedScript?.FullPath == script.FullPath) SelectedScript = null;
        ScriptName = string.Empty;
        ReloadScripts();
    }

    public void ResetSelectedScript()
    {
        if (SelectedScript is not { } script) return;
        _library.ResetToDefault(script);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    public void ReloadScripts()
    {
        string? selectedPath = SelectedScript?.FullPath;
        ScriptLibrarySnapshot snapshot = _library.Snapshot();
        ScriptProjectionSync.Apply(Scripts, snapshot.Scripts);

        // An editable ComboBox writes back when its items change. Preserve typed folder text.
        string saveCategory = SaveCategory;
        ScriptProjectionSync.ApplyCategories(Categories, snapshot.Categories);
        SaveCategory = saveCategory;
        SelectedScript = selectedPath is null ? null : Scripts.FirstOrDefault(s =>
            string.Equals(Path.GetFullPath(s.FullPath), Path.GetFullPath(selectedPath), StringComparison.OrdinalIgnoreCase));
    }

    private void OnLibraryChanged(object? sender, EventArgs e)
    {
        ReloadScripts();
        OnPropertyChanged(nameof(CanResetSelectedScript));
        OnPropertyChanged(nameof(SelectedScriptNotice));
        LibraryRefreshed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _library.LibraryChanged -= OnLibraryChanged;
}
