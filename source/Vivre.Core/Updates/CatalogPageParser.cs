using System.Globalization;
using HtmlAgilityPack;

namespace Vivre.Core.Updates;

/// <summary>One result row scraped from a Microsoft Update Catalog search page: its update title (for
/// architecture/SKU matching) and the package size in raw bytes.</summary>
public sealed record CatalogRow(string Title, long SizeBytes);

/// <summary>
/// Pure HTML parsing for the Microsoft Update Catalog <c>Search.aspx</c> result page — separated from the HTTP
/// service so it can be unit-tested against a saved sample of the page. Mirrors the approach of the open-source
/// Poushec/Poushec.UpdateCatalogParser library (which reads the same <c>_originalSize</c> spans).
/// </summary>
public static class CatalogPageParser
{
    /// <summary>
    /// Extracts every result row's title + size (raw bytes). The catalog renders, per row, a hidden span
    /// carrying the size in RAW BYTES:
    /// <code>&lt;span class="noDisplay" id="&lt;guid&gt;_originalSize"&gt;2553500647&lt;/span&gt;</code>
    /// The sibling <c>&lt;guid&gt;_size</c> span holds the pre-formatted "2435.2 MB" text — we IGNORE that and
    /// parse the exact bytes from <c>_originalSize</c>. Rows with no parseable byte count are skipped. Returns an
    /// empty list when the page has no result rows (e.g. "no updates found").
    /// </summary>
    public static IReadOnlyList<CatalogRow> ParseRows(string? html)
    {
        var rows = new List<CatalogRow>();
        if (string.IsNullOrWhiteSpace(html))
        {
            return rows;
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        HtmlNodeCollection? sizeSpans = doc.DocumentNode.SelectNodes("//span[contains(@id,'_originalSize')]");
        if (sizeSpans is null)
        {
            return rows;
        }

        foreach (HtmlNode span in sizeSpans)
        {
            string raw = HtmlEntity.DeEntitize(span.InnerText)?.Trim() ?? string.Empty;
            if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes) || bytes <= 0)
            {
                continue;
            }

            // Pair the size to its row's update title for architecture matching: walk up to the containing
            // <tr>, then read the row's update link (its id ends "_link"). Robust to the row id/cell layout.
            HtmlNode? row = span.Ancestors("tr").FirstOrDefault();
            rows.Add(new CatalogRow(ExtractRowTitle(row), bytes));
        }

        return rows;
    }

    /// <summary>
    /// Picks the size to use from the parsed rows. Prefers rows whose title matches the requested architecture
    /// ("x64" / "arm64" / "x86"); if none match (or <paramref name="arch"/> is null) it considers all rows. Within
    /// the candidate set it returns the LARGEST size — the full OS package, never a small companion file (e.g. a
    /// SafeOS / dynamic-update component sometimes listed alongside the CU). Returns null when there are no rows.
    /// The row-matching heuristic is intentionally simple so it can be refined later.
    /// </summary>
    public static long? SelectSizeBytes(IReadOnlyList<CatalogRow> rows, string? arch)
    {
        if (rows is null || rows.Count == 0)
        {
            return null;
        }

        IEnumerable<CatalogRow> candidates = rows;
        if (!string.IsNullOrWhiteSpace(arch))
        {
            List<CatalogRow> matched = rows
                .Where(r => r.Title.Contains(arch, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matched.Count > 0)
            {
                candidates = matched;
            }
        }

        return candidates.Max(r => r.SizeBytes);
    }

    private static string ExtractRowTitle(HtmlNode? row)
    {
        if (row is null)
        {
            return string.Empty;
        }

        HtmlNode? link = row.SelectSingleNode(".//a[contains(@id,'_link')]")
                         ?? row.SelectSingleNode(".//a");
        string? inner = link?.InnerText;
        return string.IsNullOrEmpty(inner) ? string.Empty : HtmlEntity.DeEntitize(inner).Trim();
    }
}
