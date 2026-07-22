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

/// <summary>Why a box is (or isn't) reboot-ready — the discriminator the wave uses to decide whether to
/// proceed, wait, or stop. Ordered from "go" through "never go".</summary>
public enum RebootReadinessKind
{
    /// <summary>A positive, fully-read quiescent triple: TrustedInstaller stopped, TiWorker idle, AND a CBS
    /// RebootPending key present. The ONLY verdict the wave proceeds to a graceful reboot on.</summary>
    Ready,

    /// <summary>Online servicing is PROVABLY still busy (TrustedInstaller not "Stopped", or TiWorker.exe
    /// alive) — worth waiting for: rebooting now risks the 2-hour "Stopping" hang, so the wave polls until it
    /// settles (a fresh <see cref="Ready"/>) or the settle window expires.</summary>
    ServicingActive,

    /// <summary>The CBS RebootPending key is DEFINITIVELY absent (a settled StdRegProv answer, ReturnValue 2)
    /// — nothing is staged, so there is nothing to reboot for. A settled answer, not an error; the wave stops
    /// calmly with a "nothing to commit" terminal.</summary>
    NothingStaged,

    /// <summary>The readiness read failed, was unreachable, or was unreadable (a query error / access-denied /
    /// a null where a value was expected) — NOT evidence of anything. Fail-closed: the wave waits and
    /// re-checks and NEVER proceeds to a reboot on this. The DEFAULT, so any un-migrated construction is
    /// treated as "couldn't confirm", never as a green "go".</summary>
    CantConfirm,
}

/// <summary>The pre-reboot readiness verdict: a box is safe to reboot only when its online servicing has
/// finished (TrustedInstaller stopped) AND a reboot is actually queued (CBS RebootPending present). The
/// <paramref name="Kind"/> discriminates WHY when <paramref name="IsReady"/> is false — so the wave can
/// wait on transient servicing, stop calmly when nothing is staged, and never proceed on an unreadable
/// probe. <paramref name="Kind"/> defaults to <see cref="RebootReadinessKind.CantConfirm"/> — fail-closed
/// for any construction that doesn't set a kind explicitly.</summary>
public sealed record RebootReadiness(bool IsReady, string Reason, RebootReadinessKind Kind = RebootReadinessKind.CantConfirm);

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
    private readonly TimeSpan? _servicingSettleWindow;

    /// <summary>Bounds the PRE-reboot wait for a box whose readiness comes back not-ready-but-worth-waiting
    /// (servicing still running) or unreadable (couldn't confirm): the wave re-checks readiness every
    /// <see cref="PollInterval"/> and proceeds the instant it reads a fresh positive <see cref="RebootReadiness.IsReady"/>;
    /// once this window elapses WITHOUT a positive reading it stops and returns a needs-action terminal —
    /// it NEVER forces and NEVER reboots on expiry. Its clock is SEPARATE from <see cref="HardCap"/> (which
    /// starts at the graceful dispatch, i.e. after this settle wait has already succeeded). Defaults to
    /// 20 minutes; inherited unchanged by <see cref="ForSlowCommit"/> via the <c>with</c> copy.</summary>
    public TimeSpan ServicingSettleWindow
    {
        get => _servicingSettleWindow ?? TimeSpan.FromMinutes(20);
        init => _servicingSettleWindow = value;
    }

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
