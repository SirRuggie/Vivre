namespace Vivre.Core.Updates;

/// <summary>Reboots a host — graceful (let SQL/services flush) or forced (`/f`-equivalent). Implemented
/// over DCOM on the ambient login so it works on the Kerberos-broken Vision boxes.
/// <para><b>Scope:</b> the only caller is the Reboot Wave, which runs only on boxes the operator explicitly
/// selected and confirmed. The forced call is the tail of completing one of those operator-ordered reboots
/// (graceful→8min→force) — it is never an independent decision to reboot or force a box the operator didn't
/// pick. Locked rule: nothing reboots or forces a reboot without the operator's explicit per-box trigger.</para></summary>
public interface IRebootTrigger
{
    /// <summary>Issues the reboot and reports the outcome so the wave can tell "accepted" from "a shutdown
    /// was ALREADY in progress" (the box is going offline on its own — watch it commit, don't re-escalate
    /// or fail it). A genuine failure to issue the reboot still throws.</summary>
    Task<RebootDispatch> RebootAsync(string host, bool forced, CancellationToken cancellationToken);
}

/// <summary>The outcome of issuing a reboot.</summary>
public enum RebootDispatch
{
    /// <summary>The OS accepted the reboot (over DCOM, or via the SMB/SCM fallback).</summary>
    Issued,

    /// <summary>A shutdown was ALREADY in progress on the box (Win32 1115 / ERROR_SHUTDOWN_IN_PROGRESS) —
    /// the box is going offline on its own, so the wave should drop into the commit-watch loop rather than
    /// escalating to a forced reboot or declaring a false "reboot isn't taking" failure.</summary>
    AlreadyInProgress,
}

/// <summary>The pre-reboot readiness verdict: a box is safe to reboot only when its online servicing has
/// finished (TrustedInstaller stopped) AND a reboot is actually queued (CBS RebootPending present).</summary>
public sealed record RebootReadiness(bool IsReady, string Reason);

/// <summary>Checks reboot-readiness right before the wave issues a reboot (TrustedInstaller stopped +
/// CBS RebootPending present) — re-checked live so a box that quietly resumed servicing isn't rebooted
/// into the 2-hour Stopping hang.</summary>
public interface IRebootReadinessProbe
{
    Task<RebootReadiness> CheckAsync(string host, CancellationToken cancellationToken);
}

/// <summary>Is the host responding on the network? Used to detect "went offline" (reboot started) and
/// "came back" (commit done — then Verify reads the UBR).</summary>
public interface IReachabilityProbe
{
    Task<bool> IsReachableAsync(string host, CancellationToken cancellationToken);
}

/// <summary>
/// The two timers + cadence for a Reboot Wave. They are deliberately separate: the go-offline window is the
/// graceful→forced escalation (to complete the operator-ordered reboot), the offline ceiling is only when to
/// FLAG "Overdue" (it never stops the watch). The hard cap bounds live tracking of a box that never returns —
/// the standalone Verify action remains the durable net for one that comes back later.
/// </summary>
/// <param name="GoOfflineWindow">After the graceful reboot, how long to wait for the box to drop off the
/// network before escalating to a forced reboot to complete it. Default 8 minutes.</param>
/// <param name="OfflineCeiling">How long a box may be offline (committing) before it's flagged "Overdue —
/// check console/iLO". The watch CONTINUES past this. Default 90 minutes.</param>
/// <param name="PollInterval">How often to poll reachability while waiting. Default 20 seconds.</param>
/// <param name="HardCap">Absolute bound on LIVE tracking, measured since the reboot was ORDERED (the graceful
/// dispatch), not since the box was first seen offline — a slightly tighter, more honest bound. After this the
/// wave stops watching (red, "use Verify when it's back"). Default 4.5 hours.</param>
public sealed record RebootWaveOptions(
    TimeSpan GoOfflineWindow,
    TimeSpan OfflineCeiling,
    TimeSpan PollInterval,
    TimeSpan HardCap)
{
    private readonly TimeSpan? _forcedGoOfflineWindow;
    private readonly TimeSpan? _postReturnConfirmWindow;

    /// <summary>Bound on the CONTINUOUSLY-reachable-but-unconfirmed phase: once a returned box has been
    /// reachable this long WITHOUT the confirmation strategy confirming (its UBR is unreadable, or it came
    /// back without ever being seen going down and can't be proven rebooted), the wave stops spinning and
    /// returns the neutral <see cref="PatchPhase.Unverified"/> terminal ("couldn't confirm — use Verify"). The
    /// clock RESETS whenever a poll sees the box offline, so a box that flaps (returns, drops again, returns)
    /// re-arms the window each time. Defaults to 30 minutes; inherited unchanged by
    /// <see cref="ForSlowCommit"/> via the <c>with</c> copy.</summary>
    public TimeSpan PostReturnConfirmWindow
    {
        get => _postReturnConfirmWindow ?? TimeSpan.FromMinutes(30);
        init => _postReturnConfirmWindow = value;
    }

    /// <summary>How long to wait for the box to drop offline after the FORCED reboot — deliberately
    /// <b>strictly longer</b> than the graceful <see cref="GoOfflineWindow"/>: the graceful wait already
    /// spent that long watching the box stay up, and a box mid-CBS-commit on shutdown can hold port 445
    /// for many more minutes, so re-using the same window would false-fail it. Defaults to 2× GoOfflineWindow.</summary>
    public TimeSpan ForcedGoOfflineWindow
    {
        get => _forcedGoOfflineWindow ?? TimeSpan.FromTicks(GoOfflineWindow.Ticks * 2);
        init => _forcedGoOfflineWindow = value;
    }

    public static RebootWaveOptions Default { get; } = new(
        GoOfflineWindow: TimeSpan.FromMinutes(8),
        OfflineCeiling: TimeSpan.FromMinutes(90),
        PollInterval: TimeSpan.FromSeconds(20),
        HardCap: TimeSpan.FromHours(4.5));

    /// <summary>Longer go-offline windows for a box expected to commit updates SLOWLY on shutdown (Server
    /// 2016 staged / CBS-heavy): such a box can keep port 445 answering for 15–20+ min while flushing
    /// patches, so the 8-min default would false-fail it as "the reboot isn't taking". 20-min graceful
    /// (⇒ 40-min forced via the 2× default); same ceiling / hard-cap / poll as Default.</summary>
    public static RebootWaveOptions ForSlowCommit { get; } = Default with { GoOfflineWindow = TimeSpan.FromMinutes(20) };
}
