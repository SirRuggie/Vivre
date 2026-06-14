namespace Vivre.Core.Updates;

/// <summary>
/// A permissive <see cref="IRebootReadinessProbe"/> for operator-ordered reboots on non-2016 boxes,
/// where the pre-reboot guard does not need to verify the 2016-specific TrustedInstaller + CBS
/// RebootPending signals.
///
/// <para><b>Why "ready" unconditionally is safe here:</b>
/// The readiness probe is PRE-reboot (may we reboot?). Proof the reboot actually FIRED is the
/// separate POST-reboot offline-detection (<c>RebootWave.WaitForOfflineAsync</c>). Relaxing this
/// pre-reboot gate does NOT weaken the fired-reboot guarantee. The strict
/// <see cref="DcomRebootReadinessProbe"/> stays for staged-2016 boxes (it also guards against
/// rebooting into the 2-hour TrustedInstaller Stopping hang).</para>
/// </summary>
public sealed class BasicReachabilityReadinessProbe : IRebootReadinessProbe
{
    /// <inheritdoc/>
    public Task<RebootReadiness> CheckAsync(string host, CancellationToken cancellationToken) =>
        Task.FromResult(new RebootReadiness(IsReady: true, "Ready (operator-ordered reboot)"));
}
