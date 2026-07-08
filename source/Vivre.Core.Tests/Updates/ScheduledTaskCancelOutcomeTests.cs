using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Locks the reboot-adjacent keep/clear decision for the scheduled-task cancel: the chip may only
/// clear on a verified REMOVED with no errors — every other shape keeps the chip (a surviving
/// <c>Vivre_Reboot</c> task must never hide behind a false "cancelled").
/// </summary>
public class ScheduledTaskCancelOutcomeTests
{
    [Fact]
    public void Clean_removed_clears()
    {
        var outcome = ScheduledTaskCancelOutcome.Classify(hadErrors: false, ["REMOVED"], []);

        Assert.True(outcome.Cleared);
        Assert.Null(outcome.Detail);
    }

    [Fact]
    public void Removed_with_errors_is_kept_as_failed()
    {
        // Safe over-warn: an unregister that wrote an error record isn't trusted even when the
        // verify saw nothing remaining — the operator retries and the clean run clears.
        var outcome = ScheduledTaskCancelOutcome.Classify(hadErrors: true, ["REMOVED"], ["Access is denied"]);

        Assert.False(outcome.Cleared);
        Assert.Equal("Cancel failed", outcome.Status);
        Assert.Equal("Access is denied", outcome.Detail);
    }

    [Fact]
    public void Remaining_single_task_keeps_chip_and_names_it()
    {
        var outcome = ScheduledTaskCancelOutcome.Classify(hadErrors: true, ["REMAINING: Vivre_Reboot"], ["Access is denied"]);

        Assert.False(outcome.Cleared);
        Assert.Equal("Still scheduled: Vivre_Reboot", outcome.Detail);
    }

    [Fact]
    public void Remaining_multiple_tasks_lists_them_all()
    {
        var outcome = ScheduledTaskCancelOutcome.Classify(
            hadErrors: true, ["REMAINING: Vivre_Reboot, Vivre_WUA_abc123"], []);

        Assert.False(outcome.Cleared);
        Assert.Equal("Still scheduled: Vivre_Reboot, Vivre_WUA_abc123", outcome.Detail);
    }

    [Fact]
    public void Task_name_containing_removed_substring_does_not_false_clear()
    {
        // The substring trap: the surviving task's name embeds REMOVED — only an exact full-line
        // REMOVED may clear.
        var outcome = ScheduledTaskCancelOutcome.Classify(
            hadErrors: false, ["REMAINING: Vivre_WUA_REMOVEDbackup"], []);

        Assert.False(outcome.Cleared);
        Assert.Equal("Still scheduled: Vivre_WUA_REMOVEDbackup", outcome.Detail);
    }

    [Fact]
    public void Empty_output_with_no_errors_fails_with_fallback_detail()
    {
        // No REMOVED token at all (e.g. the pipeline died mid-script) — never clear on silence.
        var outcome = ScheduledTaskCancelOutcome.Classify(hadErrors: false, [], []);

        Assert.False(outcome.Cleared);
        Assert.Equal("Unregister-ScheduledTask failed", outcome.Detail);
    }

    [Fact]
    public void First_error_is_preferred_when_no_remaining_line()
    {
        var outcome = ScheduledTaskCancelOutcome.Classify(
            hadErrors: true, ["OK"], ["The task scheduler RPC server is unavailable", "second"]);

        Assert.False(outcome.Cleared);
        Assert.Equal("The task scheduler RPC server is unavailable", outcome.Detail);
    }
}
