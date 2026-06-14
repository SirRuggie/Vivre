namespace Vivre.Core.Updates;

/// <summary>
/// The Server 2016 post-reboot confirmation strategy: reads the host's build/UBR and delegates to
/// <see cref="FullPackageLcuLane.Decide"/> — the same UBR rule used by the standalone Verify
/// action, so the wave and Verify can't drift. Maps <see cref="LcuVerifyOutcome"/> onto
/// <see cref="RebootConfirmationOutcome"/> with the messages preserved verbatim.
/// </summary>
internal sealed class UbrConfirmation(ILcuBuildReader builds, int targetUbr) : IPostRebootConfirmation
{
    public async Task<RebootConfirmationResult> ConfirmAsync(string host, CancellationToken cancellationToken)
    {
        (int? build, int? ubr) = await builds.ReadAsync(host, cancellationToken).ConfigureAwait(false);
        LcuVerifyResult v = FullPackageLcuLane.Decide(host, build, ubr, targetUbr);

        return v.Outcome switch
        {
            LcuVerifyOutcome.Verified    => new RebootConfirmationResult(RebootConfirmationOutcome.Confirmed, v.Message),
            LcuVerifyOutcome.WrongBuild  => new RebootConfirmationResult(RebootConfirmationOutcome.Failed,    v.Message),
            LcuVerifyOutcome.Unreachable => new RebootConfirmationResult(RebootConfirmationOutcome.NotReady,  v.Message),
            _                            => new RebootConfirmationResult(RebootConfirmationOutcome.NotReady,  v.Message),
        };
    }
}
