using Vivre.Core.Software;
using Xunit;

namespace Vivre.Core.Tests.Software;

public class SoftwareShapingTests
{
    private static UninstallRow Row(string? name, string? version = null, string? publisher = null) =>
        new(name, version, publisher);

    [Fact]
    public void Match_finds_product_by_name_substring()
    {
        var rows = new[] { Row("Google Chrome", "120.0"), Row("Mozilla Firefox", "118.0") };

        (bool found, string? name, string? version) = SoftwareShaping.Match(rows, "chrome");

        Assert.True(found);
        Assert.Equal("Google Chrome", name);
        Assert.Equal("120.0", version);
    }

    [Fact]
    public void Match_finds_product_by_publisher_when_name_misses()
    {
        var rows = new[] { Row("Falcon Sensor", "7.18", "CrowdStrike, Inc.") };

        (bool found, string? name, _) = SoftwareShaping.Match(rows, "CrowdStrike");

        Assert.True(found);
        Assert.Equal("Falcon Sensor", name);
    }

    [Fact]
    public void Match_orders_across_match_types_by_display_name()
    {
        // "Acme Tool" matches on PUBLISHER; "CrowdStrike Sensor" matches on NAME. The winner is chosen by
        // DisplayName ordering ACROSS match types, so "Acme Tool" (alphabetically first) wins.
        var rows = new[]
        {
            Row("Acme Tool", "1.0", "CrowdStrike"),
            Row("CrowdStrike Sensor", "2.0"),
        };

        (bool found, string? name, _) = SoftwareShaping.Match(rows, "CrowdStrike");

        Assert.True(found);
        Assert.Equal("Acme Tool", name);
    }

    [Fact]
    public void Match_returns_first_by_display_name_on_multiple_matches()
    {
        var rows = new[] { Row("Zeta Agent", "9"), Row("Alpha Agent", "1"), Row("Mid Agent", "5") };

        (_, string? name, string? version) = SoftwareShaping.Match(rows, "Agent");

        Assert.Equal("Alpha Agent", name);
        Assert.Equal("1", version);
    }

    [Fact]
    public void MatchAcrossHives_gives_first_hive_precedence_over_alphabetical()
    {
        var hive1 = new[] { Row("Zeta Agent", "9") };
        var hive2 = new[] { Row("Alpha Agent", "1") };

        // hive1's "Zeta" match wins even though hive2 holds an alphabetically-earlier "Alpha" — parity
        // with the WinRM script's foreach-break (never a global concat + sort).
        (bool found, string? name, _) = SoftwareShaping.MatchAcrossHives(hive1, hive2, "Agent");

        Assert.True(found);
        Assert.Equal("Zeta Agent", name);
    }

    [Fact]
    public void MatchAcrossHives_falls_through_to_second_hive_when_first_misses()
    {
        var hive1 = new[] { Row("Google Chrome", "120") };
        var hive2 = new[] { Row("CrowdStrike Sensor", "7") };

        (bool found, string? name, _) = SoftwareShaping.MatchAcrossHives(hive1, hive2, "CrowdStrike");

        Assert.True(found);
        Assert.Equal("CrowdStrike Sensor", name);
    }

    [Fact]
    public void Match_blank_display_version_yields_null_version()
    {
        var rows = new[] { Row("Some App", "   ") };

        (bool found, _, string? version) = SoftwareShaping.Match(rows, "Some App");

        Assert.True(found);
        Assert.Null(version);
    }

    [Fact]
    public void Match_genuine_absence_returns_found_false()
    {
        var rows = new[] { Row("Google Chrome"), Row("Mozilla Firefox") };

        (bool found, string? name, string? version) = SoftwareShaping.Match(rows, "CrowdStrike");

        Assert.False(found);
        Assert.Null(name);
        Assert.Null(version);
    }

    [Fact]
    public void Match_empty_but_complete_row_list_returns_found_false()
    {
        // An empty list is a COMPLETED read of a box with no matching entries — not a failure.
        (bool found, _, _) = SoftwareShaping.Match([], "CrowdStrike");

        Assert.False(found);
    }

    [Fact]
    public void Match_drops_rows_with_blank_display_name()
    {
        // A blank DisplayName is dropped (parity with the WinRM "$_.DisplayName -and" guard), even when
        // its Publisher would otherwise match.
        var rows = new[] { Row(null, "1", "CrowdStrike"), Row("   ", "2", "CrowdStrike") };

        (bool found, _, _) = SoftwareShaping.Match(rows, "CrowdStrike");

        Assert.False(found);
    }

    [Theory]
    [InlineData("Running", "Running")]
    [InlineData("Stopped", "Stopped")]
    [InlineData("Paused", "Paused")]
    [InlineData("Start Pending", "StartPending")]
    [InlineData("Stop Pending", "StopPending")]
    [InlineData("Continue Pending", "ContinuePending")]
    [InlineData("Pause Pending", "PausePending")]
    [InlineData("Some Unknown State", "Some Unknown State")]
    public void NormalizeServiceState_maps_spaced_win32_spellings(string input, string expected) =>
        Assert.Equal(expected, SoftwareShaping.NormalizeServiceState(input));
}
