namespace Vivre.Core.Updates;

/// <summary>
/// The timing and stand-down rules for the post-Force-reboot fast watch — the path that lets a machine which
/// reboots faster than the monitor can see still resolve its row.
/// <para>
/// <b>Why it exists.</b> The monitor probes every 20 s and needs <c>OfflineConfirmThreshold</c> = 2
/// CONSECUTIVE failed probes to believe a box is down, so its detection floor is ~40 s. A VM that reboots in
/// 10–20 s is down and back between two probes and never flips <c>IsOnline</c> — and EVERY downstream write
/// (Last status, Last reboot, the reboot message, the recheck budget, the degraded flag, the activity-log
/// record) hangs off that offline→online transition. Field-reproduced twice on 2026-07-28: Export-VFP came
/// back in ~20 s and its row sat on "Reboot forced — going down" indefinitely, never self-correcting.
/// </para>
/// <para>
/// <b>The fix is observational only.</b> The reboot the operator already ordered is unchanged; this only
/// changes how it is WATCHED. Proof is <see cref="UptimeRebootProof"/> — the same clock-immune uptime test
/// the wave uses to rescue a box it never saw drop — rather than an observed drop. An uptime that RESET means
/// the box rebooted whether or not anyone saw it go, and unlike a raw boot-time comparison it cannot be faked
/// by the target's clock being corrected mid-watch.
/// </para>
/// <para>
/// <b>Scope: only the blind window.</b> The monitor handles slow reboots correctly today (a ~90 s physical
/// reboot tracks perfectly), so the fast watch covers only the interval where the monitor is structurally
/// blind — <see cref="Window"/> at <see cref="Interval"/>. Past that it stands down and the normal transition
/// owns the row. Cost is bounded and per-row: one rebooted row costs at most
/// <c>Window / Interval</c> boot-time reads, and rows nobody rebooted cost nothing at all.
/// </para>
/// </summary>
public static class FastRebootWatch
{
    /// <summary>How often the rebooted row's boot time is re-read. Well under the monitor's 20 s cadence —
    /// that gap is the whole defect — but not so tight that a 2-core box notices for one row.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <summary>How long the fast watch runs before standing down. Sized to cover the monitor's blind spot
    /// (~40 s detection floor) with room for a slow-booting VM, not to cover every reboot: past this the
    /// monitor's own transition is reliable and owns the row.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How often the give-up backstop re-checks whether someone else has taken the row. It waits out the
    /// wave's forced go-offline window, and doing that as ONE long sleep held the per-host watch claim for
    /// the whole window even when the monitor resolved the row seconds in — which blocked a later Force
    /// reboot's watch. These are in-memory field reads, so slicing is free; matched to the monitor's own
    /// cadence because the monitor is what it is waiting to hear from.
    /// </summary>
    public static readonly TimeSpan GiveUpPollInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How many boot-time reads the force-reboot paths (baseline capture + the watch polls) may have in
    /// flight at once, out of the 8-slot shared boot-time throttle. The remainder is a RESERVATION for the
    /// monitor's own transition-time read, which must never queue behind a bulk burst.
    /// <para>
    /// Why a reservation and not jitter: jitter only spreads a burst statistically — with 50+ rows the
    /// aggregate demand still exceeds the pool and the monitor can still land behind a wall of waiters.
    /// A cap is a HARD bound: bulk work can never hold more than this many slots, so the monitor's worst
    /// case is one read's duration no matter how many rows were force-rebooted. Same background/total shape
    /// <c>HostWinRmGate</c> already uses, for the same reason.
    /// </para>
    /// </summary>
    public static int MaxConcurrentBulkReads => 6;

    /// <summary>Reads this row can take before the window is spent — the added load for ONE rebooted row.</summary>
    public static int MaxReads => (int)(Window.Ticks / Interval.Ticks);

    /// <summary>
    /// Whether the fast watch should stop because the MONITOR has taken ownership: it saw the box actually go
    /// offline, so the normal offline→online transition will write every field correctly and a second writer
    /// would only race it.
    /// </summary>
    public static bool ShouldStandDown(bool? isOnline) => isOnline == false;

    /// <summary>
    /// Whether enough time has passed with NO drop observed and NO boot-time advance that the row must be
    /// landed honestly rather than left mid-progress. Deliberately the wave's own forced go-offline window,
    /// not <see cref="Window"/>: standing down at 2 minutes is not the same as giving up, and a genuinely
    /// slow box must not be called failed while it is still legitimately committing.
    /// </summary>
    public static bool ShouldGiveUp(TimeSpan sinceDispatch, TimeSpan forcedGoOfflineWindow) =>
        sinceDispatch >= forcedGoOfflineWindow;
}
