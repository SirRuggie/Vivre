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

    /// <summary>
    /// True when this update was successfully installed during the current app session.
    /// Runtime-only — never persisted, never serialised. Reset when the applicable list is
    /// repopulated by a fresh scan (via <see cref="Reset"/>).
    /// Drives the "Installed — reboot pending" / "Installed" chip and the disabled/greyed row in the UI.
    /// </summary>
    [ObservableProperty]
    public partial bool InstalledThisSession { get; set; }

    /// <summary>
    /// Whether the install that set <see cref="InstalledThisSession"/> reported a reboot as required.
    /// Only meaningful when <see cref="InstalledThisSession"/> is true.
    /// Runtime-only — never persisted.
    /// </summary>
    [ObservableProperty]
    public partial bool InstalledThisSessionRebootPending { get; set; }

    /// <summary>Resets all session-install state. Called when a new scan repopulates the applicable list.</summary>
    public void Reset()
    {
        InstalledThisSession = false;
        InstalledThisSessionRebootPending = false;
    }

    /// <summary>KB article id (e.g. "5037782"), null if the update has none.</summary>
    public string? Kb => Update.ArticleId;

    /// <summary>Update display title.</summary>
    public string Title => Update.Title;

    /// <summary>Download size in MB.</summary>
    public double SizeMb => Update.SizeMb;

    /// <summary>Whether the target has already downloaded this update.</summary>
    public bool IsDownloaded => Update.IsDownloaded;

    /// <summary>Whether the update can be uninstalled (only meaningful for Installed-scope scans —
    /// the checklist's checkbox binds <c>IsEnabled</c> to this so non-uninstallable Windows
    /// updates are visibly greyed out).</summary>
    public bool IsUninstallable => Update.IsUninstallable;

    /// <summary>When the update was installed (only populated for Installed-scope scans). Bound by
    /// the checklist's "Installed" column for display and sort.</summary>
    public DateTime? InstalledAt => Update.InstalledAt;
}
