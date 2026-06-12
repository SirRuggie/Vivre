namespace Vivre.Core.Updates;

/// <summary>Reboots a host — graceful (let SQL/services flush) or forced (`/f`-equivalent). Implemented
/// over DCOM on the ambient login so it works on the Kerberos-broken Vision boxes.
/// <para><b>Scope:</b> the only caller is the Reboot Wave, which runs only on boxes the operator explicitly
/// selected and confirmed. The forced call is the tail of completing one of those operator-ordered reboots
/// (graceful→8min→force) — it is never an independent decision to reboot or force a box the operator didn't
/// pick. Locked rule: nothing reboots or forces a reboot without the operator's explicit per-box trigger.</para></summary>
public interface IRebootTrigger
{
    Task RebootAsync(string host, bool forced, CancellationToken cancellationToken);
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
/// <param name="HardCap">Absolute bound on LIVE tracking of a box that never returns; after this the wave
/// stops watching it (red, "use Verify when it's back"). Default 4.5 hours.</param>
public sealed record RebootWaveOptions(
    TimeSpan GoOfflineWindow,
    TimeSpan OfflineCeiling,
    TimeSpan PollInterval,
    TimeSpan HardCap)
{
    public static RebootWaveOptions Default { get; } = new(
        GoOfflineWindow: TimeSpan.FromMinutes(8),
        OfflineCeiling: TimeSpan.FromMinutes(90),
        PollInterval: TimeSpan.FromSeconds(20),
        HardCap: TimeSpan.FromHours(4.5));
}
