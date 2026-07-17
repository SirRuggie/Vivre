using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Tests for <see cref="RebootOutcomeSelector.Select"/> — covers every branch and precedence rule.
/// </summary>
public class RebootOutcomeSelectorTests
{
    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public void UpToDate_returns_BackOnlineUpToDate()
    {
        string result = RebootOutcomeSelector.Select(installed: 3, failed: 0, remaining: 0,
            rebootStillPending: false, scanFailed: false);

        Assert.Equal("Back online · installed 3 · up to date", result);
    }

    // ── remaining > 0 ─────────────────────────────────────────────────────────

    [Fact]
    public void Remaining_returns_BackOnlineRemaining()
    {
        string result = RebootOutcomeSelector.Select(installed: 2, failed: 0, remaining: 4,
            rebootStillPending: false, scanFailed: false);

        Assert.Equal("Back online · installed 2 · 4 remaining", result);
    }

    // ── failed > 0 ────────────────────────────────────────────────────────────

    [Fact]
    public void Failed_no_remaining_returns_BackOnlineFailed_without_tail()
    {
        string result = RebootOutcomeSelector.Select(installed: 2, failed: 1, remaining: 0,
            rebootStillPending: false, scanFailed: false);

        Assert.Equal("Back online · installed 2 · 1 failed", result);
    }

    [Fact]
    public void Failed_with_remaining_returns_BackOnlineFailed_with_tail()
    {
        string result = RebootOutcomeSelector.Select(installed: 1, failed: 2, remaining: 3,
            rebootStillPending: false, scanFailed: false);

        Assert.Equal("Back online · installed 1 · 2 failed · 3 remaining", result);
    }

    // ── rebootStillPending ────────────────────────────────────────────────────

    [Fact]
    public void RebootStillPending_returns_RebootStillPending_string()
    {
        string result = RebootOutcomeSelector.Select(installed: 5, failed: 0, remaining: 0,
            rebootStillPending: true, scanFailed: false);

        Assert.Equal("Back online · reboot still pending — re-check", result);
    }

    // ── scanFailed precedence ─────────────────────────────────────────────────

    [Fact]
    public void ScanFailed_alone_returns_BackOnlineRescanFailed()
    {
        string result = RebootOutcomeSelector.Select(installed: 0, failed: 0, remaining: 0,
            rebootStillPending: false, scanFailed: true);

        Assert.Equal("Back online · couldn't rescan — re-check", result);
    }

    [Fact]
    public void ScanFailed_beats_failed()
    {
        string result = RebootOutcomeSelector.Select(installed: 1, failed: 3, remaining: 0,
            rebootStillPending: false, scanFailed: true);

        Assert.Equal("Back online · couldn't rescan — re-check", result);
    }

    [Fact]
    public void ScanFailed_beats_remaining()
    {
        string result = RebootOutcomeSelector.Select(installed: 0, failed: 0, remaining: 5,
            rebootStillPending: false, scanFailed: true);

        Assert.Equal("Back online · couldn't rescan — re-check", result);
    }

    [Fact]
    public void ScanFailed_beats_rebootStillPending()
    {
        string result = RebootOutcomeSelector.Select(installed: 0, failed: 0, remaining: 0,
            rebootStillPending: true, scanFailed: true);

        Assert.Equal("Back online · couldn't rescan — re-check", result);
    }

    [Fact]
    public void ScanFailed_beats_all_flags_combined()
    {
        string result = RebootOutcomeSelector.Select(installed: 2, failed: 1, remaining: 3,
            rebootStillPending: true, scanFailed: true);

        Assert.Equal("Back online · couldn't rescan — re-check", result);
    }

    // ── probe-unknown (rebootStillPending: null) — the false-green fix ────────

    [Fact]
    public void Unknown_probe_on_a_clean_box_is_never_up_to_date()
    {
        // The load-bearing case: a failed/timed-out probe used to collapse to false and render
        // "up to date" — it must now read as an honest couldn't-confirm.
        string result = RebootOutcomeSelector.Select(installed: 3, failed: 0, remaining: 0,
            rebootStillPending: null, scanFailed: false);

        Assert.Equal("Back online · installed 3 · couldn't confirm reboot state — re-check", result);
    }

    [Fact]
    public void Unknown_probe_with_no_install_omits_the_count_clause()
    {
        string result = RebootOutcomeSelector.Select(installed: null, failed: null, remaining: 0,
            rebootStillPending: null, scanFailed: false);

        Assert.Equal("Back online · couldn't confirm reboot state — re-check", result);
    }

