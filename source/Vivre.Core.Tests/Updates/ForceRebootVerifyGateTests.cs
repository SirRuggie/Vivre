using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Locks the tiny razor the background monitor uses to decide whether a hand Force reboot rejoins the
/// post-reboot verify arc: run the shared outcome step ONLY when the marker is armed, no sweep owns the row,
/// AND the reboot-pending probe answered DEFINITIVELY clean. true/null probe answers and a sweep-owned row
/// never trigger it — the honest-unknown rule.
/// </summary>
public class ForceRebootVerifyGateTests
{
    [Fact]
    public void Armed_not_patching_and_definitely_clean_runs_verify() =>
        Assert.True(ForceRebootVerifyGate.ShouldRunVerify(awaitingVerify: true, isPatching: false, rebootPending: false));

    [Fact]
    public void Not_armed_never_runs() =>
        Assert.False(ForceRebootVerifyGate.ShouldRunVerify(awaitingVerify: false, isPatching: false, rebootPending: false));

    [Fact]
    public void Sweep_owns_the_row_skips_without_running() =>
        // IsPatching true → skip; the caller leaves the marker set so a later clean transition retries.
        Assert.False(ForceRebootVerifyGate.ShouldRunVerify(awaitingVerify: true, isPatching: true, rebootPending: false));

    [Fact]
    public void Probe_still_pending_does_not_run() =>
        Assert.False(ForceRebootVerifyGate.ShouldRunVerify(awaitingVerify: true, isPatching: false, rebootPending: true));

    [Fact]
    public void Probe_couldnt_answer_null_does_not_run() =>
        // null = Kerberos/timeout/unreadable — never treated as a clean return.
        Assert.False(ForceRebootVerifyGate.ShouldRunVerify(awaitingVerify: true, isPatching: false, rebootPending: null));

    [Theory]
    // Only the exact armed + idle + definite-clean triple runs; every other combination is false.
    [InlineData(false, false, null)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, null)]
    public void All_other_combinations_are_false(bool awaiting, bool patching, bool? pending) =>
        Assert.False(ForceRebootVerifyGate.ShouldRunVerify(awaiting, patching, pending));
}
