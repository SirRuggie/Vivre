using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Vivre.Core.Logging;
using Vivre.Desktop;

namespace Vivre.PerformanceAudit;

/// <summary>
/// Runtime-only probe for ActivityLog's UI-dispatch admission behavior. The caller must run this on
/// an STA thread with the Desktop project referenced. No window is created and the real file sink is
/// deliberately replaced with a counting sink.
/// </summary>
internal static class LogProbe
{
    private const int EntryCap = 2000;
    private const int PendingEntryCap = 512;
    private const int MessageCharacters = 1024;

    public static object Run()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException("LogProbe.Run must execute on an STA thread.");
        }

        _ = Application.Current ?? new Application();
        // Settle prior probe callbacks before taking comparable heap baselines.
        DrainThroughApplicationIdle();
        ForceCollection();

        ProbeResult immediate2000 = MeasureImmediate(2000);
        ProbeResult immediate10000 = MeasureImmediate(10000);
        ProbeResult paused2000 = MeasurePausedDispatcher(2000);
        ProbeResult paused10000 = MeasurePausedDispatcher(10000);
        VerifyClearOrdering();
        VerifyReentrantAddOrdering();
        VerifyReentrantClearInvalidatesCapturedBatch();

        return new
        {
            EntryCap,
            MessageCharacters,
            ImmediateControl = new[] { immediate2000, immediate10000 },
            PausedDispatcher = new[] { paused2000, paused10000 },
            ExpectedFinding = "A paused dispatcher retains the newest 512 pending entries behind one scheduled drain. Overflow is disclosed by one UI summary, while every submitted message still reaches the durable sink.",
            Limits = new[]
            {
                "The production file sink is replaced by a counting Serilog sink. This proves sink admission for every message but does not measure production disk I/O.",
                "GC retained-byte deltas are comparative managed-heap samples, not total process working set or proof of a natural UI freeze.",
                "The paused case is an intentional synthetic stall. Pump duration reports the cost of draining this workload on this machine and must not be presented as evidence that an equivalent stall occurs naturally.",
            },
        };
    }

    private static ProbeResult MeasureImmediate(int messageCount)
    {
        (ActivityLog log, CountingSink sink) = CreateCountingActivityLog();
        ForceCollection();
        long retainedBefore = GC.GetTotalMemory(forceFullCollection: true);

        Stopwatch elapsed = Stopwatch.StartNew();
        for (int i = 0; i < messageCount; i++)
        {
            log.Info("PROBE", DummyMessage(i));
        }
        elapsed.Stop();

        Assert(sink.Count == messageCount, $"Durable sink received {sink.Count} of {messageCount} messages.");
        Assert(log.Entries.Count == Math.Min(messageCount, EntryCap), "Immediate UI entry cap was not enforced.");

        long retainedAfter = GC.GetTotalMemory(forceFullCollection: true);
        return new ProbeResult(
            "UI-immediate control",
            messageCount,
            QueuedCallbacksBeforeDrain: 0,
            EntriesBeforeDrain: log.Entries.Count,
            EntriesAfterDrain: log.Entries.Count,
            ProduceMilliseconds: elapsed.Elapsed.TotalMilliseconds,
            DrainMilliseconds: 0,
            RetainedBytesBeforeDrain: retainedAfter - retainedBefore,
            RetainedBytesAfterDrain: retainedAfter - retainedBefore,
            DurableWrites: sink.Count);
    }

    private static ProbeResult MeasurePausedDispatcher(int messageCount)
    {
        (ActivityLog log, CountingSink sink) = CreateCountingActivityLog();
        ForceCollection();
        long retainedBefore = GC.GetTotalMemory(forceFullCollection: true);

        Stopwatch produce = Stopwatch.StartNew();
        Task.Run(() =>
        {
            for (int i = 0; i < messageCount; i++)
            {
                log.Info("PROBE", DummyMessage(i));
            }
        }).GetAwaiter().GetResult();
        produce.Stop();

        Assert(sink.Count == messageCount, $"Durable sink received {sink.Count} of {messageCount} messages.");
        int entriesBeforeDrain = log.Entries.Count;
        Assert(entriesBeforeDrain == 0, "The paused dispatcher unexpectedly processed UI entries.");
        long retainedQueued = GC.GetTotalMemory(forceFullCollection: true) - retainedBefore;

        Stopwatch drain = Stopwatch.StartNew();
        DrainThroughApplicationIdle();
        drain.Stop();

        int expectedEntries = Math.Min(messageCount, PendingEntryCap) + (messageCount > PendingEntryCap ? 1 : 0);
        Assert(log.Entries.Count == expectedEntries, $"Expected {expectedEntries} drained UI entries, got {log.Entries.Count}.");
        if (messageCount > PendingEntryCap)
        {
            Assert(log.Entries[0].Message.Contains("omitted", StringComparison.OrdinalIgnoreCase), "Overflow summary is missing.");
            Assert(log.Entries[1].Message.StartsWith($"{messageCount - 1:D8}:", StringComparison.Ordinal), "Newest pending entry was not retained.");
        }

        long retainedDrained = GC.GetTotalMemory(forceFullCollection: true) - retainedBefore;
        return new ProbeResult(
            "UI intentionally paused",
            messageCount,
            QueuedCallbacksBeforeDrain: 1,
            EntriesBeforeDrain: entriesBeforeDrain,
            EntriesAfterDrain: log.Entries.Count,
            ProduceMilliseconds: produce.Elapsed.TotalMilliseconds,
            DrainMilliseconds: drain.Elapsed.TotalMilliseconds,
            RetainedBytesBeforeDrain: retainedQueued,
            RetainedBytesAfterDrain: retainedDrained,
            DurableWrites: sink.Count);
    }

    private static (ActivityLog Log, CountingSink Sink) CreateCountingActivityLog()
    {
        var sink = new CountingSink();
        Logger logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        ConstructorInfo constructor = typeof(ActivityLog).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(Logger)],
            modifiers: null)
            ?? throw new MissingMethodException(typeof(ActivityLog).FullName, ".ctor(Logger)");
        return ((ActivityLog)constructor.Invoke([logger]), sink);
    }

    private static void VerifyClearOrdering()
    {
        (ActivityLog log, CountingSink sink) = CreateCountingActivityLog();
        Task.Run(() =>
        {
            log.Info("PROBE", "before-clear");
            log.Clear();
            log.Info("PROBE", "after-clear");
        }).GetAwaiter().GetResult();

        DrainThroughApplicationIdle();
        Assert(log.Entries.Count == 1 && log.Entries[0].Message == "after-clear",
            "Clear did not discard only the UI activity that preceded it.");
        Assert(sink.Count == 2, "Clear discarded a durable log event.");
    }

    private static void VerifyReentrantAddOrdering()
    {
        (ActivityLog log, CountingSink sink) = CreateCountingActivityLog();
        bool injected = false;
        log.Entries.CollectionChanged += (_, e) =>
        {
            if (!injected && e.NewItems?.OfType<LogEntry>().Any(x => x.Message == "older") == true)
            {
                injected = true;
                log.Info("PROBE", "reentrant");
            }
        };

        Task.Run(() =>
        {
            log.Info("PROBE", "older");
            log.Info("PROBE", "newer");
        }).GetAwaiter().GetResult();
        DrainThroughApplicationIdle();

        Assert(log.Entries.Select(x => x.Message).SequenceEqual(["reentrant", "newer", "older"]),
            "A reentrant log write was not deferred behind the captured batch in newest-first order.");
        Assert(sink.Count == 3, "A reentrant log write did not reach the durable sink.");
    }

    private static void VerifyReentrantClearInvalidatesCapturedBatch()
    {
        (ActivityLog log, CountingSink sink) = CreateCountingActivityLog();
        bool cleared = false;
        log.Entries.CollectionChanged += (_, e) =>
        {
            if (!cleared && e.NewItems?.OfType<LogEntry>().Any(x => x.Message == "older") == true)
            {
                cleared = true;
                log.Clear();
                log.Info("PROBE", "after-clear");
            }
        };

        Task.Run(() =>
        {
            log.Info("PROBE", "older");
            log.Info("PROBE", "newer");
        }).GetAwaiter().GetResult();
        DrainThroughApplicationIdle();

        Assert(log.Entries.Count == 1 && log.Entries[0].Message == "after-clear",
            "A reentrant Clear allowed captured pre-clear entries to reappear.");
        Assert(sink.Count == 3, "Reentrant Clear discarded a durable log event.");
    }

    private static void DrainThroughApplicationIdle()
    {
        Dispatcher dispatcher = Application.Current.Dispatcher;
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static string DummyMessage(int index) => $"{index:D8}:{new string('x', MessageCharacters - 9)}";

    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class CountingSink : ILogEventSink
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public void Emit(LogEvent logEvent) => Interlocked.Increment(ref _count);
    }

    private sealed record ProbeResult(
        string Mode,
        int SubmittedMessages,
        int QueuedCallbacksBeforeDrain,
        int EntriesBeforeDrain,
        int EntriesAfterDrain,
        double ProduceMilliseconds,
        double DrainMilliseconds,
        long RetainedBytesBeforeDrain,
        long RetainedBytesAfterDrain,
        int DurableWrites);
}
