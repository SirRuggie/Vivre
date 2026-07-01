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
}
