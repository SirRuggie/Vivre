using Vivre.Core.Models;
using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

public class ForceRebootDispatchEvidenceTests
{
    [Fact]
    public void Uncertain_dispatch_cannot_enter_verify_until_restart_evidence_arrives()
    {
        var row = new Computer("BOX") { ForceRebootAwaitingVerify = true };

        ForceRebootDispatchEvidence.BeginUncertain(row);

        Assert.True(row.ForceRebootAwaitingRestartEvidence);
        Assert.False(row.ForceRebootAwaitingVerify);
        Assert.Equal(PatchPhase.Unverified.ToString(), row.UpdatePhase);
        Assert.False(ForceRebootVerifyGate.ShouldRunVerify(
            row.ForceRebootAwaitingVerify, isPatching: false, rebootPending: false));

        ForceRebootDispatchEvidence.RecordRestartEvidence(row);

        Assert.False(row.ForceRebootAwaitingRestartEvidence);
        Assert.True(row.ForceRebootAwaitingVerify);
        Assert.True(ForceRebootVerifyGate.ShouldRunVerify(
            row.ForceRebootAwaitingVerify, isPatching: false, rebootPending: false));
    }

    [Fact]
    public void Expiry_preserves_uncertain_evidence_guard_and_disarms_verify()
    {
        var row = new Computer("BOX");
        ForceRebootDispatchEvidence.BeginUncertain(row);

        ForceRebootDispatchEvidence.ExpireUnverified(row);

        Assert.True(row.ForceRebootAwaitingRestartEvidence);
        Assert.False(row.ForceRebootAwaitingVerify);
        Assert.Contains("no restart could be confirmed", row.RebootMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Proof_from_an_older_overlapping_force_attempt_cannot_resolve_the_new_attempt()
    {
        var row = new Computer("BOX");
        int first = ForceRebootDispatchEvidence.BeginDispatch(row);
        ForceRebootDispatchEvidence.BeginUncertain(row);
        int second = ForceRebootDispatchEvidence.BeginDispatch(row);
        ForceRebootDispatchEvidence.BeginUncertain(row);

        ForceRebootDispatchEvidence.RecordRestartEvidence(row, first);

        Assert.True(row.ForceRebootAwaitingRestartEvidence);
        Assert.False(row.ForceRebootAwaitingVerify);

        ForceRebootDispatchEvidence.RecordRestartEvidence(row, second);

        Assert.False(row.ForceRebootAwaitingRestartEvidence);
        Assert.True(row.ForceRebootAwaitingVerify);
    }

    [Fact]
    public void Completion_from_an_older_overlapping_dispatch_is_stale()
    {
        var row = new Computer("BOX");
        int first = ForceRebootDispatchEvidence.BeginDispatch(row);
        int second = ForceRebootDispatchEvidence.BeginDispatch(row);

        Assert.False(ForceRebootDispatchEvidence.IsCurrentDispatch(row, first));
        Assert.True(ForceRebootDispatchEvidence.IsCurrentDispatch(row, second));
    }
}
