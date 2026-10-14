using Vivre.Core.Updates;
namespace Vivre.Core.Tests.Updates;

public sealed class ScheduledTaskTrackerTests
{
    private readonly DateTime now = new(2026, 10, 14, 12, 0, 0);
    [Fact]
    public void MultipleTasksAdvanceWithoutClearingLaterTasks()
    {
        var tracker = new ScheduledTaskTracker();
        tracker.Record("DUMMY", "install-a", "Install updates", now.AddHours(1));
        tracker.Record("dummy", "install-b", "Install updates", now.AddHours(3));
        tracker.Record("DUMMY", "reboot", "Reboot", now.AddHours(2));
        Assert.Equal(3, tracker.Next("Dummy", now)!.TaskCount);
        Assert.Equal("Reboot", tracker.Next("dummy", now.AddHours(1))!.Action);
        Assert.Equal(2, tracker.Next("dummy", now.AddHours(1))!.TaskCount);
        Assert.Equal(now.AddHours(3), tracker.Next("dummy", now.AddHours(2))!.At);
        Assert.Null(tracker.Next("dummy", now.AddHours(3)));
    }
    [Fact]
    public void ConfirmedSameTaskReplacesOnlyThatTask()
    {
        var tracker = new ScheduledTaskTracker();
        tracker.Record("dummy", "install-a", "Install updates", now.AddHours(3));
        tracker.Record("dummy", "reboot", "Reboot", now.AddHours(1));
        tracker.Record("dummy", "REBOOT", "Reboot", now.AddHours(4));
        Assert.Equal(now.AddHours(3), tracker.Next("dummy", now)!.At);
        Assert.Equal(2, tracker.Next("dummy", now)!.TaskCount);
    }
    [Fact]
    public void UnconfirmedReplacementPreservesBothPossibleTimesUntilConfirmed()
    {
        var tracker = new ScheduledTaskTracker();
        tracker.Record("dummy", "reboot", "Reboot", now.AddHours(1));
        tracker.Record("dummy", "reboot", "Reboot", now.AddHours(3), confirmed: false);
        var first = tracker.Next("dummy", now)!;
        Assert.True(first.Unconfirmed); Assert.Equal(1, first.TaskCount); Assert.Equal(now.AddHours(1), first.At);
        Assert.Equal(now.AddHours(3), tracker.Next("dummy", now.AddHours(1))!.At);
        tracker.Record("dummy", "reboot", "Reboot", now.AddHours(4));
        Assert.False(tracker.Next("dummy", now)!.Unconfirmed);
        Assert.Equal(now.AddHours(4), tracker.Next("dummy", now)!.At);
    }
    [Fact]
    public void RemovalClearsOnlyRequestedHost()
    {
        var tracker = new ScheduledTaskTracker();
        tracker.Record("dummy-a", "install", "Install updates", now.AddHours(1));
        tracker.Record("dummy-b", "install", "Install updates", now.AddHours(2));
        tracker.Remove("DUMMY-A");
        Assert.Null(tracker.Next("dummy-a", now)); Assert.NotNull(tracker.Next("dummy-b", now));
    }
}
