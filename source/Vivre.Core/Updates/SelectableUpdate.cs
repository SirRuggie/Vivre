using CommunityToolkit.Mvvm.ComponentModel;

namespace Vivre.Core.Updates;

/// <summary>
/// One scanned update plus the user's keep/skip choice, for the Windows Update view's
/// per-machine checklist. Wraps a <see cref="SoftwareUpdate"/> and adds an observable
/// <see cref="IsSelected"/> that the checklist's checkbox binds to (default ticked).
/// </summary>
public partial class SelectableUpdate : ObservableObject
{
    public SelectableUpdate(SoftwareUpdate update, bool isSelected = true)
    {
        Update = update;
        IsSelected = isSelected;
    }

    /// <summary>The underlying scanned update.</summary>
    public SoftwareUpdate Update { get; }

    /// <summary>Whether this update is ticked for install.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>KB article id (e.g. "5037782"), null if the update has none.</summary>
    public string? Kb => Update.ArticleId;

    /// <summary>Update display title.</summary>
    public string Title => Update.Title;

    /// <summary>Download size in MB.</summary>
    public double SizeMb => Update.SizeMb;

    /// <summary>Whether the target has already downloaded this update.</summary>
    public bool IsDownloaded => Update.IsDownloaded;
}
