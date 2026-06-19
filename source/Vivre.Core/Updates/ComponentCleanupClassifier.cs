namespace Vivre.Core.Updates;

/// <summary>The operator-facing outcome of a Server 2016 component cleanup, decided from the agent's raw facts.</summary>
public enum ComponentCleanupOutcome
{
    /// <summary>DISM exited 0 — a clean cleanup. The lane keeps its existing "Cleaned — ready to Stage" wording.</summary>
    CleanSuccess,

    /// <summary>DISM exited access-denied (5): the backlog cleared but a locked remainder couldn't be deleted
    /// (commonly AV/EDR holding WinSxS handles). A SUCCESS-WITH-CAVEAT — rendered as the neutral "Cleaned"
    /// state, never red. The caveat lives in the activity-log detail.</summary>
    CleanedFilesLocked,

    /// <summary>Any other non-zero exit — a genuine failure; the real exit code is surfaced unchanged.</summary>
    Failed,
}

/// <summary>The classified cleanup result: the <see cref="ComponentCleanupOutcome"/> plus the operator-facing
/// strings for the <see cref="CleanedFilesLocked"/> case (null for the other outcomes, which keep their
/// existing wording).</summary>
/// <param name="Outcome">The decided outcome.</param>
/// <param name="ShortStatus">The status-cell text (set only for <see cref="ComponentCleanupOutcome.CleanedFilesLocked"/>).</param>
/// <param name="Detail">The activity-log detail (set only for <see cref="ComponentCleanupOutcome.CleanedFilesLocked"/>).</param>
public readonly record struct ComponentCleanupClassification(
    ComponentCleanupOutcome Outcome, string? ShortStatus, string? Detail);

/// <summary>
/// Pure decision (no I/O): turns the on-target agent's raw component-cleanup facts — the
/// StartComponentCleanup exit code, whether the follow-up AnalyzeComponentStore parsed, and the
/// reclaimable-package count — into a <see cref="ComponentCleanupClassification"/>. The access-denied
/// exit means the cleanup cleared the backlog but couldn't commit the deletion of a locked remainder, so
/// it is reclassified as success-with-caveat rather than a hard failure. Mirrors
/// <see cref="RebootOutcomeSelector"/>: the classifier decides, <see cref="ComponentCleanupMessages"/>
/// supplies the wording. (Exit 3010 is handled upstream as a reboot-recommended success and never reaches
/// here, so it is not a special case below.)
/// </summary>
public static class ComponentCleanupClassifier
{
    /// <summary>Win32 ERROR_ACCESS_DENIED — DISM's process exit code for the locked-files case (decimal 5,
    /// shown as 0x00000005 in our logs / 0x80070005 in CBS).</summary>
    public const int ErrorAccessDenied = 5;

    /// <summary>
    /// Classify a cleanup result from the agent's facts.
    /// </summary>
    /// <param name="exitCode">DISM /StartComponentCleanup process exit code (decimal).</param>
    /// <param name="analyzeOk">True when the read-only AnalyzeComponentStore ran and its "Number of
    /// Reclaimable Packages" line parsed. Only meaningful for the access-denied exit.</param>
    /// <param name="reclaimableCount">The parsed reclaimable-package count when <paramref name="analyzeOk"/>;
    /// null when the count is unknown.</param>
    public static ComponentCleanupClassification Classify(int exitCode, bool analyzeOk, int? reclaimableCount)
    {
        if (exitCode == 0)
        {
            return new(ComponentCleanupOutcome.CleanSuccess, null, null);
        }

        if (exitCode == ErrorAccessDenied)
        {
            string detail =
                analyzeOk && reclaimableCount is int n
                    ? (n > 0 ? ComponentCleanupMessages.LockedPackagesSkipped(n)
                             : ComponentCleanupMessages.LockedBacklogClear())
                    : ComponentCleanupMessages.LockedRemainderUnknown();

            return new(ComponentCleanupOutcome.CleanedFilesLocked, ComponentCleanupMessages.LockedShortStatus(), detail);
        }

        return new(ComponentCleanupOutcome.Failed, null, null);
    }
}
