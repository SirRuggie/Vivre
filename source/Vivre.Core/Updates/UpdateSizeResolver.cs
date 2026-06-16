namespace Vivre.Core.Updates;

/// <summary>
/// Decides the single download size to SHOW for an update, in priority order, from WUA's reported Min/Max
/// download sizes and — only as a rescue — the Microsoft Update Catalog size. Pure + static so the tier logic is
/// unit-tested in isolation (no catalog HTTP, no WUA).
///
/// <para>WUA's <c>IUpdate.MaxDownloadSize</c> is a realistic download size for the vast majority of updates
/// (Defender, drivers, .NET, SQL, normal cumulative updates) and is already in the scan result — so it's the
/// PRIMARY source, matching what BatchPatch shows, at zero scan-time cost. The ONE exception is an
/// express/checkpoint OS cumulative update (Server 2025 / Windows 11 24H2), whose <c>MaxDownloadSize</c> is a
/// worst-case differential AGGREGATE — wildly inflated (e.g. ~21.9 GB for a ~2.4 GB package). We detect that by an
/// implausibly large value and substitute the Catalog's exact published .msu size; only if the catalog also can't
/// answer do we show a dash.</para>
/// </summary>
public static class UpdateSizeResolver
{
    /// <summary>One mebibyte in bytes — the divisor used everywhere a byte count is shown as "MB".</summary>
    private const double BytesPerMb = 1048576.0;

    /// <summary>
    /// The largest WUA <c>MaxDownloadSize</c> we treat as a real, showable figure (10 GB). It sits above the
    /// largest legitimate single update — full feature updates / big cumulative updates are a few GB — and below
    /// the tens-of-GB worst-case aggregate that ONLY express/UUP cumulative updates produce. A value above this is
    /// the inflated-CU case where we substitute the catalog size instead. Tune here if real update sizes grow.
    /// </summary>
    public const long AbsurdMaxDownloadSizeBytes = 10_737_418_240L; // 10 * 1024^3

    /// <summary>
    /// Resolves the display size in MB, or <c>null</c> to render a dash ("—"):
    /// <list type="number">
    /// <item>PRIMARY — WUA's <paramref name="maxBytes"/> when it's &gt; 0 and NOT absurd (≤ 10 GB). Realistic for
    ///   nearly every update and instant (already in the scan result). If <paramref name="maxBytes"/> is 0 but
    ///   <paramref name="minBytes"/> &gt; 0, the Min is used (some updates populate only Min).</item>
    /// <item>OVERRIDE — when <paramref name="maxBytes"/> is absurd (&gt; 10 GB), i.e. an inflated express CU, the
    ///   <paramref name="catalogBytes"/> value is used if available (the exact published package size). The catalog
    ///   is consulted ONLY in this case (see <see cref="NeedsCatalogLookup"/>).</item>
    /// <item>DASH — only when BOTH fail: Max is absurd (or absent) AND no catalog answer (and the Min fallback
    ///   didn't apply). A genuine "size unknown", never a whole category of updates.</item>
    /// </list>
    /// </summary>
    /// <param name="catalogBytes">Microsoft Update Catalog package size in bytes, or null when not looked up /
    /// the lookup couldn't answer. Consulted ONLY when <paramref name="maxBytes"/> is absurd.</param>
    /// <param name="minBytes">WUA <c>MinDownloadSize</c> in bytes (0 when unknown).</param>
    /// <param name="maxBytes">WUA <c>MaxDownloadSize</c> in bytes (0 when unknown).</param>
    public static double? ResolveDisplaySize(long? catalogBytes, long minBytes, long maxBytes)
    {
        // 1. PRIMARY: WUA's MaxDownloadSize, unless it's implausibly large (the express-CU aggregate).
        if (maxBytes > 0 && maxBytes <= AbsurdMaxDownloadSizeBytes)
        {
            return BytesToMb(maxBytes);
        }

        // Some updates leave Max at 0 but populate Min — use it (still a real, non-absurd WUA figure).
        if (maxBytes == 0 && minBytes > 0)
        {
            return BytesToMb(minBytes);
        }

        // 2. OVERRIDE: an absurd Max is an inflated express CU — substitute the catalog's exact size if we have it.
        if (maxBytes > AbsurdMaxDownloadSizeBytes && catalogBytes is { } catalog && catalog > 0)
        {
            return BytesToMb(catalog);
        }

        // 3. DASH: Max absurd (or absent) and no catalog answer — a genuine unknown.
        return null;
    }

    /// <summary>
    /// Whether an update needs a Microsoft Update Catalog lookup — true ONLY when WUA's <paramref name="maxBytes"/>
    /// is absurd (&gt; 10 GB, the inflated express-CU case). The vast majority of updates resolve from the WUA value
    /// with NO network call, so the catalog (and its jump-box TLS dependency) is touched only for the handful of
    /// express-CU rows that actually need it. Gates <c>WorkspaceViewModel.ResolveCatalogSizesAsync</c>.
    /// </summary>
    public static bool NeedsCatalogLookup(long maxBytes) => maxBytes > AbsurdMaxDownloadSizeBytes;

    /// <summary>Bytes → MB (mebibytes), rounded to one decimal, matching the catalog's own MB display.</summary>
    public static double BytesToMb(long bytes) => Math.Round(bytes / BytesPerMb, 1);

    /// <summary>
    /// Best-effort processor architecture extracted from a WUA update title (e.g. "… for x64-based Systems
    /// (KB…)") so the catalog row-match can prefer the matching SKU. Returns "x64" / "arm64" / "x86", or null
    /// when the title carries no architecture hint (then the catalog lookup falls back to the largest row).
    /// Heuristic — refine as catalog/title conventions evolve. ARM64 is tested first because its token contains
    /// neither "x64" nor "x86".
    /// </summary>
    public static string? ArchFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        string t = title.ToLowerInvariant();
        if (t.Contains("arm64"))
        {
            return "arm64";
        }

        if (t.Contains("x64"))
        {
            return "x64";
        }

        if (t.Contains("x86"))
        {
            return "x86";
        }

        return null;
    }
}
