namespace Vivre.Core.Updates;

/// <summary>
/// Classifies the result of the remote scheduled-task cancel script (unregister <c>Vivre_*</c>,
/// then verify by absence): the row's Scheduled chip may only clear on a VERIFIED removal, because
/// a false "cancelled" hides a surviving task — worst case a <c>Vivre_Reboot</c> that still fires
/// on a box the operator believes is safe. Pure so this reboot-adjacent keep/clear decision is
/// unit-tested (the set-site is in <c>WorkspaceViewModel</c>, a different project).
/// </summary>
/// <param name="Cleared">True only when the verify step proved no <c>Vivre_*</c> task remains.</param>
/// <param name="Status">Short row status ("Cancel failed" when not cleared).</param>
/// <param name="Detail">Failure detail for LastError / the activity log; null when cleared.</param>
public sealed record ScheduledTaskCancelOutcome(bool Cleared, string Status, string? Detail)
{
    private const string RemovedToken = "REMOVED";
    private const string RemainingPrefix = "REMAINING: ";

    public static ScheduledTaskCancelOutcome Classify(
        bool hadErrors,
        IReadOnlyList<string> output,
        IReadOnlyList<string> errors)
    {
        // EXACT full-line match, never Contains: a surviving task whose NAME embeds "REMOVED"
        // yields "REMAINING: ...REMOVED..." — a substring match would falsely clear the chip for
        // a task that still fires (the dangerous direction; a kept chip merely over-warns).
        bool removed = output.Any(static line => line.Trim() == RemovedToken);

        // REMOVED with hadErrors is deliberately classified as failed, NOT cleared: the unregister
        // wrote an error record, so the verify isn't trusted — a safe over-warn that self-heals on
        // retry (a genuinely-clean second run classifies Cleared). Do not "fix" this into a clear.
        if (removed && !hadErrors)
        {
            return new(Cleared: true, Status: "Scheduled task cancelled", Detail: null);
        }

        string? remaining = output
            .Select(static line => line.Trim())
            .FirstOrDefault(static line => line.StartsWith(RemainingPrefix, StringComparison.Ordinal));
        string detail = remaining is not null
            ? "Still scheduled: " + remaining[RemainingPrefix.Length..]
            : errors.Count > 0 ? errors[0] : "Unregister-ScheduledTask failed";

        return new(Cleared: false, Status: "Cancel failed", Detail: detail);
    }
}
