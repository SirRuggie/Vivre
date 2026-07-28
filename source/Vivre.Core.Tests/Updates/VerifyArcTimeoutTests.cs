using Vivre.Core.Models;
using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Locks the post-reboot verify arc's ceiling and the HONEST state a cut-short arc lands on.
/// <para>
/// These cover behaviour that did not exist before (the arc was unbounded and received the raw monitor
/// token), so they are NOT regression guards against an older implementation of the same rule — they are
/// the proof of the new rule. The failure they exist to prevent is the one that started this arc: a verify
/// step that gives up must never leave a row looking rebooted, verified, or up to date.
/// </para>
/// </summary>
public class VerifyArcTimeoutTests
{
    private static Computer MidArcRow() => new("BOX01")
    {
        // What the row actually looks like when the ceiling fires: the arc has already written its
        // "rechecking" message and is partway through, so a timeout must overwrite it, not leave it.
        UpdateMessage = "Back online — rechecking for updates…",
        UpdatePhase = PatchPhase.Done.ToString(),
    };

    [Fact]
    public void A_cut_short_arc_never_leaves_the_row_looking_finished()
    {
        Computer row = MidArcRow();

        VerifyArcTimeout.MarkUnverified(row);

        Assert.Equal(PatchPhase.Unverified.ToString(), row.UpdatePhase);
        Assert.Equal(VerifyArcTimeout.RowMessage, row.UpdateMessage);
        Assert.DoesNotContain("up to date", row.UpdateMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rechecking", row.UpdateMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_cut_short_arc_does_NOT_claim_the_reboot_committed()
    {
        // The whole point: a timeout is evidence of nothing. A row known reboot-pending stays pending —
        // clearing it here would be exactly the false success this arc exists to prevent.
        Computer row = MidArcRow();
        row.RebootRequired = true;

        VerifyArcTimeout.MarkUnverified(row);

        Assert.True(row.RebootRequired);
    }

    [Fact]
    public void A_cut_short_arc_does_NOT_clear_the_staged_marker()
    {
        Computer row = MidArcRow();
        row.StagedThisSession = true;

        VerifyArcTimeout.MarkUnverified(row);

        Assert.True(row.StagedThisSession);
    }

    [Fact]
    public void A_cut_short_arc_cannot_later_self_heal_to_green()
    {
        // MonitorSelfHeal lifts an Unverified row only when it was PROBE-ONLY unverified. A timed-out arc
        // never completed its rescan, so it must not qualify — otherwise the next clean probe would quietly
        // turn an unverified box green, which is the same class of lie by a slower route.
        Computer row = MidArcRow();

        VerifyArcTimeout.MarkUnverified(row);

        Assert.False(row.UnverifiedRebootProbeOnly);
        Assert.False(MonitorSelfHeal.ShouldSelfHeal(row.UpdatePhase, row.UnverifiedRebootProbeOnly, probeResult: false));
    }

    [Theory]
    [InlineData(true, false, true)]    // our ceiling fired, monitoring still running -> ours to swallow
    [InlineData(true, true, false)]    // operator stopped monitoring -> NOT ours, must propagate
    [InlineData(false, true, false)]   // stop, arc deadline never fired
    [InlineData(false, false, false)]  // neither -> not a deadline at all
    public void Only_our_own_ceiling_may_be_swallowed(bool arcCancelled, bool monitorCancelled, bool expected)
        => Assert.Equal(expected, VerifyArcTimeout.IsArcDeadline(arcCancelled, monitorCancelled));

    [Fact]
    public void The_ceiling_is_sized_for_the_arc_not_inherited_from_the_reboot_probe()
    {
        // Guard against silently re-inheriting the 120s that was sized for ONE WinRM probe: the arc runs up
        // to three WUA rescans plus retry delays plus its own probe. Also guard the other direction — a
        // ceiling at or above the arc's ~18 min bounded worst case would not bound the monitor stall at all.
        Assert.True(VerifyArcTimeout.Ceiling > TimeSpan.FromSeconds(120));
        Assert.True(VerifyArcTimeout.Ceiling < TimeSpan.FromMinutes(18));
    }

    [Fact]
    public void The_activity_line_names_the_host_and_says_the_row_is_unverified()
    {
        string line = VerifyArcTimeout.ActivityLine("NYC-FP1");

        Assert.Contains("NYC-FP1", line, StringComparison.Ordinal);
        Assert.Contains("Unverified", line, StringComparison.Ordinal);
    }
}
