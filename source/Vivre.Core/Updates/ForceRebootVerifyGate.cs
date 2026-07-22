namespace Vivre.Core.Updates;

/// <summary>
/// Pure (no I/O, no side effects) decision for whether the background monitor should run the shared
/// post-reboot verify step for a box that was rebooted by the operator's standalone <b>Force reboot</b>
/// (as opposed to the Reboot Wave, whose Done arm runs that step inline).
/// </summary>
/// <remarks>
/// Sits beside <see cref="MonitorSelfHeal"/>: both are tiny razors the monitor's reboot-pending probe
/// consults so the wiring stays readable and the rule is unit-testable in isolation.
/// </remarks>
public static class ForceRebootVerifyGate
{
    /// <summary>
    /// True only when all three hold — so the monitor runs the post-reboot recheck ONCE for a hand Force reboot:
    /// <list type="number">
    ///   <item><paramref name="awaitingVerify"/> — the Force-reboot path armed the one-shot marker
    ///     (<c>Computer.ForceRebootAwaitingVerify</c>);</item>
    ///   <item><paramref name="isPatching"/> is <see langword="false"/> — no sweep owns this row; if a sweep
    ///     (a wave/install) owns it, SKIP without clearing the marker so a later clean transition retries;</item>
    ///   <item>the reboot-pending probe answered DEFINITIVELY clean (<paramref name="rebootPending"/> ==
    ///     <see langword="false"/>) — <see langword="true"/> (still pending) and <see langword="null"/>
    ///     (couldn't answer — Kerberos/timeout) NEVER trigger the verify, mirroring the honest-unknown rule.</item>
    /// </list>
    /// The caller must clear the marker BEFORE awaiting the verify step (single-shot) so a concurrent monitor
    /// tick can't double-enter.
    /// </summary>
    /// <param name="awaitingVerify">The row's <c>ForceRebootAwaitingVerify</c> marker.</param>
    /// <param name="isPatching">The row's <c>IsPatching</c> flag — true when a sweep holds the row.</param>
    /// <param name="rebootPending">Tri-state reboot-pending result: false = confirmed clean, true = confirmed
    /// pending, null = couldn't answer.</param>
    public static bool ShouldRunVerify(bool awaitingVerify, bool isPatching, bool? rebootPending) =>
        awaitingVerify && !isPatching && rebootPending == false;
}
