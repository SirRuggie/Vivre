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
/// changes how it is WATCHED. Proof comes from the same boot-time evidence the wave already uses
/// (<see cref="ReadyConfirmation"/> + <see cref="RebootWave.UptimeProofMargin"/>) rather than from observing
/// a drop — a boot time that advanced past the margin means the box rebooted whether or not anyone saw it go.
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
