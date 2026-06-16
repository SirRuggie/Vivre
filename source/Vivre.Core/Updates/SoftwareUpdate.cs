namespace Vivre.Core.Updates;

/// <summary>
/// One applicable update returned by a WUA scan. The grid shows the count + a
/// summary; the per-host detail view (Phase 2) lists these rows.
/// </summary>
/// <param name="Title">The update's display title (e.g. "2024-05 Cumulative Update for Windows Server …").</param>
/// <param name="ArticleId">The KB article id without the "KB" prefix (e.g. "5037782"); null if the update has none.</param>
/// <param name="IsDownloaded">True if WUA already has the payload cached locally.</param>
/// <param name="MinDownloadSizeBytes">WUA's <c>IUpdate.MinDownloadSize</c> in RAW BYTES (0 when unknown). Used as
/// the display size only when <see cref="MaxDownloadSizeBytes"/> is 0 (some updates populate just Min). See
/// <see cref="UpdateSizeResolver.ResolveDisplaySize"/> for the tier order.</param>
/// <param name="MaxDownloadSizeBytes">WUA's <c>IUpdate.MaxDownloadSize</c> in RAW BYTES (0 when unknown). The
/// PRIMARY display size for the vast majority of updates (realistic, already in the scan result). The exception
/// is an express/checkpoint CU, where this is WUA's worst-case differential AGGREGATE — wildly inflated (e.g.
/// ~21.9 GB for a ~2.4 GB package): when it exceeds 10 GB the grid substitutes the Microsoft Update Catalog size
/// instead. See <see cref="UpdateSizeResolver"/>.</param>
/// <param name="IsUninstallable">Whether Windows reports the update as removable — the Installed
/// scan sets this purely from WUA's <c>IsUninstallable</c> property (the DISM <c>Package_for_KB</c>
/// presence test was dropped because it over-promised on permanent SSU/cumulative updates). A false
/// value means Windows reports it as non-removable; the agent may still attempt DISM as a runtime
/// fallback. Only meaningful for <see cref="UpdateScope.Installed"/> scans; the Applicable scan
/// emits <c>true</c> so the checklist's checkboxes stay enabled for install.</param>
/// <param name="InstalledAt">When this update was installed (best-effort, from WUA's history) —
/// populated only for <see cref="UpdateScope.Installed"/> scans; null for Applicable rows and for
/// installed updates whose history entry can't be matched.</param>
public sealed record SoftwareUpdate(
    string Title,
    string? ArticleId,
    bool IsDownloaded,
    long MinDownloadSizeBytes,
    long MaxDownloadSizeBytes,
    bool IsUninstallable = true,
    DateTime? InstalledAt = null);
