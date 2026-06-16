namespace Vivre.Core.Updates;

/// <summary>
/// Looks up an update's published package size (raw bytes) from the Microsoft Update Catalog, so the grid can
/// show the real download size instead of WUA's inflated worst-case aggregate. The lookup is cached per KB for
/// the session; failures return null ("unavailable") rather than throwing, so the caller falls back to a
/// WUA-definite size or a dash via <see cref="UpdateSizeResolver"/>.
/// </summary>
public interface ICatalogSizeService
{
    /// <summary>
    /// Returns the catalog package size in bytes for the given KB, or null when the catalog can't answer
    /// (offline, KB not found, row miss, parse failure, timeout). Never throws.
    /// </summary>
    /// <param name="kb">KB id in any form ("KB5094125" / "5094125"); null/blank yields null.</param>
    /// <param name="arch">Target architecture hint ("x64" / "arm64" / "x86") used to pick the matching catalog
    /// row; null falls back to the largest row.</param>
    Task<long?> GetSizeBytesAsync(string? kb, string? arch, CancellationToken cancellationToken = default);
}