    [Fact]
    public void Unknown_probe_does_not_hide_remaining()
    {
        // Real actionable data keeps winning: unknown only replaces the false-green up-to-date.
        string result = RebootOutcomeSelector.Select(installed: 2, failed: 0, remaining: 4,
            rebootStillPending: null, scanFailed: false);

        Assert.Equal("Back online · installed 2 · 4 remaining", result);
    }

    [Fact]
    public void Unknown_probe_does_not_hide_failed()
    {
        string result = RebootOutcomeSelector.Select(installed: 2, failed: 1, remaining: 0,
            rebootStillPending: null, scanFailed: false);

        Assert.Equal("Back online · installed 2 · 1 failed", result);
    }

    [Fact]
    public void ScanFailed_beats_unknown_probe()
    {
        string result = RebootOutcomeSelector.Select(installed: null, failed: null, remaining: 0,
            rebootStillPending: null, scanFailed: true);

        Assert.Equal("Back online · couldn't rescan — re-check", result);
    }

    [Fact]
    public void Confirmed_clean_probe_is_not_treated_as_unknown()
    {
        // false means the probe RAN and confirmed no pending reboot — the green path must survive.
        string result = RebootOutcomeSelector.Select(installed: null, failed: null, remaining: 0,
            rebootStillPending: false, scanFailed: false);

        Assert.Equal("Back online · up to date", result);
    }

    [Fact]
    public void Confirmed_pending_beats_remaining()
    {
        string result = RebootOutcomeSelector.Select(installed: 1, failed: 0, remaining: 4,
            rebootStillPending: true, scanFailed: false);

        Assert.Equal("Back online · reboot still pending — re-check", result);
    }

    // ── null install counts (no un-consumed install this session) ─────────────

    [Fact]
    public void Null_counts_up_to_date_omits_the_installed_clause()
    {
        // A standalone Reboot & verify with no prior install must not claim "installed 0".
        string result = RebootOutcomeSelector.Select(installed: null, failed: null, remaining: 0,
            rebootStillPending: false, scanFailed: false);

        Assert.Equal("Back online · up to date", result);
    }

    [Fact]
    public void Null_counts_with_remaining_omits_the_installed_clause()
    {
        string result = RebootOutcomeSelector.Select(installed: null, failed: null, remaining: 3,
            rebootStillPending: false, scanFailed: false);

        Assert.Equal("Back online · 3 remaining", result);
    }

    // ── Classify: the single source of precedence Select delegates to ─────────
    // Same truthfulness-first ladder: scanFailed > failed > confirmed-pending > remaining >
    // couldn't-confirm > up-to-date. These lock in the outcome KIND the display state keys off.

    [Fact]
    public void Classify_scanFailed_wins_over_everything()
    {
        // scanFailed set together with failed>0, remaining>0 AND pending=true — it still wins.
        Assert.Equal(RebootOutcomeKind.CouldntRescan,
            RebootOutcomeSelector.Classify(failed: 2, remaining: 5, rebootStillPending: true, scanFailed: true));
    }

    [Fact]
    public void Classify_failed_beats_pending_and_remaining()
    {
        Assert.Equal(RebootOutcomeKind.Failed,
            RebootOutcomeSelector.Classify(failed: 2, remaining: 3, rebootStillPending: true, scanFailed: false));
    }

    [Fact]
    public void Classify_confirmed_pending_beats_remaining()
    {
        Assert.Equal(RebootOutcomeKind.RebootStillPending,
            RebootOutcomeSelector.Classify(failed: 0, remaining: 3, rebootStillPending: true, scanFailed: false));
    }

    [Fact]
    public void Classify_remaining_when_not_pending()
    {
        Assert.Equal(RebootOutcomeKind.Remaining,
            RebootOutcomeSelector.Classify(failed: 0, remaining: 3, rebootStillPending: false, scanFailed: false));
    }

    [Fact]
    public void Classify_unknown_probe_on_a_clean_box_is_CouldntConfirm()
    {
        Assert.Equal(RebootOutcomeKind.CouldntConfirm,
            RebootOutcomeSelector.Classify(failed: 0, remaining: 0, rebootStillPending: null, scanFailed: false));
    }

    [Fact]
    public void Classify_confirmed_clean_is_UpToDate()
    {
        Assert.Equal(RebootOutcomeKind.UpToDate,
            RebootOutcomeSelector.Classify(failed: 0, remaining: 0, rebootStillPending: false, scanFailed: false));
    }
}
