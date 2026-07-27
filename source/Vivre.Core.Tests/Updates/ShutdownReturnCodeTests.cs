using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Locks the pure razor over the DCOM reboot method's return code
/// (<see cref="ShutdownReturnCode.Classify"/>). The codes that matter operationally — 1115 (a shutdown is
/// already underway) and 1191 (Windows refused the GRACEFUL form because a session exists) — only appear on
/// a live box in rare states, so this is the only place their branches are provable.
/// </summary>
public class ShutdownReturnCodeTests
{
    [Fact]
    public void Zero_is_Accepted()
    {
        // An accepted call always answers with an explicit 0 — the box is going down.
        Assert.Equal(ShutdownCallOutcome.Accepted, ShutdownReturnCode.Classify(0));
    }

    [Fact]
    public void Code_1115_is_AlreadyInProgress()
    {
        // ERROR_SHUTDOWN_IN_PROGRESS — the box is already going offline on its own, so it is not a failure
        // and must never provoke a second reboot.
        Assert.Equal(ShutdownCallOutcome.AlreadyInProgress, ShutdownReturnCode.Classify(1115));
        Assert.Equal(ShutdownCallOutcome.AlreadyInProgress,
            ShutdownReturnCode.Classify(ShutdownReturnCode.AlreadyInProgress));
    }

    [Fact]
    public void Code_1191_is_GracefulRefused_not_a_transport_failure()
    {
        // ERROR_SHUTDOWN_USERS_LOGGED_ON — a session exists (Active OR disconnected), so Windows refused the
        // graceful form only. The channel is healthy: authentication and the invocation both succeeded, which
        // is why this is classified apart from Failed rather than as a Kerberos/SPN symptom.
        Assert.Equal(ShutdownCallOutcome.GracefulRefused, ShutdownReturnCode.Classify(1191));
        Assert.Equal(ShutdownCallOutcome.GracefulRefused,
            ShutdownReturnCode.Classify(ShutdownReturnCode.UsersLoggedOn));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(5u)]
    [InlineData(87u)]
    [InlineData(1190u)]
    [InlineData(1192u)]
    [InlineData(uint.MaxValue)]
    public void Any_other_non_zero_code_is_Failed(uint code)
    {
        Assert.Equal(ShutdownCallOutcome.Failed, ShutdownReturnCode.Classify(code));
    }

    [Fact]
    public void The_1191_match_is_exact_and_not_a_range()
    {
        // The neighbours must stay ordinary failures — a range match would silently reclassify unrelated
        // codes as "the graceful form was refused" once a later chunk gives that outcome its own escalation.
        Assert.Equal(ShutdownCallOutcome.Failed, ShutdownReturnCode.Classify(1190));
        Assert.Equal(ShutdownCallOutcome.Failed, ShutdownReturnCode.Classify(1192));
    }

    [Fact]
    public void A_missing_result_code_is_NoResultCode_and_never_success()
    {
        // null = the method produced no result code at all. A missing result code must NEVER read as success:
        // an accepted call always answers with an explicit 0, so nothing here confirms a reboot happened.
        Assert.Equal(ShutdownCallOutcome.NoResultCode, ShutdownReturnCode.Classify(null));
        Assert.NotEqual(ShutdownCallOutcome.Accepted, ShutdownReturnCode.Classify(null));
    }

    // ── What a 1191 refusal means for the SMB/SCM fallback ───────────────────────
    // A 1191 is the OS itself refusing the GRACEFUL form. When DCOM then can't resolve the box (the forced
    // escalation failed, or the call was already forced), that knowledge must travel to the fallback: sending
    // the very form the box just refused, down a second channel, only gets refused again — and costs the wave
    // a second dispatch to fix. This is the FORM of an already-ordered reboot, never a decision to reboot.

    [Theory]
    [InlineData(false, false, false)] // graceful call, no 1191 (a throw/timeout — the Kerberos-broken path) → graceful, unchanged
    [InlineData(false, true, true)]   // graceful call the OS REFUSED with 1191 → the fallback must force
    [InlineData(true, false, true)]   // forced call → forced, unchanged
    [InlineData(true, true, true)]    // forced call the OS refused → forced
    public void The_smb_fallback_forces_exactly_when_the_os_refused_the_graceful_form(bool requested, bool gracefulRefusedByTheOs, bool expected)
    {
        Assert.Equal(expected, DcomRebootTrigger.FallbackForced(requested, gracefulRefusedByTheOs));
    }

    // ── What the fallback REPORTS, so the wave picks the right go-offline window ──
    // A box the fallback sent /f to is going down FORCED. If that were reported as a plain Issued, a wave
    // that had asked for graceful would time it on the SHORT graceful window and then spend a SECOND
    // dispatch forcing a box already executing a forced reboot — one operator click, two reboots.

    [Theory]
    [InlineData(false, false, RebootDispatch.Issued)]            // graceful asked, graceful sent → nothing escalated
    [InlineData(false, true, RebootDispatch.EscalatedToForced)]  // graceful asked, /f sent: the force came from the OS's refusal → forced window
    [InlineData(true, true, RebootDispatch.Issued)]              // the CALLER asked for forced → not an escalation; that leg is already on the forced window
    [InlineData(true, false, RebootDispatch.Issued)]             // (unreachable in production — FallbackForced never de-escalates a forced request)
    public void The_smb_fallback_reports_an_escalation_only_when_the_force_came_from_the_os(bool requested, bool sentForced, RebootDispatch expected)
    {
        Assert.Equal(expected, DcomRebootTrigger.FallbackDispatch(requested, sentForced));
    }

    [Fact]
    public void A_1191_refusal_that_reaches_the_fallback_sends_forced_AND_reports_the_forced_window()
    {
        // The two decisions composed, on the path that matters: an operator-ordered GRACEFUL reboot the OS
        // refused with 1191, which DCOM then couldn't resolve. The fallback must put /f on the wire AND tell
        // the wave the box is going down forced. Either half alone leaves a real gap.
        bool smbForced = DcomRebootTrigger.FallbackForced(requested: false, gracefulRefusedByTheOs: true);
        Assert.True(smbForced);
        Assert.Equal(RebootDispatch.EscalatedToForced, DcomRebootTrigger.FallbackDispatch(requested: false, sentForced: smbForced));
    }
}
