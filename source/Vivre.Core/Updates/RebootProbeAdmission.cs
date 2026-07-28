namespace Vivre.Core.Updates;

/// <summary>
/// Pure (no I/O, no side effects) decision for whether the background monitor may run its
/// reboot-pending WinRM probe against one row on this pass.
/// </summary>
/// <remarks>
/// <para>
/// Sits beside <see cref="ForceRebootVerifyGate"/> and <see cref="MonitorSelfHeal"/> — the same family of
/// tiny razors the monitor's reboot-pending path consults so the rule stays readable and unit-testable.
/// </para>
/// <para>
/// The probe used to be admitted on the Windows-Update (patching) tab only. That left the Force-reboot
/// verify arc dead exactly where Force reboot is actually used: Force reboot is not mode-gated, so an
/// operator on a Health tab armed <c>Computer.ForceRebootAwaitingVerify</c> on a row this gate then never
/// let the probe reach — and <see cref="ForceRebootVerifyGate"/>, whose only caller is inside that gate,
/// could never fire. So a row awaiting force-reboot verification is now admitted on EITHER tab.
/// </para>
/// <para>
/// That second admission is deliberately PER ROW and BOUNDED, not a tab-wide ungating: the cost scales
/// with how many boxes the operator force-rebooted, never with fleet size (a ~300-box list on a small
/// box is why blanket ungating was rejected). The bound is <paramref name="hasRecheckBudget"/> — the
/// caller's post-boot recheck budget, refilled by the Force reboot itself and by each offline→online
/// transition, and spent down by the same block that runs the probe. Without it an armed row whose probe
/// keeps answering "still pending" or "couldn't answer" (both of which deliberately leave the marker
/// armed so a later clean transition retries) would probe on every monitor pass forever.
/// </para>
/// </remarks>
public static class RebootProbeAdmission
{
    /// <summary>
    /// True when the monitor may probe this row's reboot-pending state on this pass: the row must be
    /// <paramref name="online"/>, must be admitted by EITHER the patching-tab mode
    /// (<paramref name="isUpdateMode"/>) OR the bounded force-reboot-verify admission
    /// (<paramref name="awaitingForceRebootVerify"/> AND <paramref name="hasRecheckBudget"/>), and must not be
    /// backed off, WinRM-unsupported, or held by an operation.
    /// </summary>
    /// <param name="online">The row's confirmed-online state this pass — an offline box is never probed.</param>
    /// <param name="isUpdateMode">The tab's fixed mode: true on a Patching tab, false on a Health tab.</param>
    /// <param name="awaitingForceRebootVerify">The row's <c>Computer.ForceRebootAwaitingVerify</c> marker —
    /// read PER ROW, so only rows the operator actually force-rebooted widen the gate.</param>
    /// <param name="hasRecheckBudget">The row still has post-boot recheck budget left. This is what BOUNDS
    /// an armed row's probing; an armed row with no budget left is not admitted.</param>
    /// <param name="backoffActive">The host is in its degraded-WinRM back-off window.</param>
    /// <param name="winRmUnsupported">The host rejects Kerberos by design — its WinRM probe can never succeed.</param>
    /// <param name="rowHeld">An operation owns the row (held or monitor-skipped) — closes the
    /// row-became-held-mid-pass race for this one expensive WinRM call.</param>
    public static bool ShouldProbeRebootPending(
        bool online,
        bool isUpdateMode,
        bool awaitingForceRebootVerify,
        bool hasRecheckBudget,
        bool backoffActive,
        bool winRmUnsupported,
        bool rowHeld) =>
        online
        && (isUpdateMode || (awaitingForceRebootVerify && hasRecheckBudget))
        && !backoffActive
        && !winRmUnsupported
        && !rowHeld;
}
