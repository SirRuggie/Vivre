using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Security.Authentication;

namespace Vivre.Core.Updates;

/// <summary>
/// <see cref="ICatalogSizeService"/> backed by a single HTTPS GET to the public Microsoft Update Catalog
/// (<c>https://www.catalog.update.microsoft.com/Search.aspx?q=KB…</c>), parsed with HtmlAgilityPack
/// (<see cref="CatalogPageParser"/>). Self-contained — no PowerShell module, no shell-out, no third-party
/// catalog service. One shared instance is created at composition root, so its cache is process-wide.
/// </summary>
public sealed class MicrosoftUpdateCatalogService : ICatalogSizeService, IDisposable
{
    private const string SearchUrlFormat = "https://www.catalog.update.microsoft.com/Search.aspx?q={0}";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    // Cache the TASK (not just the value), keyed by KB+arch: many grid rows across many machines show the same
    // KB, and we must hit the catalog ONCE per unique KB — concurrent callers share the single in-flight request,
    // and the published size never changes for a KB within a session. LookupAsync never throws, so a cached task
    // never faults (a failed lookup caches as a completed null = "unavailable", and we don't retry — by design).
    private readonly ConcurrentDictionary<string, Task<long?>> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="http">Inject a pre-configured client for testing; null builds the default TLS-1.2 client.</param>
    public MicrosoftUpdateCatalogService(HttpClient? http = null)
    {
        if (http is not null)
        {
            _http = http;
            _ownsHttp = false;
        }
        else
        {
            // The catalog endpoint requires TLS 1.2; pin it (allow 1.3) rather than relying on the process default.
            var handler = new HttpClientHandler { SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 };
            _http = new HttpClient(handler) { Timeout = RequestTimeout };
            // A browser-like UA avoids the occasional bot rejection from the public catalog.
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Vivre");
            _ownsHttp = true;
        }
    }

    public Task<long?> GetSizeBytesAsync(string? kb, string? arch, CancellationToken cancellationToken = default)
    {
        string? normalized = NormalizeKb(kb);
        if (normalized is null)
        {
            return Task.FromResult<long?>(null);
        }

        string key = $"{normalized}|{arch?.ToLowerInvariant() ?? "any"}";
        return _cache.GetOrAdd(key, _ => LookupAsync(normalized, arch, cancellationToken));
    }

    private async Task<long?> LookupAsync(string kb, string? arch, CancellationToken cancellationToken)
    {
        try
        {
            string url = string.Format(CultureInfo.InvariantCulture, SearchUrlFormat, Uri.EscapeDataString(kb));
            string html = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<CatalogRow> rows = CatalogPageParser.ParseRows(html);
            return CatalogPageParser.SelectSizeBytes(rows, arch);
        }
        catch (Exception)
        {
            // Any failure (offline / KB not found / row miss / parse / timeout / HTTP error / cancellation) is
            // reported as "unavailable" (null) so UpdateSizeResolver falls through to a WUA-definite size or a
            // dash. Deliberately NOT surfaced to the activity log: a catalog miss is an expected, benign outcome
            // on locked-down/air-gapped hosts (per the spec), and Core services stay log-free. The null return IS
            // the surfaced outcome — it changes what the grid shows — so this is not a silent swallow.
            return null;
        }
    }

    /// <summary>"KB5094125" / "5094125" / "kb 5094125" → "KB5094125"; null / blank / no digits → null.</summary>
    internal static string? NormalizeKb(string? kb)
    {
        if (string.IsNullOrWhiteSpace(kb))
        {
            return null;
        }

        string digits = new([.. kb.Where(char.IsDigit)]);
        return digits.Length == 0 ? null : "KB" + digits;
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
