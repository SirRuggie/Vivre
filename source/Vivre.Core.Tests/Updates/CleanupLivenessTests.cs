using System;
using Vivre.UpdateAgent;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Tests the agent's pure component-cleanup liveness decisions (linked source — the agent is net48
/// and can't be referenced from this net10 project). The live CPU sampling + Process.Kill that drive
/// these predicates live in the agent's Program.cs and need a real process, so they aren't unit-tested;
/// the go/no-go logic and the elapsed formatting are what matter here.
/// </summary>
public class CleanupLivenessTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(45);

    [Fact]
    public void Not_hung_when_under_the_window()
    {
        Assert.False(CleanupLiveness.IsHung(TimeSpan.FromMinutes(44), Window));
        Assert.False(CleanupLiveness.IsHung(TimeSpan.Zero, Window));
        Assert.False(CleanupLiveness.IsHung(TimeSpan.FromMinutes(44.999), Window));
    }

    [Fact]
    public void Hung_at_exactly_the_window_boundary_inclusive()
    {
        // Boundary is inclusive: at exactly the window with no CPU advance, treat as hung.
        Assert.True(CleanupLiveness.IsHung(Window, Window));
    }

    [Fact]
    public void Hung_when_over_the_window()
    {
        Assert.True(CleanupLiveness.IsHung(TimeSpan.FromMinutes(46), Window));
        Assert.True(CleanupLiveness.IsHung(TimeSpan.FromHours(3), Window));
    }

    [Theory]
    [InlineData(0.0, false)]   // no advance at all
    [InlineData(0.5, false)]   // half a second — below the 1s threshold (idle-thread noise)
    [InlineData(1.0, false)]   // exactly the threshold is NOT "more than" — strictly greater required
    [InlineData(1.5, true)]    // over the threshold — real work, resets the hang clock
    [InlineData(30.0, true)]
    public void CpuAdvancedMeaningfully_requires_strictly_more_than_the_threshold(double deltaSeconds, bool expected)
    {
        var highWater = TimeSpan.FromSeconds(100);
        var current = highWater + TimeSpan.FromSeconds(deltaSeconds);
        var threshold = TimeSpan.FromSeconds(1);

        Assert.Equal(expected, CleanupLiveness.CpuAdvancedMeaningfully(highWater, current, threshold));
    }

    [Fact]
    public void CpuAdvancedMeaningfully_treats_a_regression_as_no_advance()
    {
        // TotalProcessorTime shouldn't go backwards, but if a flaky sample reports less than the
        // high-water mark, that is never a meaningful advance (must not reset the hang clock).
        var highWater = TimeSpan.FromSeconds(100);
        var current = TimeSpan.FromSeconds(90);
        Assert.False(CleanupLiveness.CpuAdvancedMeaningfully(highWater, current, TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(0, "<1m")]
    [InlineData(30, "<1m")]      // 30s rounds down to under a minute
    [InlineData(59, "<1m")]
    [InlineData(60, "1m")]
    [InlineData(12 * 60, "12m")]
    [InlineData(60 * 60, "1h")]           // exactly one hour, no trailing minutes
    [InlineData(60 * 60 + 4 * 60, "1h 4m")]
    [InlineData(2 * 60 * 60 + 59 * 60, "2h 59m")]
    public void FormatElapsed_produces_compact_human_durations(int totalSeconds, string expected)
    {
        Assert.Equal(expected, CleanupLiveness.FormatElapsed(TimeSpan.FromSeconds(totalSeconds)));
    }

    [Fact]
    public void FormatElapsed_clamps_negative_to_under_a_minute()
    {
        // A clock skew shouldn't ever produce a negative elapsed in the line; clamp to the floor.
        Assert.Equal("<1m", CleanupLiveness.FormatElapsed(TimeSpan.FromSeconds(-5)));
    }
}
