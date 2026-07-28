namespace Vivre.Core.Updates;

/// <summary>
/// Guards the post-reboot verify arc's VERDICT against being overwritten by a stale one.
/// <para>
/// <b>Why this exists.</b> The arc used to be awaited inside the monitor's per-row work, so the arc and the
/// monitor's reboot-pending probe could never interleave — whatever the arc concluded was, by construction,
/// the newest thing known about the row. Detaching the arc from the monitor's critical path (so one slow
/// verify can no longer freeze an entire tab) removes that guarantee: the monitor keeps probing on its 20 s
/// cadence while a detached arc is still running, so an arc that started minutes ago can finish holding a
/// reboot-pending answer that a later probe has already superseded. Writing it anyway would put a stale
/// verdict on the row — the same silent-wrong-state family as the freeze this whole arc has been about.
/// </para>
/// <para>
/// <b>Mechanism: a monotonic per-host generation, not a timestamp.</b> The monitor bumps a counter every
/// time IT writes <c>RebootRequired</c>; the arc captures the counter before it starts and compares before
/// it writes its verdict. A counter is used rather than a captured <see cref="System.DateTime"/> compared
/// against a last-probed marker because the marker the monitor already keeps
/// (<c>_lastRebootProbeAt</c>) is stamped when the PROBE returns — which, with the arc detached, happens
/// immediately — so every arc would see it advanced and every verdict would be discarded. The generation
/// moves only on an actual competing write, which is exactly the event that makes a verdict stale.
/// </para>
/// <para>
/// Equality, not ordering: any bump at all means a newer probe landed. The arc never bumps the counter
/// itself, so it cannot invalidate its own verdict.
/// </para>
/// <para>
/// UI-free so <c>Vivre.Core.Tests</c> (net10.0) can cover it — the caller lives in the net10.0-windows
/// Desktop project and is unreachable from the test project.
/// </para>
/// </summary>
public static class ArcResultFreshness
{
    /// <summary>
    /// Whether a verdict captured at <paramref name="generationAtStart"/> may still be written, given the
    /// host's current generation. False once ANY newer monitor probe has written the row's reboot state.
    /// </summary>
    public static bool IsCurrent(long generationAtStart, long generationNow) =>
        generationAtStart == generationNow;

    /// <summary>
    /// As <see cref="IsCurrent(long, long)"/>, plus: a row an operation has CLAIMED since the arc started is
    /// never current. Detaching the arc widened the window in which it can write a row a sweep now owns —
    /// the claim is checked at start (<c>ShouldRunVerify</c>'s <c>IsPatching</c> leg) but nothing re-checked
    /// it afterwards, so a Check All / Check Vitals begun mid-arc could have its results overwritten by an
    /// arc that started before it. A claimed row is treated exactly like a superseded one: discard, log, and
    /// leave the row to its owner.
    /// </summary>
    public static bool IsCurrent(long generationAtStart, long generationNow, bool rowClaimed) =>
        IsCurrent(generationAtStart, generationNow) && !rowClaimed;

    /// <summary>The line emitted when a second verify arc is skipped because one is already running.</summary>
    public static string AlreadyRunningLine(string host) =>
        $"{host}: a post-reboot verify is already running — not starting a second one.";

    /// <summary>The one activity line a discarded verdict emits. The caller supplies the tab tag as origin.</summary>
    public static string StaleLine(string host) =>
        $"{host}: post-reboot verify finished after a newer reboot check — discarded its result rather than overwrite fresher state.";
}
