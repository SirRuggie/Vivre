namespace Vivre.Core.Updates;

/// <summary>
/// One applicable update returned by a WUA scan. The grid shows the count + a
/// summary; the per-host detail view (Phase 2) lists these rows.
/// </summary>
/// <param name="Title">The update's display title (e.g. "2024-05 Cumulative Update for Windows Server …").</param>
/// <param name="ArticleId">The KB article id without the "KB" prefix (e.g. "5037782"); null if the update has none.</param>
/// <param name="IsDownloaded">True if WUA already has the payload cached locally.</param>
/// <param name="SizeMb">Approximate download size in MB (0 when unknown).</param>
public sealed record SoftwareUpdate(
    string Title,
    string? ArticleId,
    bool IsDownloaded,
    double SizeMb);
