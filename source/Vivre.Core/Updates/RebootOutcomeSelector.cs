namespace Vivre.Core.Updates;

/// <summary>Which post-reboot outcome <see cref="RebootOutcomeSelector"/> selected — the single source
/// of truth for BOTH the message string and the display state, so the two can never drift.</summary>
public enum RebootOutcomeKind
{
    UpToDate,
    Remaining,
    Failed,
    RebootStillPending,
    CouldntRescan,
    CouldntConfirm,
}

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
    /// <param name="installed">Number of updates confirmed installed during this pass; null when no
    /// un-consumed install ran this session (the "installed N" clause is omitted, never a false 0).</param>
    /// <param name="failed">Number of updates that failed to install; null when no un-consumed
    /// install ran (stamped together with <paramref name="installed"/>).</param>
    /// <param name="remaining">Number of updates still applicable after the reboot.</param>
    /// <param name="rebootStillPending">TRI-STATE: true = a reboot is confirmed still required;
    /// false = confirmed clean; null = the probe couldn't answer (Kerberos/WinRM failure or a 120s
    /// probe timeout) — rendered as an honest "couldn't confirm", NEVER as up-to-date. The unknown
    /// branch deliberately sits BELOW remaining (real actionable data keeps winning) and ONLY
    /// replaces the otherwise-false-green up-to-date.</param>
    /// <param name="scanFailed">True if the post-reboot rescan could not be completed.</param>
    /// <returns>A human-readable outcome string from <see cref="RebootOutcomeMessages"/>.</returns>
    public static string Select(int? installed, int? failed, int remaining, bool? rebootStillPending, bool scanFailed) =>
        Classify(failed, remaining, rebootStillPending, scanFailed) switch
        {
            RebootOutcomeKind.CouldntRescan => RebootOutcomeMessages.BackOnlineRescanFailed(),
            RebootOutcomeKind.Failed => RebootOutcomeMessages.BackOnlineFailed(installed ?? 0, failed!.Value, remaining),
            RebootOutcomeKind.RebootStillPending => RebootOutcomeMessages.RebootStillPending(),
            RebootOutcomeKind.Remaining => RebootOutcomeMessages.BackOnlineRemaining(installed, remaining),
            RebootOutcomeKind.CouldntConfirm => RebootOutcomeMessages.BackOnlineRebootUnknown(installed),
            _ => RebootOutcomeMessages.BackOnlineUpToDate(installed),
        };

    /// <summary>Classifies the post-reboot rescan result into the outcome kind, using the SAME
    /// truthfulness-first precedence as <see cref="Select"/> (scan-failure &gt; install-failure &gt;
    /// confirmed pending &gt; remaining &gt; couldn't-confirm &gt; up-to-date). This is the single source
    /// of precedence — <see cref="Select"/> delegates to it, and the view model keys the display state
    /// off it, so the chip and the message can never disagree.</summary>
    public static RebootOutcomeKind Classify(int? failed, int remaining, bool? rebootStillPending, bool scanFailed)
    {
        if (scanFailed) return RebootOutcomeKind.CouldntRescan;
        if (failed > 0) return RebootOutcomeKind.Failed;
        if (rebootStillPending == true) return RebootOutcomeKind.RebootStillPending;
        if (remaining > 0) return RebootOutcomeKind.Remaining;
        if (rebootStillPending is null) return RebootOutcomeKind.CouldntConfirm;
        return RebootOutcomeKind.UpToDate;
    }
}
