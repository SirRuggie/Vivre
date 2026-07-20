namespace Vivre.Core.Updates;

/// <summary>
/// Pure (no I/O, no side effects) decision for whether the background monitor may self-heal a row
/// that reads <see cref="PatchPhase.Unverified"/> back to green <see cref="PatchPhase.Done"/> after a
/// later reboot-pending probe answers definitively clean.
/// </summary>
public static class MonitorSelfHeal
{
    /// <summary>
    /// The razor: a row heals ONLY when all three hold at write time —
    /// <list type="number">
    ///   <item>the probe DEFINITIVELY answered <c>false</c> (a reboot is confirmed not pending) —
    ///     unknown (<c>null</c>, the Kerberos/timeout cohort) NEVER heals, and <c>true</c> never heals
    ///     (the caller's pending guard upgrades that to amber instead);</item>
    ///   <item>the row's Unverified state is probe-only (<paramref name="probeOnlyUnverified"/>) — the
    ///     applicability rescan had already come back clean, so the reboot was the only unconfirmed thing;</item>
    ///   <item>the row STILL reads Unverified right now (<paramref name="updatePhase"/>) — a live re-check so a
    ///     concurrent scan that moved the phase since the marker was set suppresses the heal.</item>
    /// </list>
    /// Only the "couldn't confirm reboot state" variant (variant A) can satisfy all three; couldn't-rescan
    /// and scan-failed variants leave <paramref name="probeOnlyUnverified"/> false and can never reach green here.
    /// </summary>
    /// <param name="updatePhase">The row's current <see cref="Computer"/>.UpdatePhase string, read live at write time.</param>
    /// <param name="probeOnlyUnverified">The row's UnverifiedRebootProbeOnly marker — true only for the probe-only variant.</param>
    /// <param name="probeResult">Tri-state reboot-pending result: false = confirmed clean, true = confirmed pending, null = couldn't answer.</param>
    public static bool ShouldSelfHeal(string? updatePhase, bool probeOnlyUnverified, bool? probeResult) =>
        probeResult == false
        && probeOnlyUnverified
        && string.Equals(updatePhase, PatchPhase.Unverified.ToString(), StringComparison.Ordinal);
}
