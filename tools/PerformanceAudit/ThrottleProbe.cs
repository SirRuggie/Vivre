using System.Diagnostics;
using System.Reflection;
using Vivre.Core.Threading;

namespace Vivre.PerformanceAudit;

/// <summary>Diagnostic-only measurement of SplitThrottle losing waits.</summary>
internal static class ThrottleProbe
{
    private const int Total = 32;
    private const int Reserved = 4;
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    public static async Task<object> RunAsync() => new
    {
        Probe = nameof(ThrottleProbe),
        Runtime = Environment.Version.ToString(),
        ProcessorCount = Environment.ProcessorCount,
        Configuration = new { Total, Reserved, Active = Total - Reserved },
        Control = new[] { await ControlAsync(319), await ControlAsync(3190) },
        Contended = new[] { await ContendedAsync(319), await ContendedAsync(3190) },
        MetricMeaning = new
        {
            QueuedWaiters = "Literal linked-list nodes reachable from SemaphoreSlim.m_asyncHead.",
            AllocatedBytes = "Process-wide managed allocation delta for the measured interval.",
            ManagedBytes = "GC.GetTotalMemory(false) delta; this alone does not prove retention.",
            DrainMilliseconds = "Time from releasing held active permits until its async queue reached zero."
        }
    };

    private static async Task<object> ControlAsync(int acquisitions)
    {
        using var throttle = new SplitThrottle(Total, Reserved);
        long allocated = GC.GetTotalAllocatedBytes(true);
        long managed = GC.GetTotalMemory(false);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < acquisitions; i++)
        {
            using IDisposable permit = await throttle.AcquirePassiveAsync(CancellationToken.None).ConfigureAwait(false);
        }
        sw.Stop();
        return new
        {
            Acquisitions = acquisitions,
            ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds,
            ActiveQueuedWaiters = CountWaiters(throttle.Active),
            ReservedQueuedWaiters = CountWaiters(GetReserved(throttle)),
            AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
            ManagedBytesDelta = GC.GetTotalMemory(false) - managed
        };
    }

    private static async Task<object> ContendedAsync(int acquisitions)
    {
        using var throttle = new SplitThrottle(Total, Reserved);
        int activePermits = Total - Reserved;
        for (int i = 0; i < activePermits; i++)
            if (!throttle.Active.Wait(0)) throw new InvalidOperationException("Could not hold every active permit.");

        bool released = false;
        var seedPermits = new List<IDisposable>(Reserved);
        try
        {
            long allocated = GC.GetTotalAllocatedBytes(true);
            long managed = GC.GetTotalMemory(false);
            var acquireSw = Stopwatch.StartNew();
            // Hold the reserved pool first. Every remaining acquisition must enter the contested
            // two-wait path before these seeds are released to cycle the cohort.
            for (int i = 0; i < Math.Min(Reserved, acquisitions); i++)
                seedPermits.Add(await throttle.AcquirePassiveAsync(CancellationToken.None).ConfigureAwait(false));
            Task[] pending = Enumerable.Range(0, Math.Max(0, acquisitions - seedPermits.Count))
                .Select(_ => AcquireAndReleaseAsync(throttle)).ToArray();
            foreach (IDisposable permit in seedPermits) permit.Dispose();
            seedPermits.Clear();
            await Task.WhenAll(pending).WaitAsync(CompletionTimeout).ConfigureAwait(false);
            acquireSw.Stop();

            int activeBefore = CountWaiters(throttle.Active);
            int reservedBefore = CountWaiters(GetReserved(throttle));
            long allocatedForAcquires = GC.GetTotalAllocatedBytes(true) - allocated;
            long managedAfterAcquires = GC.GetTotalMemory(false);

            var drainSw = Stopwatch.StartNew();
            throttle.Active.Release(activePermits);
            released = true;
            await WaitForDrainAsync(throttle.Active).ConfigureAwait(false);
            drainSw.Stop();

            return new
            {
                Acquisitions = acquisitions,
                AcquireMilliseconds = acquireSw.Elapsed.TotalMilliseconds,
                ActiveQueuedWaitersBeforeRelease = activeBefore,
                ReservedQueuedWaitersBeforeRelease = reservedBefore,
                ExpectedActiveLosers = Math.Max(0, acquisitions - Reserved),
                ActiveQueuedWaitersAfterDrain = CountWaiters(throttle.Active),
                ReservedQueuedWaitersAfterDrain = CountWaiters(GetReserved(throttle)),
                DrainMilliseconds = drainSw.Elapsed.TotalMilliseconds,
                AllocatedBytesForAcquires = allocatedForAcquires,
                ManagedBytesDeltaAfterAcquires = managedAfterAcquires - managed,
                ManagedBytesDeltaAfterDrain = GC.GetTotalMemory(false) - managed
            };
        }
        finally
        {
            foreach (IDisposable permit in seedPermits) permit.Dispose();
            if (!released)
            {
                throttle.Active.Release(activePermits);
                try { await WaitForDrainAsync(throttle.Active).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
        }
    }

    private static async Task AcquireAndReleaseAsync(SplitThrottle throttle)
    {
        using IDisposable permit = await throttle.AcquirePassiveAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task WaitForDrainAsync(SemaphoreSlim semaphore)
    {
        long started = Stopwatch.GetTimestamp();
        while (CountWaiters(semaphore) != 0)
        {
            if (Stopwatch.GetElapsedTime(started) >= DrainTimeout)
                throw new TimeoutException($"Semaphore queue did not drain within {DrainTimeout}.");
            await Task.Delay(1).ConfigureAwait(false);
        }
    }

    private static SemaphoreSlim GetReserved(SplitThrottle throttle)
    {
        FieldInfo field = typeof(SplitThrottle).GetField("_reserved", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(SplitThrottle).FullName, "_reserved");
        return (SemaphoreSlim)(field.GetValue(throttle) ?? throw new InvalidOperationException("_reserved was null."));
    }

    private static int CountWaiters(SemaphoreSlim semaphore)
    {
        FieldInfo headField = typeof(SemaphoreSlim).GetField("m_asyncHead", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(SemaphoreSlim).FullName, "m_asyncHead");
        object? node = headField.GetValue(semaphore);
        if (node is null) return 0;
        FieldInfo nextField = node.GetType().GetField("Next", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? node.GetType().GetField("m_next", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(node.GetType().FullName, "Next/m_next");
        int count = 0;
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        while (node is not null && visited.Add(node))
        {
            if (++count > 1_000_000) throw new InvalidOperationException("Wait queue traversal limit exceeded.");
            node = nextField.GetValue(node);
        }
        return count;
    }
}
