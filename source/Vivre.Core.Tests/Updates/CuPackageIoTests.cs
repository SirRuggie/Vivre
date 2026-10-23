using System.Security.Cryptography;
using Vivre.Core.Updates;

namespace Vivre.Core.Tests.Updates;

public sealed class CuPackageIoTests : IDisposable
{
    public CuPackageIoTests() => CuPackageIo.ConfigureLimit(1);
    public void Dispose() => CuPackageIo.ConfigureLimit(CuPackageIo.DefaultConcurrency);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public async Task Concurrent_real_copies_and_hashes_obey_limit_and_preserve_all_bytes(int limit)
    {
        CuPackageIo.ConfigureLimit(limit);
        string root = Path.Combine(Path.GetTempPath(), "VivreCuQueue_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "dummy.msu");
            byte[] bytes = RandomNumberGenerator.GetBytes(2 * 1024 * 1024);
            File.WriteAllBytes(source, bytes);
            int active = 0, completed = 0;
            await Task.WhenAll(Enumerable.Range(0, 9).Select(i => CuPackageIo.RunAsync(() =>
            {
                Assert.InRange(Interlocked.Increment(ref active), 1, limit);
                try
                {
                    string target = Path.Combine(root, i + ".msu");
                    SmbAgentLane.CopyPackage(source, target);
                    Assert.Equal(bytes, File.ReadAllBytes(target));
                    Interlocked.Increment(ref completed);
                    return true;
                }
                finally { Interlocked.Decrement(ref active); }
            }, CancellationToken.None)));
            Assert.Equal(9, completed);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Increasing_wakes_waiters_and_decreasing_drains_existing_holders_without_overlap()
    {
        using var releaseFirst = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> first = CuPackageIo.RunAsync(() =>
        {
            firstEntered.SetResult();
            Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(10)));
            return true;
        }, default);
        Task<bool>? second = null, third = null;
        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = CuPackageIo.RunAsync(() =>
            {
                secondEntered.SetResult();
                Assert.True(releaseSecond.Wait(TimeSpan.FromSeconds(10)));
                return true;
            }, default);
            Assert.False(secondEntered.Task.IsCompleted);
            CuPackageIo.ConfigureLimit(2);
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(first.IsCompleted); // Both file jobs can run concurrently at the default of 2.
            third = CuPackageIo.RunAsync(() => true, default);
            Assert.False(third.IsCompleted);
            CuPackageIo.ConfigureLimit(1);
            releaseFirst.Set();
            await first;
            Assert.False(second.IsCompleted); // Lowering the cap never cancels the existing holder.
            Assert.False(third.IsCompleted); // One active job still fills the reduced cap.
            await Assert.ThrowsAsync<TimeoutException>(() => third.WaitAsync(TimeSpan.FromMilliseconds(100)));
            releaseSecond.Set();
            Assert.True(await second);
            Assert.True(await third.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            releaseFirst.Set(); releaseSecond.Set();
            await first;
            if (second is not null) await second;
            if (third is not null) await third;
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(2, 2)]
    [InlineData(200, 200)]
    [InlineData(int.MaxValue, 200)]
    public void Out_of_range_saved_values_are_bounded(int value, int expected)
    {
        CuPackageIo.ConfigureLimit(value);
        Assert.Equal(expected, CuPackageIo.Limit);
    }

    [Fact]
    public async Task Queued_cancellation_does_not_run_work_or_release_another_holders_slot()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task<bool> holder = CuPackageIo.RunAsync(() =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return true;
        }, default);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            using var cancel = new CancellationTokenSource();
            bool ran = false;
            Task<bool> queued = CuPackageIo.RunAsync(() => ran = true, cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(ran);
            Task<bool> next = CuPackageIo.RunAsync(() => true, default);
            Assert.False(next.IsCompleted);
            release.Set();
            Assert.True(await holder);
            Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { release.Set(); await holder; }
    }

    [Fact]
    public async Task Failed_and_precancelled_work_do_not_leak_the_slot()
    {
        await Assert.ThrowsAsync<IOException>(() => CuPackageIo.RunAsync<bool>(() => throw new IOException("dummy failure"), default));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CuPackageIo.RunAsync<bool>(() => throw new Exception("must not run"), cancel.Token));
        Assert.Equal(42, await CuPackageIo.RunAsync(() => 42, default).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Actual_smb_lane_waiter_cancels_without_attempting_remote_drop_or_cleanup()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task<bool> holder = CuPackageIo.RunAsync(() =>
        {
            entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); return true;
        }, default);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            using var cancel = new CancellationTokenSource();
            var messages = new List<string>();
            var lane = new SmbAgentLane(() => [0]);
            Task<HostPatchStatus> run = lane.InstallFullPackageAsync("DUMMY-CU-QUEUE.invalid", "dummy.msu", new(),
                new SyncProgress(s => messages.Add(s.Message)), cancel.Token);
            Assert.Contains(messages, message => message.StartsWith("Queued"));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.DoesNotContain(messages, message => message.StartsWith("Copying"));
        }
        finally { release.Set(); await holder; }
    }

    private sealed class SyncProgress(Action<HostPatchStatus> report) : IProgress<HostPatchStatus>
    {
        public void Report(HostPatchStatus value) => report(value);
    }
}
