using System.Globalization;

namespace Vivre.Core.Updates;

/// <summary>
/// Builds the absolute-instant string Vivre assigns to a scheduled task's <c>StartBoundary</c>.
///
/// The operator picks a wall-clock time that means the VIVRE HOST's local time (the picker yields a
/// <see cref="DateTimeKind.Unspecified"/> value). The scheduled-task command runs ON THE REMOTE BOX,
/// so a bare no-offset string would be read as that box's OWN local time — a UTC Azure box would then
/// fire hours off from what the operator intended. To make every target fire at the SAME absolute
/// instant, we convert the operator's host-local time to UTC here and emit it with a trailing <c>Z</c>.
///
/// That string is assigned DIRECTLY to <c>$trigger.StartBoundary</c> — never passed to
/// <c>New-ScheduledTaskTrigger -At</c>, whose <c>[DateTime]</c> cast strips the UTC marker — so Task
/// Scheduler honors the absolute instant per the documented <c>ITrigger::put_StartBoundary</c> contract.
/// </summary>
public static class ScheduleTimeFormatter
{
    /// <summary>
    /// Converts an operator-picked time (treated as the Vivre host's LOCAL wall-clock) to the
    /// absolute-UTC <c>StartBoundary</c> string, e.g. <c>2026-07-01T18:00:00Z</c>. The Kind is pinned
    /// to <see cref="DateTimeKind.Local"/> explicitly so a Kind=Unspecified picker value (or any other
    /// Kind) can't be silently misread; <see cref="DateTime.ToUniversalTime"/> then converts using the
    /// host zone, and the trailing literal <c>Z</c> makes the instant unambiguous to Task Scheduler.
    /// </summary>
    public static string FormatStartBoundaryUtc(DateTime at) =>
        DateTime.SpecifyKind(at, DateTimeKind.Local)
            .ToUniversalTime()
            .ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
