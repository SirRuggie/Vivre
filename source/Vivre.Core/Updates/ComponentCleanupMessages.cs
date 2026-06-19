namespace Vivre.Core.Updates;

/// <summary>
/// Ready-to-use operator-facing strings for a component-cleanup that finished but could not delete a
/// locked remainder (the access-denied / files-locked outcome). Split out of
/// <see cref="ComponentCleanupClassifier"/> like <see cref="RebootOutcomeMessages"/> so the wording is
/// in one place and the classifier stays a pure decision. Cause wording is GENERIC ("security software
/// (AV/EDR)") — never a specific vendor.
/// </summary>
public static class ComponentCleanupMessages
{
    // The shared explanation appended to every locked-files detail: why it happened, that it's harmless
    // for staging, and the two ways to reclaim the rest.
    private const string LockedTail =
        "Windows couldn't remove the rest of the store because the files were locked, commonly by " +
        "security software (AV/EDR) holding open handles. This doesn't block staging. To finish reclaiming " +
        "it, temporarily disable AV on this box and run Clean up again — the second pass is quick since the " +
        "bulk is already done. (It also clears on the next reboot, which releases the lock.)";

    /// <summary>The glanceable status-cell text for a locked-files cleanup — neutral, with the detail in the log.</summary>
    public static string LockedShortStatus() => "Cleaned · locked files (see log)";

    /// <summary>Detail when AnalyzeComponentStore confirmed nothing reclaimable remained (the backlog is clear).</summary>
    public static string LockedBacklogClear() =>
        "Component cleanup completed — the update backlog is clear. " + LockedTail;

    /// <summary>Detail when AnalyzeComponentStore found <paramref name="reclaimable"/> packages that were locked and skipped.</summary>
    public static string LockedPackagesSkipped(int reclaimable) =>
        $"Component cleanup applied — {reclaimable} package(s) were locked and skipped. " + LockedTail;

    /// <summary>Detail when AnalyzeComponentStore couldn't run/parse, so the remaining count is unknown.</summary>
    public static string LockedRemainderUnknown() =>
        "Most of the cleanup applied; a locked remainder couldn't be removed. " + LockedTail;
}
