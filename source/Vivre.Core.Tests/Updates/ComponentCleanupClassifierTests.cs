using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Tests for <see cref="ComponentCleanupClassifier.Classify"/> — the access-denied-is-success-with-caveat
/// decision. Locks the outcome AND the verbatim short status + activity-log detail per branch, so a wording
/// drift (or a regression back to "failed") fails here. Cause wording stays GENERIC (no vendor name).
/// </summary>
public class ComponentCleanupClassifierTests
{
    private const string LockedShort = "Cleaned · locked files (see log)";

    // The shared explanation appended to every locked-files detail.
    private const string Tail =
        "Windows couldn't remove the rest of the store because the files were locked, commonly by " +
        "security software (AV/EDR) holding open handles. This doesn't block staging. To finish reclaiming " +
        "it, temporarily disable AV on this box and run Clean up again — the second pass is quick since the " +
        "bulk is already done. (It also clears on the next reboot, which releases the lock.)";

    // ── Clean success ───────────────────────────────────────────────────────

    [Fact]
    public void Exit0_is_clean_success()
    {
        ComponentCleanupClassification c = ComponentCleanupClassifier.Classify(0, analyzeOk: true, reclaimableCount: 0);

        Assert.Equal(ComponentCleanupOutcome.CleanSuccess, c.Outcome);
        Assert.Null(c.ShortStatus);
        Assert.Null(c.Detail);
    }

    // ── Access denied → cleaned, files locked ─────────────────────────────────

    [Fact]
    public void AccessDenied_with_zero_reclaimable_reads_backlog_clear()
    {
        ComponentCleanupClassification c = ComponentCleanupClassifier.Classify(5, analyzeOk: true, reclaimableCount: 0);

        Assert.Equal(ComponentCleanupOutcome.CleanedFilesLocked, c.Outcome);
        Assert.Equal(LockedShort, c.ShortStatus);
        Assert.Equal("Component cleanup completed — the update backlog is clear. " + Tail, c.Detail);
    }

    [Fact]
    public void AccessDenied_with_reclaimable_packages_reads_N_skipped()
    {
        ComponentCleanupClassification c = ComponentCleanupClassifier.Classify(5, analyzeOk: true, reclaimableCount: 3);

        Assert.Equal(ComponentCleanupOutcome.CleanedFilesLocked, c.Outcome);
        Assert.Equal(LockedShort, c.ShortStatus);
        Assert.Equal("Component cleanup applied — 3 package(s) were locked and skipped. " + Tail, c.Detail);
    }

    [Fact]
    public void AccessDenied_when_analyze_failed_reads_count_unknown()
    {
        ComponentCleanupClassification c = ComponentCleanupClassifier.Classify(5, analyzeOk: false, reclaimableCount: null);

        Assert.Equal(ComponentCleanupOutcome.CleanedFilesLocked, c.Outcome);
        Assert.Equal(LockedShort, c.ShortStatus);
        Assert.Equal("Most of the cleanup applied; a locked remainder couldn't be removed. " + Tail, c.Detail);
    }

    [Fact]
    public void AccessDenied_with_null_count_even_when_analyze_ok_reads_count_unknown()
    {
        // Defensive: analyzeOk true but no count means we can't confirm the remainder — report it as unknown,
        // never as "backlog is clear".
        ComponentCleanupClassification c = ComponentCleanupClassifier.Classify(5, analyzeOk: true, reclaimableCount: null);

        Assert.Equal(ComponentCleanupOutcome.CleanedFilesLocked, c.Outcome);
        Assert.Equal("Most of the cleanup applied; a locked remainder couldn't be removed. " + Tail, c.Detail);
    }

    // ── Genuine failure ───────────────────────────────────────────────────────

    [Fact]
    public void Other_nonzero_exit_is_failed()
    {
        ComponentCleanupClassification c = ComponentCleanupClassifier.Classify(1726, analyzeOk: false, reclaimableCount: null);

        Assert.Equal(ComponentCleanupOutcome.Failed, c.Outcome);
        Assert.Null(c.ShortStatus);
        Assert.Null(c.Detail);
    }
}
