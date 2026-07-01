using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

public class ScheduledTaskMessageTests
{
    // Locks the exact verbiage Fleet Health's "Last status" has always shown on a cancel, which the
    // Patching "Windows update message" column now mirrors. WorkspaceViewModel assigns BOTH columns from
    // this one method, so they can't drift; this test guards the literal strings against silent change.
    [Fact]
    public void CancelStatus_success_is_scheduled_task_cancelled() =>
        Assert.Equal("Scheduled task cancelled", ScheduledTaskMessage.CancelStatus(hadErrors: false));

    [Fact]
    public void CancelStatus_with_errors_is_cancel_had_errors() =>
        Assert.Equal("Cancel had errors", ScheduledTaskMessage.CancelStatus(hadErrors: true));
}
