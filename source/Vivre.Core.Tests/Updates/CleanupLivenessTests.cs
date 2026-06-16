using System;
using Vivre.UpdateAgent;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Tests the agent's pure component-cleanup liveness decisions (linked source — the agent is net48
/// and can't be referenced from this net10 project). The live CPU/log sampling that drives these
/// predicates lives in the agent's Program.cs and needs real processes + the CBS log folder, so it
/// isn't unit-tested; the go/no-go logic and the elapsed formatting are what matter here.
///
/// <para>Stall semantics: a stack that is CPU-active OR whose CBS log grew is NEVER stalled; only an
/// all-quiet stack+log reads stalled — and stalled is a non-terminal DISPLAY FLAG, never a kill.</para>
/// </summary>
public class CleanupLivenessTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(45);

    [Fact]
    public void Not_stalled_when_under_the_window()
    {
        Assert.False(CleanupLiveness.IsStalled(TimeSpan.FromMinutes(44), Window));
        Assert.False(CleanupLiveness.IsStalled(TimeSpan.Zero, Window));
        Assert.False(CleanupLiveness.IsStalled(TimeSpan.FromMinutes(44.999), Window));
    }

    [Fact]
    public void Stalled_at_exactly_the_window_boundary_inclusive()
    {
        // Boundary is inclusive: at exactly the window with no activity, flag as stalled.
        Assert.True(CleanupLiveness.IsStalled(Window, Window));
    }

    [Fact]
    public void Stalled_when_over_the_window()
    {
        Assert.True(CleanupLiveness.IsStalled(TimeSpan.FromMinutes(46), Window));
        Assert.True(CleanupLiveness.IsStalled(TimeSpan.FromHours(3), Window));
    }

    [Theory]
    [InlineData(0.0, false)]   // no advance at all
    [InlineData(0.5, false)]   // half a second — below the 1s threshold (idle-thread noise)
    [InlineData(1.0, false)]   // exactly the threshold is NOT "more than" — strictly greater required
    [InlineData(1.5, true)]    // over the threshold — real work, resets the stall clock
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
        // Summed-stack TotalProcessorTime shouldn't go backwards, but if a process exited between samples
        // and the sum reports less than the high-water mark, that is never a meaningful advance (must not
        // reset the stall clock).
        var highWater = TimeSpan.FromSeconds(100);
        var current = TimeSpan.FromSeconds(90);
        Assert.False(CleanupLiveness.CpuAdvancedMeaningfully(highWater, current, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void LogAdvanced_true_only_when_the_newest_write_moved_forward()
    {
        var t0 = new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        // Advanced — the CBS log got a newer write since the prior tick.
        Assert.True(CleanupLiveness.LogAdvanced(t0, t0 + TimeSpan.FromSeconds(1)));

        // Same — no new write this tick.
        Assert.False(CleanupLiveness.LogAdvanced(t0, t0));

        // Regressed — a flaky/rollover read reporting an older max is never "advanced".
        Assert.False(CleanupLiveness.LogAdvanced(t0, t0 - TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(true, false, true)]    // CPU-only → working
    [InlineData(false, true, true)]    // log-only → working
    [InlineData(true, true, true)]     // both → working
    [InlineData(false, false, false)]  // NEITHER → not working (the only path to a stall)
    public void IsWorking_is_the_load_bearing_OR(bool stackCpuAdvanced, bool logAdvanced, bool expected)
    {
        Assert.Equal(expected, CleanupLiveness.IsWorking(stackCpuAdvanced, logAdvanced));
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
