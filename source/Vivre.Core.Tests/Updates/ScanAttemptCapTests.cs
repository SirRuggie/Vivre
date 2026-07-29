using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Locks who gets a per-attempt ceiling on the post-reboot rescan. The rescan loop is shared by the monitor's
/// detached verify arc and the reboot wave, and they need opposite things.
/// <para>
/// These cover the RULE, not the wiring: the loop itself lives in the net10.0-windows Desktop project and is
/// unreachable from this net10.0 test project. Nothing here proves the call sites pass the right flag.
/// </para>
/// </summary>
public class ScanAttemptCapTests
{
    private const int CapSeconds = 300;

    [Fact]
    public void The_reboot_wave_is_UNCAPPED()
    {
        // The wave awaits this loop inline on a multi-hour per-host budget. A staged 2016 box working through
        // a CU backlog can legitimately run past 300s, and capping it would land it Unverified — a regression
        // in the lane built for exactly those boxes. Null means "use the caller's own token, unchanged".
        Assert.Null(ScanAttemptCap.For(fromDetachedArc: false, CapSeconds));
    }

    [Fact]
    public void The_monitors_detached_arc_IS_capped()
    {
        Assert.Equal(TimeSpan.FromSeconds(CapSeconds), ScanAttemptCap.For(fromDetachedArc: true, CapSeconds));
    }

    [Fact]
    public void The_cap_is_inert_while_it_equals_the_arcs_own_ceiling()
    {
        // Honest guard, not a passing grade. The arc ceiling is armed BEFORE the per-attempt cap and both are
        // 5 minutes, so the arc ceiling always trips first and the per-attempt branch cannot be reached. This
        // asserts that state of affairs so it is visible rather than implied — if someone later changes either
        // value, this test's premise changes with it and the cap becomes real.
        TimeSpan? monitorCap = ScanAttemptCap.For(fromDetachedArc: true, CapSeconds);

        Assert.Equal(VerifyArcTimeout.Ceiling, monitorCap);
    }
}
