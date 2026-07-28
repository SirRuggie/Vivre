using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Locks the razor that decides whether the background monitor may run its reboot-pending WinRM probe
/// against one row on this pass. Two things matter here:
/// <list type="bullet">
///   <item>a row awaiting Force-reboot verification is admitted on a HEALTH tab too (the old gate was
///     patching-only, which left the verify arc dead exactly where Force reboot is used); and</item>
///   <item>that admission is PER ROW and BOUNDED by the post-boot recheck budget — an armed row with no
///     budget left is NOT admitted, so an armed row whose probe keeps answering "still pending" or
///     "couldn't answer" can never probe every pass forever.</item>
/// </list>
/// </summary>
public class RebootProbeAdmissionTests
{
    /// <summary>The old gate's whole admission: a patching tab probes every eligible online row.</summary>
    [Fact]
    public void Update_mode_online_and_idle_is_admitted() =>
        Assert.True(RebootProbeAdmission.ShouldProbeRebootPending(
            online: true, isUpdateMode: true, awaitingForceRebootVerify: false, hasRecheckBudget: false,
            backoffActive: false, winRmUnsupported: false, rowHeld: false));

    /// <summary>The old gate's whole exclusion: a health tab probed nothing at all.</summary>
    [Fact]
    public void Health_mode_row_that_is_not_awaiting_verify_is_not_admitted() =>
        Assert.False(RebootProbeAdmission.ShouldProbeRebootPending(
            online: true, isUpdateMode: false, awaitingForceRebootVerify: false, hasRecheckBudget: true,
            backoffActive: false, winRmUnsupported: false, rowHeld: false));

    /// <summary>THE CHANGE: an armed row with budget left is admitted on a health tab, so the Force-reboot
    /// verify arc can complete on the tab the operator actually force-reboots from.</summary>
    [Fact]
    public void Health_mode_row_awaiting_verify_with_budget_is_admitted() =>
        Assert.True(RebootProbeAdmission.ShouldProbeRebootPending(
            online: true, isUpdateMode: false, awaitingForceRebootVerify: true, hasRecheckBudget: true,
            backoffActive: false, winRmUnsupported: false, rowHeld: false));

    /// <summary>THE BOUND: the same armed row is dropped the moment its recheck budget is spent. This is
    /// what stops an armed row (whose marker deliberately survives a true/null probe answer) from pulling
    /// a WinRM probe on every monitor pass forever.</summary>
    [Fact]
    public void Health_mode_row_awaiting_verify_without_budget_is_not_admitted() =>
        Assert.False(RebootProbeAdmission.ShouldProbeRebootPending(
            online: true, isUpdateMode: false, awaitingForceRebootVerify: true, hasRecheckBudget: false,
            backoffActive: false, winRmUnsupported: false, rowHeld: false));

    /// <summary>The budget bound applies ONLY to the force-reboot admission — a patching tab still probes
    /// on its own cadence with no budget, exactly as before.</summary>
    [Fact]
    public void Update_mode_does_not_need_budget() =>
        Assert.True(RebootProbeAdmission.ShouldProbeRebootPending(
            online: true, isUpdateMode: true, awaitingForceRebootVerify: false, hasRecheckBudget: false,
            backoffActive: false, winRmUnsupported: false, rowHeld: false));

    /// <summary>An offline box is never probed, however it was admitted.</summary>
    [Theory]
    [InlineData(true, false, false)]   // patching tab
    [InlineData(false, true, true)]    // armed health row with budget
    public void Offline_is_never_admitted(bool isUpdateMode, bool awaiting, bool budget) =>
        Assert.False(RebootProbeAdmission.ShouldProbeRebootPending(
            online: false, isUpdateMode, awaiting, budget,
            backoffActive: false, winRmUnsupported: false, rowHeld: false));

    /// <summary>The three suppressors veto BOTH admissions — the new arm never bypasses the degraded
    /// back-off, the Kerberos-unsupported set, or the row-held race guard.</summary>
    [Theory]
    [InlineData(true, false, false)]   // patching tab
    [InlineData(false, true, true)]    // armed health row with budget
    public void Backoff_unsupported_and_held_veto_both_admissions(bool isUpdateMode, bool awaiting, bool budget)
    {
        Assert.False(RebootProbeAdmission.ShouldProbeRebootPending(
            online: true, isUpdateMode, awaiting, budget,
            backoffActive: true, winRmUnsupported: false, rowHeld: false));
        Assert.False(RebootProbeAdmission.ShouldProbeRebootPending(
            online: true, isUpdateMode, awaiting, budget,
            backoffActive: false, winRmUnsupported: true, rowHeld: false));
        Assert.False(RebootProbeAdmission.ShouldProbeRebootPending(
            online: true, isUpdateMode, awaiting, budget,
            backoffActive: false, winRmUnsupported: false, rowHeld: true));
    }

    /// <summary>
    /// The full health-tab admission truth table: on a health tab ONLY the armed-and-budgeted row is
    /// admitted. Budget alone must not admit anything (every row gets a budget on each offline→online
    /// transition, so this is the case that would silently ungate the whole tab).
    /// </summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]   // budget alone — every returning box has one; must NOT admit
    [InlineData(true, false, false)]   // armed but spent
    [InlineData(true, true, true)]
    public void Health_tab_admits_only_armed_rows_with_budget(bool awaiting, bool budget, bool expected) =>
        Assert.Equal(expected, RebootProbeAdmission.ShouldProbeRebootPending(
            online: true, isUpdateMode: false, awaiting, budget,
            backoffActive: false, winRmUnsupported: false, rowHeld: false));
}
