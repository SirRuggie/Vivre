namespace Vivre.Core.Updates;

/// <summary>
/// Who gets a per-attempt ceiling on the post-reboot rescan, and who must not.
/// <para>
/// The rescan loop is shared by two callers with opposite needs. The MONITOR's detached verify arc runs on a
/// bounded budget and must never sit on a single unbounded attempt. The REBOOT WAVE awaits the same loop
/// inline on a multi-hour per-host budget, and a staged 2016 box working through a CU backlog can
/// legitimately take longer than the cap — cutting it short would land it Unverified, a regression in the
/// very lane built for those boxes. So the cap is scoped to the arc, not to the method.
/// </para>
/// <para>
/// <b>Honest note on effectiveness.</b> On the monitor path this cap is currently DOCUMENTATION, not
/// mechanism: the arc's own ceiling (<see cref="VerifyArcTimeout.Ceiling"/>, 5 min) equals the per-attempt
/// cap and is armed EARLIER, so the arc ceiling always trips first and the per-attempt branch is
/// unreachable. It becomes live the moment those two values differ — which is exactly why the rule is
/// written down and tested rather than left implicit.
/// </para>
/// </summary>
public static class ScanAttemptCap
{
    /// <summary>
    /// The per-attempt ceiling for this caller, or <see langword="null"/> for "no cap — use the caller's own
    /// token unchanged". Null is the wave's pre-existing behaviour and must stay that way.
    /// </summary>
    public static TimeSpan? For(bool fromDetachedArc, int capSeconds) =>
        fromDetachedArc ? TimeSpan.FromSeconds(capSeconds) : null;
}
