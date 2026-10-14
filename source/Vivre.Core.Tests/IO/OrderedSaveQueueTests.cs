using System.Collections.Concurrent;
using System.Diagnostics;
using Vivre.Core.IO;

namespace Vivre.Core.Tests.IO;

public class OrderedSaveQueueTests
{
    [Fact]
    public void Blocked_first_write_preserves_newer_save_order()
    {
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var writes = new ConcurrentQueue<int>();
        var queue = new OrderedSaveQueue();

        queue.Enqueue(() =>
        {
            firstStarted.Set();
            Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(5)));
            writes.Enqueue(1);
        });
        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(5)));
        queue.Enqueue(() => writes.Enqueue(2));
        queue.Enqueue(() => writes.Enqueue(3));

        releaseFirst.Set();
        SaveFlushResult result = queue.Flush(TimeSpan.FromSeconds(5));

        Assert.Equal(SaveFlushStatus.Success, result.Status);
        Assert.Null(result.Failure);
        Assert.Equal([1, 2, 3], writes);
    }

    [Fact]
    public void Newer_success_supersedes_failure_and_callback_cannot_block_recovery()
    {
        using var callbackStarted = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        var failure = new IOException("disk unavailable");
        var queue = new OrderedSaveQueue(_ =>
        {
            callbackStarted.Set();
            Assert.True(releaseCallback.Wait(TimeSpan.FromSeconds(5)));
            throw new InvalidOperationException("logging failed");
        });

        queue.Enqueue(() => throw failure);
        SaveFlushResult failed = queue.Flush(TimeSpan.FromSeconds(5));
        Assert.Equal(SaveFlushStatus.Failed, failed.Status);
        Assert.Same(failure, failed.Failure);
        Assert.True(callbackStarted.Wait(TimeSpan.FromSeconds(5)));

        queue.Enqueue(() => { });
        SaveFlushResult recovered = queue.Flush(TimeSpan.FromSeconds(1));
        releaseCallback.Set();

        Assert.Equal(SaveFlushStatus.Success, recovered.Status);
        Assert.Null(recovered.Failure);
        Assert.True(SpinWait.SpinUntil(
            () => queue.LatestCallbackFailure is InvalidOperationException,
            TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Flush_is_bounded_when_a_write_stalls()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var queue = new OrderedSaveQueue();
        queue.Enqueue(() =>
        {
            started.Set();
            release.Wait();
        });
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));

        var stopwatch = Stopwatch.StartNew();
        SaveFlushResult result = queue.Flush(TimeSpan.FromMilliseconds(75));
        stopwatch.Stop();
        release.Set();

        Assert.Equal(SaveFlushStatus.TimedOut, result.Status);
        Assert.Null(result.Failure);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(40), TimeSpan.FromSeconds(2));
        Assert.Equal(SaveFlushStatus.Success, queue.Flush(TimeSpan.FromSeconds(5)).Status);
    }

    [Fact]
    public void Timed_out_flush_includes_the_latest_failure_reason()
    {
        using var stalledStarted = new ManualResetEventSlim();
        using var releaseStalled = new ManualResetEventSlim();
        var failure = new IOException("first write failed");
        var queue = new OrderedSaveQueue();
        queue.Enqueue(() => throw failure);
        Assert.Equal(SaveFlushStatus.Failed, queue.Flush(TimeSpan.FromSeconds(5)).Status);
        queue.Enqueue(() =>
        {
            stalledStarted.Set();
            releaseStalled.Wait();
        });
        Assert.True(stalledStarted.Wait(TimeSpan.FromSeconds(5)));

        SaveFlushResult result = queue.Flush(TimeSpan.FromMilliseconds(75));
        releaseStalled.Set();

        Assert.Equal(SaveFlushStatus.TimedOut, result.Status);
        Assert.Same(failure, result.Failure);
        Assert.Equal(SaveFlushStatus.Success, queue.Flush(TimeSpan.FromSeconds(5)).Status);
    }

    [Fact]
    public void Flush_with_no_accepted_work_succeeds()
    {
        var queue = new OrderedSaveQueue();

        SaveFlushResult result = queue.Flush(TimeSpan.Zero);

        Assert.Equal(SaveFlushStatus.Success, result.Status);
        Assert.Null(result.Failure);
    }

    [Fact]
    public void Writes_never_overlap()
    {
        const int writeCount = 32;
        int active = 0;
        int maximumActive = 0;
        var queue = new OrderedSaveQueue();

        for (int i = 0; i < writeCount; i++)
        {
            queue.Enqueue(() =>
            {
                int nowActive = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, nowActive);
                Thread.Sleep(2);
                Interlocked.Decrement(ref active);
            });
        }

        Assert.Equal(SaveFlushStatus.Success, queue.Flush(TimeSpan.FromSeconds(5)).Status);
        Assert.Equal(1, maximumActive);
    }

    [Fact]
    public void Flush_reports_its_captured_tail_despite_a_later_failure()
    {
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var laterFailure = new IOException("later failure");
        var queue = new OrderedSaveQueue();
        queue.Enqueue(() =>
        {
            firstStarted.Set();
            Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(5)));
            queue.Enqueue(() => throw laterFailure);
        });
        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(5)));

        SaveFlushResult? capturedResult = null;
        var flushThread = new Thread(() => capturedResult = queue.Flush(TimeSpan.FromSeconds(5)));
        flushThread.Start();
        bool flushIsWaiting = SpinWait.SpinUntil(
            () => (flushThread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
            TimeSpan.FromSeconds(5));
        releaseFirst.Set();

        Assert.True(flushIsWaiting);
        Assert.True(flushThread.Join(TimeSpan.FromSeconds(5)));
        SaveFlushResult finalResult = queue.Flush(TimeSpan.FromSeconds(5));
        Assert.Equal(SaveFlushStatus.Success, capturedResult?.Status);
        Assert.Equal(SaveFlushStatus.Failed, finalResult.Status);
        Assert.Same(laterFailure, finalResult.Failure);
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref maximum);
            if (observed >= candidate)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref maximum, candidate, observed) != observed);
    }
}
