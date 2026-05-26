namespace Vivre.Core.Sccm;

/// <summary>
/// A ConfigMgr client action that fires a built-in schedule via
/// <c>SMS_Client.TriggerSchedule</c>. Ported verbatim from the legacy cm12 plugin —
/// the schedule GUIDs are ConfigMgr constants and must not be changed.
/// </summary>
/// <param name="Label">Menu text shown to the user.</param>
/// <param name="ScheduleId">The ConfigMgr schedule GUID (including braces).</param>
/// <param name="CompletionMessage">Status text reported when the trigger is accepted.</param>
public sealed record ScheduleAction(string Label, string ScheduleId, string CompletionMessage);

/// <summary>The set of client actions exposed in the grid's right-click menu.</summary>
public static class ClientActions
{
    public static IReadOnlyList<ScheduleAction> All { get; } =
    [
        new("Machine Policy Retrieval & Evaluation", "{00000000-0000-0000-0000-000000000021}", "Machine policy requested"),
        new("Send Heartbeat (DDR)",                  "{00000000-0000-0000-0000-000000000003}", "Heartbeat requested"),
        new("Hardware Inventory",                    "{00000000-0000-0000-0000-000000000001}", "Hardware inventory requested"),
        new("Software Update Scan",                  "{00000000-0000-0000-0000-000000000113}", "Update scan requested"),
        new("Software Update Evaluation",            "{00000000-0000-0000-0000-000000000108}", "Update evaluation requested"),
    ];
}
