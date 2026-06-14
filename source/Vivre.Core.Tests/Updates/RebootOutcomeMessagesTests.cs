using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Smoke tests for <see cref="RebootOutcomeMessages"/> format methods.
/// These strings are wired from <c>WorkspaceViewModel.ReportPostRebootOutcomeAsync</c> via
/// <see cref="RebootOutcomeSelector"/>. These tests assert their exact shape so a refactor
/// can't silently change wording.
/// </summary>
public class RebootOutcomeMessagesTests
{
    [Fact]
    public void BackOnlineUpToDate_singular()
    {
        Assert.Equal("Back online · installed 1 · up to date", RebootOutcomeMessages.BackOnlineUpToDate(1));
    }

    [Fact]
    public void BackOnlineUpToDate_plural()
    {
        Assert.Equal("Back online · installed 5 · up to date", RebootOutcomeMessages.BackOnlineUpToDate(5));
    }

    [Fact]
    public void BackOnlineRemaining_returns_correct_format()
    {
        Assert.Equal("Back online · installed 3 · 2 remaining", RebootOutcomeMessages.BackOnlineRemaining(3, 2));
    }

    [Fact]
    public void BackOnlineFailed_no_remaining()
    {
        Assert.Equal("Back online · installed 2 · 1 failed", RebootOutcomeMessages.BackOnlineFailed(2, 1, 0));
    }

    [Fact]
    public void BackOnlineFailed_with_remaining()
    {
        Assert.Equal("Back online · installed 2 · 1 failed · 3 remaining", RebootOutcomeMessages.BackOnlineFailed(2, 1, 3));
    }

    [Fact]
    public void InstalledNoReboot_returns_correct_format()
    {
        Assert.Equal("Installed 4 · up to date", RebootOutcomeMessages.InstalledNoReboot(4));
    }

    [Fact]
    public void RebootStillPending_returns_correct_string()
    {
        Assert.Equal("Back online · reboot still pending — re-check", RebootOutcomeMessages.RebootStillPending());
    }

    [Fact]
    public void StillRebooting_returns_correct_string()
    {
        Assert.Equal("Rebooting · waiting for it to come back…", RebootOutcomeMessages.StillRebooting());
    }
}
