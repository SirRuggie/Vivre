namespace Vivre.Core.Updates;

/// <summary>
/// Pure text for the scheduled-task cancel breadcrumb. Shared by the row's "Last status" (shown as a
/// column in Fleet Health) and its "Windows update message" (shown in Patching) so both grids read
/// IDENTICALLY after a cancel: the Patching column keeps the breadcrumb instead of blanking, until the
/// row's next action replaces it. A regression test locks the literals — the set-site is in
/// <c>WorkspaceViewModel</c> (a different project), so these can't be shared as constants there.
/// </summary>
public static class ScheduledTaskMessage
{
    /// <summary>
    /// The status shown when a cancel completes: the success wording, or the errors variant when the
    /// remote <c>Unregister-ScheduledTask</c> reported errors.
    /// </summary>
    public static string CancelStatus(bool hadErrors) =>
        hadErrors ? "Cancel had errors" : "Scheduled task cancelled";
}
