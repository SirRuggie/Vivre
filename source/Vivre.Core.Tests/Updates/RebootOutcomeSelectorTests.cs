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
}
