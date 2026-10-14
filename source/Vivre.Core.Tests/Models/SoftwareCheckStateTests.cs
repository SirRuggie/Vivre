using Vivre.Core.Models;
using Vivre.Core.Software;
using Xunit;

namespace Vivre.Core.Tests.Models;

public class SoftwareCheckStateTests
{
    [Fact]
    public async Task QueuedCancellation_ReplacesPriorResult_AndDoesNotReleaseUnownedSlot()
    {
        var computer = new Computer("DUMMY-01")
        {
            SoftwareFound = true, SoftwareName = "Old product", SoftwareVersion = "1",
            SoftwareCheck = "Installed — Old product", SoftwareQuery = "Old", SoftwareServiceState = "Running",
            SoftwareCheckedAt = DateTime.MinValue, SoftwareQueryWarning = "old warning",
        };
        using var throttle = new SemaphoreSlim(0, 1);
        using var stop = new CancellationTokenSource();
        bool invoked = false;
        Task check = SoftwareCheckState.RunAsync(computer, "New", "DummySvc", throttle,
            _ => { invoked = true; return Task.CompletedTask; }, stop.Token);
        Assert.Null(computer.SoftwareFound);
        Assert.Null(computer.SoftwareName);
        Assert.Null(computer.SoftwareVersion);
        Assert.Null(computer.SoftwareServiceState);
        Assert.Null(computer.SoftwareQueryWarning);
        Assert.Null(computer.SoftwareCheckedAt);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.False(invoked);
        Assert.Equal(0, throttle.CurrentCount);
        Assert.Equal("New", computer.SoftwareQuery);
        Assert.Null(computer.SoftwareFound);
        Assert.Contains("cancelled before check started", computer.SoftwareCheck);
        Assert.NotNull(computer.SoftwareCheckedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recheck_ClearsOwnedError_PreservesUnrelatedError(bool unrelated)
    {
        var computer = new Computer("DUMMY-01") { SoftwareQuery = "Dummy" };
        SoftwareCheckState.Fail(computer, "Dummy", "denied", 0);
        if (unrelated) computer.LastError = "Separate operation failed";
        using var throttle = new SemaphoreSlim(1, 1);
        await SoftwareCheckState.RunAsync(computer, "Dummy", null, throttle, generation =>
        {
            SoftwareCheckState.Complete(computer, "Dummy", new(true, "Dummy product", "1", null), generation);
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.Equal(unrelated ? "Separate operation failed" : null, computer.LastError);
        Assert.Null(computer.SoftwareError);
        Assert.False(computer.SoftwareWarning);
        Assert.True(computer.SoftwareFound);
        Assert.Equal(1, throttle.CurrentCount);
    }

    [Fact]
    public async Task ActiveFailure_ReleasesOwnedSlot()
    {
        using var throttle = new SemaphoreSlim(1, 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SoftwareCheckState.RunAsync(
            new Computer("DUMMY-01"), "Dummy", null, throttle,
            _ => throw new InvalidOperationException("dummy failure"), CancellationToken.None));
        Assert.Equal(1, throttle.CurrentCount);
    }

    [Fact]
    public void ProvenMatchAndServiceFailure_RemainInstalledAndUnknown()
    {
        var computer = new Computer("DUMMY-01");
        SoftwareCheckState.Complete(computer, "Dummy", new(true, "Dummy product", "1", null,
            "one entry denied", "service query denied"), 0);
        Assert.True(computer.SoftwareFound);
        Assert.Null(computer.SoftwareServiceDown);
        Assert.True(computer.SoftwareWarning);
        Assert.Contains("Installed — Dummy product 1", computer.SoftwareCheck);
        Assert.Contains("service could not be checked", computer.SoftwareCheck);
        Assert.Contains("partial check", computer.SoftwareCheck);
    }

    [Fact]
    public void ProductAbsenceAndRunningService_AreSeparateExplicitVerdicts()
    {
        var computer = new Computer("DUMMY-01");
        SoftwareCheckState.Complete(computer, "Dummy", new(false, null, null, "Running"), 0);
        Assert.False(computer.SoftwareFound);
        Assert.Equal("Not installed — Dummy · service running", computer.SoftwareCheck);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledOldRun_CannotOverwriteNewerResult(bool oldFailure)
    {
        var computer = new Computer("DUMMY-01");
        using var throttle = new SemaphoreSlim(2, 2);
        using var stop = new CancellationTokenSource();
        var finishOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task old = SoftwareCheckState.RunAsync(computer, "Old", null, throttle, async generation =>
        {
            await finishOld.Task;
            bool applied = oldFailure
                ? SoftwareCheckState.Fail(computer, "Old", "cancelled", generation)
                : SoftwareCheckState.Complete(computer, "Old", new(false, null, null, null), generation);
            Assert.False(applied);
        }, stop.Token);
        stop.Cancel();
        await SoftwareCheckState.RunAsync(computer, "New", null, throttle, generation =>
        {
            Assert.True(SoftwareCheckState.Complete(computer, "New", new(true, "New product", "2", null), generation));
            return Task.CompletedTask;
        }, CancellationToken.None);
        finishOld.SetResult();
        await old;
        Assert.True(computer.SoftwareFound);
        Assert.Equal("New", computer.SoftwareQuery);
        Assert.Equal("Installed — New product 2", computer.SoftwareCheck);
        Assert.Null(computer.SoftwareError);
        Assert.Null(computer.LastError);
        Assert.Equal(2, throttle.CurrentCount);
    }

}
