namespace Vivre.Core.Net;

/// <summary>
/// The pure rule the monitor uses to decide whether a box is <em>really</em> offline before it
/// announces it. The continuous monitor probes reachability (ICMP, then an authenticated WMI/DCOM
/// fallback) every pass; under load a single probe can drop or time out on a perfectly healthy box.
/// Flipping a box offline on one failure produces false "Went offline → Back online" blips. This rule
/// requires a previously-online box to fail several <em>consecutive</em> probes before it is declared
/// offline; a box that was already offline (or never seen) flips on the first failure, since there is
/// no blip to suppress. UI-free, so it lives in Core and is unit-tested directly.
/// </summary>
public static class ReachabilityConfirmation
{
    /// <summary>
    /// Whether the monitor should treat a box as online given the raw reachability result and how many
    /// times it has failed in a row (counting the current failure). A previously-online box survives up
    /// to <paramref name="threshold"/> − 1 consecutive failures (a transient dropped ping / busy WMI
    /// under load) before being declared offline.
    /// </summary>
    /// <param name="previous">The box's prior online state (<see langword="null"/> = never probed yet).</param>
    /// <param name="rawOnline">The raw result of the reachability probe just run.</param>
    /// <param name="consecutiveFailures">Consecutive failed probes including this one (0 when online).</param>
    /// <param name="threshold">Consecutive failures required to declare a previously-online box offline.</param>
    public static bool ConfirmEffectiveOnline(bool? previous, bool rawOnline, int consecutiveFailures, int threshold)
    {
        if (rawOnline)
        {
            return true;
        }

        // Only a previously-online box gets the benefit of the doubt; require `threshold` consecutive fails.
        return previous == true && consecutiveFailures < threshold;
    }
}
