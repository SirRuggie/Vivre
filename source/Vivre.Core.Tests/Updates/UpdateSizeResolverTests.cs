using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// <see cref="UpdateSizeResolver"/> — the pure tiered size resolution that decides the grid's Size value:
/// WUA's MaxDownloadSize is PRIMARY (matching BatchPatch) for every normal update; the Microsoft Update Catalog
/// size is substituted ONLY when WUA's value is implausibly large (express CUs); dash only when both fail.
/// </summary>
public class UpdateSizeResolverTests
{
    private const long Mb = 1048576; // one mebibyte in bytes

    [Fact]
    public void Max_download_size_is_the_primary_for_a_normal_update()
    {
        // Defender / driver / SQL / .NET / normal CU: WUA's MaxDownloadSize is realistic — show it, no catalog.
        // This is the regression the re-order fixes (these used to dash).
        double? mb = UpdateSizeResolver.ResolveDisplaySize(catalogBytes: null, minBytes: 0, maxBytes: 1497 * Mb);

        Assert.Equal(1497.0, mb);
    }

    [Fact]
    public void Min_is_used_when_max_is_zero()
    {
        // Some updates leave Max at 0 but populate Min.
        double? mb = UpdateSizeResolver.ResolveDisplaySize(catalogBytes: null, minBytes: 50 * Mb, maxBytes: 0);

        Assert.Equal(50.0, mb);
    }

    [Fact]
    public void Catalog_overrides_an_absurd_max()
    {
        // Express CU: MaxDownloadSize is the inflated ~21.5 GB aggregate → substitute the catalog's exact size.
        double? mb = UpdateSizeResolver.ResolveDisplaySize(catalogBytes: 2435 * Mb, minBytes: 0, maxBytes: 22000 * Mb);

        Assert.Equal(2435.0, mb);
    }

    [Fact]
    public void Dash_when_max_absurd_and_catalog_absent()
    {
        // Inflated express CU with no catalog answer (offline / locked-down): a dash, NEVER the inflated Max.
        double? mb = UpdateSizeResolver.ResolveDisplaySize(catalogBytes: null, minBytes: 0, maxBytes: 22000 * Mb);

        Assert.Null(mb);
    }

    [Fact]
    public void Dash_when_max_and_min_zero_and_catalog_absent()
    {
        double? mb = UpdateSizeResolver.ResolveDisplaySize(catalogBytes: null, minBytes: 0, maxBytes: 0);

        Assert.Null(mb);
    }

    [Fact]
    public void Normal_max_wins_even_when_a_catalog_value_is_present()
    {
        // Precedence: a non-absurd WUA Max is primary; the catalog is the absurd-case OVERRIDE only.
        double? mb = UpdateSizeResolver.ResolveDisplaySize(catalogBytes: 2435 * Mb, minBytes: 0, maxBytes: 1497 * Mb);

        Assert.Equal(1497.0, mb);
    }

    [Theory]
    [InlineData(1497L * 1048576, false)] // normal Defender — resolves from WUA, no network
    [InlineData(0L, false)]              // unknown size — no lookup (catalog only rescues the absurd case)
    [InlineData(22000L * 1048576, true)] // ~21.5 GB express-CU aggregate — needs the catalog
    public void NeedsCatalogLookup_is_true_only_for_an_absurd_max(long maxBytes, bool expected)
    {
        Assert.Equal(expected, UpdateSizeResolver.NeedsCatalogLookup(maxBytes));
    }

    [Fact]
    public void NeedsCatalogLookup_boundary_is_exclusive_at_the_cap()
    {
        Assert.False(UpdateSizeResolver.NeedsCatalogLookup(UpdateSizeResolver.AbsurdMaxDownloadSizeBytes));     // exactly 10 GB is showable
        Assert.True(UpdateSizeResolver.NeedsCatalogLookup(UpdateSizeResolver.AbsurdMaxDownloadSizeBytes + 1));  // 1 byte over → absurd
    }

    [Theory]
    [InlineData("2026-06 Cumulative Update for Windows Server 2025 for x64-based Systems (KB5094125)", "x64")]
    [InlineData("2026-06 Cumulative Update for Windows Server 2025 for ARM64-based Systems (KB5094125)", "arm64")]
    [InlineData("2024-05 Cumulative Update for Windows 10 for x86-based Systems (KB5037782)", "x86")]
    [InlineData("Security Update for Microsoft .NET Framework (KB5000001)", null)]
    [InlineData("", null)]
    public void ArchFromTitle_extracts_the_architecture_hint(string title, string? expected)
    {
        Assert.Equal(expected, UpdateSizeResolver.ArchFromTitle(title));
    }
}
