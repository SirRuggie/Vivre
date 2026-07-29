namespace Vivre.Core.Updates;

/// <summary>
/// The CLOCK-IMMUNE test for "did this box reboot?", for the case where nobody ever saw it go offline.
/// <para>
/// <b>Why not compare boot times.</b> <c>LastBootUpTime</c> is derived from the target's wall clock, so a
/// clock STEP on the target — an NTP correction on a drifted VM, a manual set, a forward DST jump — moves it
/// without any reboot. Comparing two raw boot-time readings therefore reports a reboot that never happened
/// once the step exceeds the margin. That is tolerable when a drop off the network was also observed; it is
/// NOT tolerable when the boot time is the only evidence and the box was reachable the whole time, which is
/// exactly the fast-watch case.
/// </para>
/// <para>
/// <b>What is immune.</b> <see cref="BootTimeReading"/> takes <c>LocalDateTime</c> and <c>LastBootUpTime</c>
/// from the target in ONE query, so a clock step moves both and their difference — the uptime — is unchanged.
/// If the box never rebooted, <c>current.Uptime ≈ baseline.Uptime + elapsed</c> and the drop is near zero. A
/// real reboot collapses <c>current.Uptime</c> to seconds, so the drop is the whole pre-reboot session —
/// always far past <see cref="RebootWave.UptimeProofMargin"/>, which only has to cover read jitter.
/// </para>
/// <para>
/// This is the same rule the reboot wave uses to rescue a box it never saw drop; it lives here so the fast
/// watch uses the rule rather than a second, weaker one.
/// </para>
/// </summary>
public static class UptimeRebootProof
{
    /// <summary>
    /// Whether <paramref name="current"/> proves a reboot since <paramref name="baseline"/> was taken.
    /// Null on either side is NOT proof — an unreadable box tells us nothing, and must never confirm.
    /// </summary>
    public static bool IsProven(BootTimeReading? baseline, BootTimeReading? current, TimeSpan elapsedSinceBaseline)
    {
        if (baseline is null || current is null)
        {
            return false;
        }

        TimeSpan expectedIfNeverRebooted = baseline.Uptime + elapsedSinceBaseline;
        return expectedIfNeverRebooted - current.Uptime > RebootWave.UptimeProofMargin;
    }
}
