using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// <see cref="CatalogPageParser"/> against a SAVED sample of the Microsoft Update Catalog <c>Search.aspx</c>
/// result page. Pins: the raw-bytes parse from the hidden <c>_originalSize</c> spans (NOT the formatted
/// <c>_size</c> text), largest-row selection, architecture matching, the &gt; 2 GB (long) path, and
/// "no rows → null".
/// </summary>
public class CatalogPageParserTests
{
    // A faithful (trimmed) sample of a KB5094125 search result: an x64 CU, an ARM64 CU (> 2 GB — locks the long
    // path), and a small x64 Dynamic Update companion. Each row carries the catalog's real shape: a "_link"
    // title anchor, a formatted "_size" span (to be IGNORED), and a hidden "noDisplay _originalSize" byte span.
    private const string SampleHtml = """
        <html><body>
        <table id="ctl00_catalogBody_updateMatches" class="resultsBorder">
          <tr id="headerRow"><td>Title</td><td>Products</td><td>Size</td></tr>
          <tr id="aaaa1111-2222-3333-4444-555555555555">
            <td><a id="aaaa1111-2222-3333-4444-555555555555_link" href="ScopedViewInline.aspx?updateid=aaaa1111">2026-06 Cumulative Update for Windows Server 2025 for x64-based Systems (KB5094125)</a></td>
            <td>Windows Server 2025</td>
            <td>
              <span id="aaaa1111-2222-3333-4444-555555555555_size">2435.2 MB</span>
              <span class="noDisplay" id="aaaa1111-2222-3333-4444-555555555555_originalSize">2553500647</span>
            </td>
          </tr>
          <tr id="bbbb1111-2222-3333-4444-555555555555">
            <td><a id="bbbb1111-2222-3333-4444-555555555555_link" href="ScopedViewInline.aspx?updateid=bbbb1111">2026-06 Cumulative Update for Windows Server 2025 for ARM64-based Systems (KB5094125)</a></td>
            <td>Windows Server 2025</td>
            <td>
              <span id="bbbb1111-2222-3333-4444-555555555555_size">2510.7 MB</span>
              <span class="noDisplay" id="bbbb1111-2222-3333-4444-555555555555_originalSize">2632876032</span>
            </td>
          </tr>
          <tr id="cccc1111-2222-3333-4444-555555555555">
            <td><a id="cccc1111-2222-3333-4444-555555555555_link" href="ScopedViewInline.aspx?updateid=cccc1111">2026-06 Dynamic Update for Windows Server 2025 for x64-based Systems (KB5094126)</a></td>
            <td>Windows Server 2025</td>
            <td>
              <span id="cccc1111-2222-3333-4444-555555555555_size">58.0 MB</span>
              <span class="noDisplay" id="cccc1111-2222-3333-4444-555555555555_originalSize">60817408</span>
            </td>
          </tr>
        </table>
        </body></html>
        """;

    // The catalog's "no results" page: no result rows, hence no _originalSize spans.
    private const string NoResultsHtml = """
        <html><body>
        <div id="ctl00_catalogBody_noSearchResults">We did not find any results for KB0000000.</div>
        </body></html>
        """;

    [Fact]
    public void ParseRows_reads_raw_bytes_from_originalSize_and_captures_titles()
    {
        IReadOnlyList<CatalogRow> rows = CatalogPageParser.ParseRows(SampleHtml);

        Assert.Equal(3, rows.Count);

        // Bytes come from _originalSize, not the formatted "_size" text. The ARM64 row is > 2 GB → long, not int.
        Assert.Equal(2553500647L, rows[0].SizeBytes);
        Assert.Equal(2632876032L, rows[1].SizeBytes);
        Assert.Equal(60817408L, rows[2].SizeBytes);

        Assert.Contains("x64-based", rows[0].Title);
        Assert.Contains("ARM64-based", rows[1].Title);
    }

    [Fact]
    public void SelectSizeBytes_prefers_the_matching_architecture_and_takes_the_largest()
    {
        IReadOnlyList<CatalogRow> rows = CatalogPageParser.ParseRows(SampleHtml);

        // x64 has two rows (the big CU + the small Dynamic Update companion); the full CU wins, not the companion.
        Assert.Equal(2553500647L, CatalogPageParser.SelectSizeBytes(rows, "x64"));
        Assert.Equal(2632876032L, CatalogPageParser.SelectSizeBytes(rows, "arm64"));
    }

    [Fact]
    public void SelectSizeBytes_falls_back_to_the_largest_row_when_arch_is_null_or_unmatched()
    {
        IReadOnlyList<CatalogRow> rows = CatalogPageParser.ParseRows(SampleHtml);

        // No arch → largest overall (the ARM64 row here); an unmatched arch behaves the same (no match → all rows).
        Assert.Equal(2632876032L, CatalogPageParser.SelectSizeBytes(rows, arch: null));
        Assert.Equal(2632876032L, CatalogPageParser.SelectSizeBytes(rows, "ia64"));
    }

    [Fact]
    public void ParseRows_returns_empty_and_SelectSizeBytes_returns_null_for_a_no_results_page()
    {
        IReadOnlyList<CatalogRow> rows = CatalogPageParser.ParseRows(NoResultsHtml);

        Assert.Empty(rows);
        Assert.Null(CatalogPageParser.SelectSizeBytes(rows, "x64"));
    }

    [Fact]
    public void ParseRows_is_empty_for_null_or_blank_html()
    {
        Assert.Empty(CatalogPageParser.ParseRows(null));
        Assert.Empty(CatalogPageParser.ParseRows("   "));
    }
}
