using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Locks the timing and stand-down rules of the post-Force-reboot fast watch — the path that lets a machine
/// which reboots faster than the monitor can see still resolve its row.
/// <para>
/// The PROOF itself is not re-tested here: the fast watch reuses <see cref="ReadyConfirmation"/>, whose
/// boot-time-advance-past-the-margin rule is already covered end to end by <c>ReadyConfirmationTests</c>
/// (newer boot time confirms, drift inside the margin does not, same boot time is a flicker not a reboot, no
/// baseline never confirms). That reuse is the point — there is no second implementation to test.
/// </para>
/// <para>New behaviour, not regression guards: none of this existed before, so none of it can fail against
/// an older implementation of the same rule.</para>
/// </summary>
public class FastRebootWatchTests
{
    // The monitor's own numbers, restated here because they are the reason this watch exists and they live in
    // the Desktop project (unreachable from net10.0 tests): 20s cadence, 2 consecutive failures to believe an
    // offline, so ~40s before an outage is reliably visible.
    private static readonly TimeSpan MonitorCadence = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MonitorDetectionFloor = TimeSpan.FromSeconds(40);

    [Fact]
    public void The_watch_polls_far_faster_than_the_monitor_it_exists_to_cover_for()
    {
        // A watch at or above the monitor's own cadence would reproduce the exact blind spot it is fixing.
        Assert.True(FastRebootWatch.Interval < MonitorCadence);
    }

    [Fact]
    public void The_window_outlasts_the_monitors_detection_floor()
    {
        // If the window closed before ~40s, a fast VM could still slip through unproven — the defect.
        Assert.True(FastRebootWatch.Window > MonitorDetectionFloor);
    }

    [Fact]
    public void The_cost_for_one_rebooted_row_is_bounded_and_small()
    {
        // The load claim made to the operator, locked: at most this many boot-time reads for ONE row, each
        // through the shared 8-slot throttle. Rows nobody rebooted start no watch and cost nothing.
        Assert.Equal(24, FastRebootWatch.MaxReads);
    }

    // The shared boot-time throttle's size, restated here because it lives in the Desktop project.
    private const int SharedBootTimeSlots = 8;

    [Fact]
    public void Bulk_reads_leave_a_RESERVATION_the_monitor_can_always_reach()
    {
        // The whole point: force-reboot work (baseline burst + every watch polling every 5s) must never be
        // able to hold all 8 shared slots, or the monitor's own transition-time read queues behind a wall of
        // waiters and its 20s tick slips — silently, with no error and no log line.
        Assert.True(FastRebootWatch.MaxConcurrentBulkReads < SharedBootTimeSlots);
        Assert.Equal(2, SharedBootTimeSlots - FastRebootWatch.MaxConcurrentBulkReads);
    }

    [Fact]
    public void The_cap_does_not_re_serialize_the_baseline_capture()
    {
        // Capturing baselines concurrently was a deliberate fix; a cap of 1 would undo it. Comfortably
        // parallel while still leaving the reservation above.
        Assert.True(FastRebootWatch.MaxConcurrentBulkReads > 1);
    }

    [Theory]
    [InlineData(false, true)]   // monitor confirmed the box offline -> the normal transition owns the row
    [InlineData(true, false)]   // still online as far as the monitor knows -> keep watching
    [InlineData(null, false)]   // never probed -> no evidence to defer to
    public void The_watch_stands_down_only_once_the_monitor_has_seen_the_drop(bool? isOnline, bool expected)
        => Assert.Equal(expected, FastRebootWatch.ShouldStandDown(isOnline));

    [Fact]
    public void Standing_down_at_the_fast_window_is_NOT_giving_up()
    {
        // Operator decision: hand back at the fast window, but do not call the box failed there — a slow box
        // may still be legitimately committing. Giving up waits for the wave's forced go-offline window.
        TimeSpan forcedWindow = TimeSpan.FromMinutes(16);

        Assert.False(FastRebootWatch.ShouldGiveUp(FastRebootWatch.Window, forcedWindow));
    }

    [Fact]
    public void A_box_that_never_reboots_is_given_up_on_at_the_forced_window()
    {
        TimeSpan forcedWindow = TimeSpan.FromMinutes(16);

        Assert.False(FastRebootWatch.ShouldGiveUp(forcedWindow - TimeSpan.FromSeconds(1), forcedWindow));
        Assert.True(FastRebootWatch.ShouldGiveUp(forcedWindow, forcedWindow));
        Assert.True(FastRebootWatch.ShouldGiveUp(forcedWindow + TimeSpan.FromMinutes(5), forcedWindow));
    }
}
