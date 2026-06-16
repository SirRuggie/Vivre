using System.Net;
using System.Net.Http;
using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// <see cref="MicrosoftUpdateCatalogService"/> over a stub HTTP handler (no real network): the per-KB cache hits
/// the catalog once, failures surface as null ("unavailable"), a blank KB short-circuits, and KB normalization.
/// </summary>
public class MicrosoftUpdateCatalogServiceTests
{
    private const string OneRowHtml = """
        <html><body><table id="ctl00_catalogBody_updateMatches">
          <tr id="aaaa-1111">
            <td><a id="aaaa-1111_link">2026-06 Cumulative Update for Windows Server 2025 for x64-based Systems (KB5094125)</a></td>
            <td><span id="aaaa-1111_size">2435.2 MB</span><span class="noDisplay" id="aaaa-1111_originalSize">2553500647</span></td>
          </tr>
        </table></body></html>
        """;

    [Fact]
    public async Task Returns_parsed_size_and_hits_the_catalog_once_per_kb()
    {
        var handler = new StubHandler(_ => Ok(OneRowHtml));
        using var http = new HttpClient(handler);
        var service = new MicrosoftUpdateCatalogService(http);

        long? first = await service.GetSizeBytesAsync("KB5094125", "x64");
        long? second = await service.GetSizeBytesAsync("5094125", "x64"); // normalizes to the same key

        Assert.Equal(2553500647L, first);
        Assert.Equal(2553500647L, second);
        Assert.Equal(1, handler.Calls); // one shared request — many boxes showing the KB don't each fire one
    }

    [Fact]
    public async Task Returns_null_when_the_request_fails()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("offline"));
        using var http = new HttpClient(handler);
        var service = new MicrosoftUpdateCatalogService(http);

        Assert.Null(await service.GetSizeBytesAsync("KB5094125", "x64"));
    }

    [Fact]
    public async Task Returns_null_for_a_blank_kb_without_calling_the_network()
    {
        var handler = new StubHandler(_ => Ok(OneRowHtml));
        using var http = new HttpClient(handler);
        var service = new MicrosoftUpdateCatalogService(http);

        Assert.Null(await service.GetSizeBytesAsync("   ", "x64"));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("KB5094125", "KB5094125")]
    [InlineData("5094125", "KB5094125")]
    [InlineData("kb 5094125", "KB5094125")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("no-digits", null)]
    public void NormalizeKb_canonicalizes_or_rejects(string? input, string? expected)
    {
        Assert.Equal(expected, MicrosoftUpdateCatalogService.NormalizeKb(input));
    }

    [Fact]
    public async Task The_catalog_gate_skips_the_network_for_normal_sized_updates()
    {
        // Mirrors WorkspaceViewModel.ResolveCatalogSizesAsync: only rows whose WUA MaxDownloadSize is absurd
        // (UpdateSizeResolver.NeedsCatalogLookup) ever reach the catalog. A normal Defender row must NOT.
        var handler = new StubHandler(_ => Ok(OneRowHtml));
        using var http = new HttpClient(handler);
        var service = new MicrosoftUpdateCatalogService(http);

        (string Kb, long MaxBytes)[] updates =
        [
            ("KB2267602", 1497L * 1048576),   // Defender definition — normal size
            ("KB5094125", 22000L * 1048576),  // express CU — inflated ~21.5 GB aggregate
        ];

        foreach ((string kb, long maxBytes) in updates)
        {
            if (UpdateSizeResolver.NeedsCatalogLookup(maxBytes))
            {
                await service.GetSizeBytesAsync(kb, "x64");
            }
        }

        Assert.Equal(1, handler.Calls); // only the express CU hit the catalog; the normal update did not
    }

    private static HttpResponseMessage Ok(string html) =>
        new(HttpStatusCode.OK) { Content = new StringContent(html) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(responder(request));
        }
    }
}
