namespace Vivre.Core.Updates;

/// <summary>
/// One applicable update returned by a WUA scan. The grid shows the count + a
/// summary; the per-host detail view (Phase 2) lists these rows.
/// </summary>
/// <param name="Title">The update's display title (e.g. "2024-05 Cumulative Update for Windows Server …").</param>
/// <param name="ArticleId">The KB article id without the "KB" prefix (e.g. "5037782"); null if the update has none.</param>
/// <param name="IsDownloaded">True if WUA already has the payload cached locally.</param>
/// <param name="SizeMb">Approximate download size in MB (0 when unknown).</param>
/// <param name="IsUninstallable">Whether the update can be uninstalled — only meaningful for
/// <see cref="UpdateScope.Installed"/> scans. The Applicable scan emits <c>true</c> so the
/// checklist's checkboxes stay enabled for install.</param>
/// <param name="InstalledAt">When this update was installed (best-effort, from WUA's history) —
/// populated only for <see cref="UpdateScope.Installed"/> scans; null for Applicable rows and for
/// installed updates whose history entry can't be matched.</param>
public sealed record SoftwareUpdate(
    string Title,
    string? ArticleId,
    bool IsDownloaded,
    double SizeMb,
    bool IsUninstallable = true,
    DateTime? InstalledAt = null);
