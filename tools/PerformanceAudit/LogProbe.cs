using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Serilog;
using Serilog.Core;
using Vivre.Core.Logging;
using Vivre.Desktop;

namespace Vivre.PerformanceAudit;

/// <summary>
/// Runtime-only probe for ActivityLog's UI-dispatch admission behavior. The caller must run this on
/// an STA thread with the Desktop project referenced. No window is created and the real file sink is
/// deliberately replaced with a sink-free logger.
/// </summary>
internal static class LogProbe
{
    private const int EntryCap = 2000;
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

        return new
        {
            EntryCap,
            MessageCharacters,
            ImmediateControl = new[] { immediate2000, immediate10000 },
            PausedDispatcher = new[] { paused2000, paused10000 },
            ExpectedFinding = "With the dispatcher paused, every submitted message has queued one real ActivityLog UI callback even though Entries is still empty; therefore the 10,000-message case has 10,000 callbacks pending before the 2,000-entry cap can run.",
            Limits = new[]
            {
                "The production Serilog file sink is bypassed with a sink-free Logger, so these measurements isolate dispatcher admission, retained managed data, collection work, and dispatcher drain time; they do not measure production disk logging.",
                "GC retained-byte deltas are comparative managed-heap samples, not total process working set or proof of a natural UI freeze.",
                "The paused case is an intentional synthetic stall. Pump duration reports the cost of draining this workload on this machine and must not be presented as evidence that an equivalent stall occurs naturally.",
            },
        };
    }

    private static ProbeResult MeasureImmediate(int messageCount)
    {
        ActivityLog log = CreateSinklessActivityLog();
        ForceCollection();
        long retainedBefore = GC.GetTotalMemory(forceFullCollection: true);

        Stopwatch elapsed = Stopwatch.StartNew();
        for (int i = 0; i < messageCount; i++)
        {
            log.Info("PROBE", DummyMessage(i));
        }
        elapsed.Stop();

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
            RetainedBytesAfterDrain: retainedAfter - retainedBefore);
    }

    private static ProbeResult MeasurePausedDispatcher(int messageCount)
    {
        ActivityLog log = CreateSinklessActivityLog();
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

        // The STA has deliberately not pumped since the producer began. ActivityLog.Add issues exactly
        // one Dispatcher.BeginInvoke per call, so every completed submission is pending at this point.
        int entriesBeforeDrain = log.Entries.Count;
        long retainedQueued = GC.GetTotalMemory(forceFullCollection: true) - retainedBefore;

        Stopwatch drain = Stopwatch.StartNew();
        DrainThroughApplicationIdle();
        drain.Stop();

        long retainedDrained = GC.GetTotalMemory(forceFullCollection: true) - retainedBefore;
        return new ProbeResult(
            "UI intentionally paused",
            messageCount,
            QueuedCallbacksBeforeDrain: messageCount,
            EntriesBeforeDrain: entriesBeforeDrain,
            EntriesAfterDrain: log.Entries.Count,
            ProduceMilliseconds: produce.Elapsed.TotalMilliseconds,
            DrainMilliseconds: drain.Elapsed.TotalMilliseconds,
            RetainedBytesBeforeDrain: retainedQueued,
            RetainedBytesAfterDrain: retainedDrained);
    }

    private static ActivityLog CreateSinklessActivityLog()
    {
        var log = (ActivityLog)RuntimeHelpers.GetUninitializedObject(typeof(ActivityLog));
        Logger sinkless = new LoggerConfiguration().CreateLogger();

        FieldInfo fileField = typeof(ActivityLog).GetField("_file", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(ActivityLog).FullName, "_file");
        FieldInfo entriesField = typeof(ActivityLog).GetField("<Entries>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(ActivityLog).FullName, "<Entries>k__BackingField");

        fileField.SetValue(log, sinkless);
        entriesField.SetValue(log, new ObservableCollection<LogEntry>());
        return log;
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

    private sealed record ProbeResult(
        string Mode,
        int SubmittedMessages,
        int QueuedCallbacksBeforeDrain,
        int EntriesBeforeDrain,
        int EntriesAfterDrain,
        double ProduceMilliseconds,
        double DrainMilliseconds,
        long RetainedBytesBeforeDrain,
        long RetainedBytesAfterDrain);
}
