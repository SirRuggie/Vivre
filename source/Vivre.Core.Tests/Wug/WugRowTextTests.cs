using Vivre.Core.Wug;
using Xunit;

namespace Vivre.Core.Tests.Wug;

/// <summary>
/// Locks the exact per-row Command-result strings of the WUG state check. The three "no result"
/// flavours must stay distinct: "state unknown" (WUG answered, no definite state), "no matching device"
/// (name mapped to nothing), and "not checked (read stopped)" (Vivre never got an answer). A regression
/// that blurred NotChecked into either of the other two would tell the operator a stopped/aborted read
/// was a definite finding.
/// </summary>
public class WugRowTextTests
{
    [Fact]
    public void Checking_exact()
        => Assert.Equal("WhatsUp Gold: checking state…", WugRowText.Checking);

    [Fact]
    public void InMaintenance_exact()
        => Assert.Equal("WhatsUp Gold: in maintenance", WugRowText.InMaintenance);

    [Fact]
    public void NotInMaintenance_exact()
        => Assert.Equal("WhatsUp Gold: not in maintenance", WugRowText.NotInMaintenance);

    [Fact]
    public void NoMatchingDevice_exact()
        => Assert.Equal("WhatsUp Gold: no matching device (by IP)", WugRowText.NoMatchingDevice);

    [Fact]
    public void StateUnknown_exact()
        => Assert.Equal("WhatsUp Gold: state unknown", WugRowText.StateUnknown);

    [Fact]
    public void NotChecked_exact()
        => Assert.Equal("WhatsUp Gold: not checked (read stopped)", WugRowText.NotChecked);

    [Fact]
    public void NotChecked_never_reads_as_a_data_gap_or_a_name_miss()
    {
        // Load-bearing: "not checked" means Vivre never got an answer for this row (abort/stop). It must
        // NEVER contain "unknown" (which means WUG answered without a definite state) or "no matching
        // device" (which means the name mapped to nothing) — conflating them would misreport a stopped
        // read as a definite WUG finding.
        Assert.DoesNotContain("unknown", WugRowText.NotChecked, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no matching device", WugRowText.NotChecked, System.StringComparison.OrdinalIgnoreCase);
    }

    // ── ComposeInMaintenance: the enriched in-maintenance row text ──────────────────────────────────

    private static readonly System.DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, System.TimeSpan.Zero);

    [Fact]
    public void ComposeInMaintenance_all_absent_is_exactly_the_plain_constant()
        => Assert.Equal(WugRowText.InMaintenance, WugRowText.ComposeInMaintenance(null, null, null, Now));

    [Fact]
    public void ComposeInMaintenance_full_detail_exact()
        => Assert.Equal(
            "WhatsUp Gold: in maintenance — \"Rebuilding - SB\" (admin_sbridges, since 2026-05-27, 53d)",
            WugRowText.ComposeInMaintenance("Rebuilding - SB", "admin_sbridges", "2026-05-27T21:14:42Z", Now));

    [Fact]
    public void ComposeInMaintenance_reason_only()
        => Assert.Equal(
            "WhatsUp Gold: in maintenance — \"Rebuilding - SB\"",
            WugRowText.ComposeInMaintenance("Rebuilding - SB", null, null, Now));

    [Fact]
    public void ComposeInMaintenance_user_only()
        => Assert.Equal(
            "WhatsUp Gold: in maintenance (admin_sbridges)",
            WugRowText.ComposeInMaintenance(null, "admin_sbridges", null, Now));

    [Fact]
    public void ComposeInMaintenance_unparseable_since_is_omitted_never_garbage()
        => Assert.Equal(
            "WhatsUp Gold: in maintenance (admin_sbridges)",
            WugRowText.ComposeInMaintenance(null, "admin_sbridges", "not-a-date", Now));

    [Fact]
    public void ComposeInMaintenance_same_day_since_drops_the_day_count()
        => Assert.Equal(
            "WhatsUp Gold: in maintenance (since 2026-07-20)",
            WugRowText.ComposeInMaintenance(null, null, "2026-07-20T09:00:00Z", Now));

    [Fact]
    public void ComposeInMaintenance_future_since_from_clock_skew_keeps_date_drops_count()
        => Assert.Equal(
            "WhatsUp Gold: in maintenance (since 2026-07-21)",
            WugRowText.ComposeInMaintenance(null, null, "2026-07-21T09:00:00Z", Now));

    [Fact]
    public void ComposeInMaintenance_always_starts_with_the_plain_constant()
    {
        // Load-bearing prefix invariant: with or without detail, an in-maintenance row starts with the
        // exact plain text — anything keying on the state string keeps working.
        string s = WugRowText.ComposeInMaintenance("r", "u", "2026-05-21T07:00:07Z", Now);
        Assert.StartsWith(WugRowText.InMaintenance, s, System.StringComparison.Ordinal);
    }

    // ── ComposeMaintenanceDigest: the activity-log fleet digest ─────────────────────────────────────

    [Fact]
    public void ComposeMaintenanceDigest_null_when_nothing_in_maintenance()
    {
        var byName = new System.Collections.Generic.Dictionary<string, bool?> { ["A"] = false, ["B"] = null };
        Assert.Null(WugRowText.ComposeMaintenanceDigest(byName, null, Now));
    }

    [Fact]
    public void ComposeMaintenanceDigest_lists_detail_and_plain_entries_name_sorted()
    {
        var byName = new System.Collections.Generic.Dictionary<string, bool?> { ["ZBOX"] = true, ["ABOX"] = true, ["OK1"] = false };
        var details = new System.Collections.Generic.Dictionary<string, Vivre.Core.Wug.WugMaintenanceDetail>
        {
            ["ABOX"] = new("Rebuilding - SB", "admin_sbridges", "2026-05-27T21:14:42Z"),
        };

        Assert.Equal(
            "WhatsUp Gold in maintenance: ABOX — \"Rebuilding - SB\" (admin_sbridges, since 2026-05-27, 53d); ZBOX",
            WugRowText.ComposeMaintenanceDigest(byName, details, Now));
    }

    [Fact]
    public void ComposeMaintenanceDigest_caps_entries_and_says_how_many_more()
    {
        var byName = new System.Collections.Generic.Dictionary<string, bool?>();
        for (int i = 1; i <= 9; i++) { byName[$"BOX{i}"] = true; }

        string? digest = WugRowText.ComposeMaintenanceDigest(byName, null, Now, maxEntries: 6);

        Assert.NotNull(digest);
        Assert.EndsWith("; … and 3 more", digest, System.StringComparison.Ordinal);
        Assert.Contains("BOX6", digest, System.StringComparison.Ordinal);
        Assert.DoesNotContain("BOX7", digest, System.StringComparison.Ordinal);
    }
}
