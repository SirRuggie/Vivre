namespace Vivre.Core.Updates;

/// <summary>
/// Pure (no I/O, no side effects) selector that maps the post-reboot rescan result to the
/// appropriate <see cref="RebootOutcomeMessages"/> string.
/// <para>
/// Called by the reboot-and-verify wiring after the wave returns Done and a rescan has run.
/// Selection is truthfulness-first: a scan failure is always surfaced, failures beat pending
/// reboots, remaining beats up-to-date. It deliberately does NOT use
/// <see cref="RebootOutcomeMessages.InstalledNoReboot"/> (that is the no-reboot install path)
/// nor <see cref="RebootOutcomeMessages.StillRebooting"/> (in-flight, not a terminal outcome).
/// </para>
/// </summary>
public static class RebootOutcomeSelector
{
    /// <summary>
    /// Selects the outcome message that most accurately describes the post-reboot rescan result.
    /// </summary>
    /// <param name="installed">Number of updates confirmed installed during this pass.</param>
    /// <param name="failed">Number of updates that failed to install.</param>
    /// <param name="remaining">Number of updates still applicable after the reboot.</param>
    /// <param name="rebootStillPending">True if the rescan reports another reboot is still required.</param>
    /// <param name="scanFailed">True if the post-reboot rescan could not be completed.</param>
    /// <returns>A human-readable outcome string from <see cref="RebootOutcomeMessages"/>.</returns>
    public static string Select(int installed, int failed, int remaining, bool rebootStillPending, bool scanFailed)
    {
        if (scanFailed) return RebootOutcomeMessages.BackOnlineRescanFailed();
        if (failed > 0) return RebootOutcomeMessages.BackOnlineFailed(installed, failed, remaining);
        if (rebootStillPending) return RebootOutcomeMessages.RebootStillPending();
        if (remaining > 0) return RebootOutcomeMessages.BackOnlineRemaining(installed, remaining);
        return RebootOutcomeMessages.BackOnlineUpToDate(installed);
    }
}
