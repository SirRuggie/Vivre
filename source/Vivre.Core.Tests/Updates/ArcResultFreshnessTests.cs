using Vivre.Core.Models;
using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Locks the staleness guard that makes detaching the verify arc from the monitor pass safe.
/// <para>
/// New behaviour, not a regression guard: before the arc was detached it could not race the monitor at all,
/// so there was no rule here to regress. The failure these prevent is a slow arc landing a reboot verdict
/// that a newer probe already superseded.
/// </para>
/// </summary>
public class ArcResultFreshnessTests
{
    // The monitor's counter, modelled exactly as the view model keeps it: bumped ONLY when the monitor's own
    // probe writes RebootRequired, never by the arc.
    private long _generation;

    private long CaptureAtArcStart() => _generation;

    private void MonitorProbeWritesRebootState() => _generation++;

    [Fact]
    public void A_verdict_is_current_when_no_probe_landed_while_the_arc_ran()
    {
        long captured = CaptureAtArcStart();

        Assert.True(ArcResultFreshness.IsCurrent(captured, _generation));
    }

    [Fact]
    public void A_verdict_is_STALE_once_a_newer_probe_wrote_the_reboot_state()
    {
        long captured = CaptureAtArcStart();

        MonitorProbeWritesRebootState(); // the arc is still running; a 20s pass overtakes it

        Assert.False(ArcResultFreshness.IsCurrent(captured, _generation));
    }

    [Fact]
    public void Repeated_probes_keep_the_verdict_stale_rather_than_wrapping_back_to_current()
    {
        // Equality, not ordering: a long-running arc that is overtaken many times must never come back
        // around to "current". A counter that was compared with < or that reset would do exactly that.
        long captured = CaptureAtArcStart();

        for (int i = 0; i < 50; i++)
        {
            MonitorProbeWritesRebootState();
            Assert.False(ArcResultFreshness.IsCurrent(captured, _generation));
        }
    }

    [Fact]
    public void An_arc_that_is_never_overtaken_stays_current_no_matter_how_long_it_runs()
    {
        // The arc must not invalidate ITSELF — it never bumps the counter, so elapsed time alone is not
        // staleness. This is why the mechanism is a generation and not a captured timestamp.
        long captured = CaptureAtArcStart();

        Assert.True(ArcResultFreshness.IsCurrent(captured, _generation));
        Assert.True(ArcResultFreshness.IsCurrent(captured, _generation));
    }

    [Fact]
    public void A_stale_verdict_is_discarded_before_it_can_touch_the_row()
    {
        // Mirrors the guard's placement: the check runs BEFORE any outcome write, so a row overtaken by a
        // newer probe keeps that probe's state. Here the newer probe said "still pending"; a stale arc
        // carrying "clean" must not turn that into a green, cleared row.
        var row = new Computer("BOX01") { RebootRequired = true };
        long captured = CaptureAtArcStart();

        MonitorProbeWritesRebootState();

        if (ArcResultFreshness.IsCurrent(captured, _generation))
        {
            row.RebootRequired = false;   // what the stale arc would have written
            row.UpdatePhase = PatchPhase.Done.ToString();
        }

        Assert.True(row.RebootRequired);
        Assert.NotEqual(PatchPhase.Done.ToString(), row.UpdatePhase);
    }

    [Fact]
    public void The_stale_line_names_the_host_and_says_the_result_was_discarded()
    {
        string line = ArcResultFreshness.StaleLine("NYC-FP1");

        Assert.Contains("NYC-FP1", line, StringComparison.Ordinal);
        Assert.Contains("discarded", line, StringComparison.OrdinalIgnoreCase);
    }
}
