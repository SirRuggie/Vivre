namespace Vivre.Core.Net;

/// <summary>
/// Pure decisions that keep a powered-off box (whose BMC/iDRAC, reused IP, or DNS merely answers
/// ICMP) from reading as a management failure. Vivre equates "answers ping" with "online", so a box
/// that pings once then drops would otherwise look like a managed box that "went offline", and a scan
/// against an unreachable box would surface a scary WinRM/SMB remoting error. UI-free, so it lives in
/// Core and is unit-tested directly. See <see cref="ReachabilityConfirmation"/> for the blip-suppression rule.
/// </summary>
public static class ReachabilityGating
{
    /// <summary>
    /// Whether a box that just transitioned offline should be tracked for its return — the
    /// "Offline since HH:mm — waiting for it to come back…" reboot-wave signal. True ONLY when the box
    /// was genuinely online before (<paramref name="previousOnline"/> == true — not a first-ever-offline)
    /// AND was actually MANAGED over remoting this session (<paramref name="wasConfirmedOnline"/>): a
    /// health check, vitals pull, scan, install, reboot-pending probe, or reboot succeeded against it.
    /// A box that only ever answered ICMP ping (a powered-off server's management controller) is NOT
    /// "managed" and must read a calm "Offline" instead of a false went-offline event.
    /// </summary>
    public static bool ShouldTrackOfflineReturn(bool? previousOnline, bool wasConfirmedOnline) =>
        previousOnline == true && wasConfirmedOnline;

    /// <summary>
    /// Whether a scan should short-circuit to a calm "Offline" instead of attempting WinRM/SMB, given the
    /// row's known reachability. True ONLY for a <em>confirmed</em> offline box (<c>false</c>). A box that
    /// is online — even ping-only/unmanageable — returns <c>false</c> so its scan runs and surfaces the
    /// honest "Can't reach over WinRM or SMB" error. <c>null</c> (never probed) also returns <c>false</c>:
    /// the caller must take a reachability probe first and re-ask with the result, so a never-probed box
    /// can't slip a doomed remoting attempt through, nor be skipped without evidence it's actually down.
    /// </summary>
    public static bool ScanShouldShortCircuitOffline(bool? isOnline) => isOnline == false;

    /// <summary>
    /// Whether the Health sweep should SKIP the doomed WinRM health / vitals / custom-column probes on a box
    /// and mark it Offline directly, given a FRESH per-sweep reachability check. True ONLY when the box is
    /// unreachable by BOTH ICMP ping AND an ambient DCOM/WMI probe (the same identity the DCOM vitals
    /// fallback uses) — no transport those probes rely on can reach it, so attempting them only burns a
    /// ~20s WinRM open-timeout (and a vitals DCOM-fallback timeout) before failing. A box reachable by ping
    /// OR ambient DCOM (e.g. a Kerberos-broken box still readable over DCOM) is NOT skipped, so it still
    /// gets its health/vitals. Deliberately keyed on an AMBIENT DCOM probe, NOT on
    /// <c>IsOnline</c>/<c>ProbeReachabilityAsync</c> — whose DCOM leg is credential-gated and would
    /// false-negative a DCOM-reachable box on the ambient login, wrongly skipping it.
    /// </summary>
    public static bool ShouldSkipAsOffline(bool pingReachable, bool dcomReachable) =>
        !pingReachable && !dcomReachable;
}
