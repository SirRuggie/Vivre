using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Threading;
using Vivre.Core.Credentials;
using Vivre.Core.Logging;
using Vivre.Core.Models;
using Vivre.Core.Updates;
using Vivre.Core.Vitals;
using Vivre.Desktop.ViewModels;

namespace Vivre.PerformanceAudit;

/// <summary>
/// Bounded, local-only measurements of the real <see cref="WorkspaceViewModel"/> collection and
/// filtering paths. The console host must create a WPF <c>Application</c> and call this method from
/// its STA main thread. No production <c>App</c> startup, windows, remote calls, or saves are performed;
/// the dispatcher is pumped only to let the production debounce timer settle.
/// </summary>
internal static class WorkspaceProbe
{
    private static readonly int[] RowCounts = [319, 3_190, 10_000];

    public static object Run()
    {
        var measurements = new List<object>(RowCounts.Length);

        foreach (int rowCount in RowCounts)
        {
            using WorkspaceViewModel workspace = CreateWorkspace();

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var fillWatch = Stopwatch.StartNew();
            for (int index = 0; index < rowCount; index++)
            {
                // Deliberately bypass AddComputers: that production path reads shared settings and
                // starts the monitor/auto-check/reaper lanes. ObservableCollection.Add still exercises
                // the real WorkspaceViewModel collection, live-view, subscription, and dirty paths.
                workspace.Computers.Add(new Computer($"MACHINE-{index:D5}"));
            }

            fillWatch.Stop();
            long fillAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            string[] filterSequence =
            [
                "M", "MA", "MAC", "MACH", "MACHI",
                "MACHIN", "MACHINE", "MACHINE-", "MACHINE-0", "MACHINE-00",
            ];
            var filterSteps = new List<object>(filterSequence.Length);
            var typingWatch = Stopwatch.StartNew();
            foreach (string filter in filterSequence)
            {
                var stepWatch = Stopwatch.StartNew();
                workspace.FilterText = filter;
                stepWatch.Stop();
                filterSteps.Add(new
                {
                    filter,
                    elapsedTicks = stepWatch.ElapsedTicks,
                    elapsedMilliseconds = stepWatch.Elapsed.TotalMilliseconds,
                });
            }

            typingWatch.Stop();

            // Production debounces free-text on a DispatcherTimer. Pump only until the expected final
            // result arrives, measuring the complete user-visible settle (the debounce plus final Refresh).
            var settleWatch = Stopwatch.StartNew();
            bool settled = PumpDispatcherUntil(
                () => settleWatch.Elapsed >= TimeSpan.FromMilliseconds(300)
                    && workspace.VisibleRowCount == ExpectedMachine00Rows(rowCount),
                TimeSpan.FromSeconds(10));
            settleWatch.Stop();
            Require(settled, $"The debounced filter did not settle for {rowCount:N0} rows.");

            var countWatch = new Stopwatch();
            long countAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            countWatch.Start();
            int visibleRowsChecksum = 0;
            for (int read = 0; read < 10_000; read++)
            {
                visibleRowsChecksum += workspace.VisibleRowCount;
            }
            countWatch.Stop();
            long countReadAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - countAllocatedBefore;
            Require(
                visibleRowsChecksum == ExpectedMachine00Rows(rowCount) * 10_000,
                $"Cached visible count changed during repeated reads for {rowCount:N0} rows.");

            // State filters must bypass the text debounce. All generated rows have unknown IsOnline, so
            // Offline deterministically produces zero rows and All restores the final text-filter result.
            var offlineFilterWatch = Stopwatch.StartNew();
            workspace.ActiveFilter = RowFilter.Offline;
            offlineFilterWatch.Stop();
            int offlineVisibleRows = workspace.VisibleRowCount;
            var restoreFilterWatch = Stopwatch.StartNew();
            workspace.ActiveFilter = RowFilter.All;
            restoreFilterWatch.Stop();
            int restoredVisibleRows = workspace.VisibleRowCount;
            Require(offlineVisibleRows == 0, $"Offline filter was not immediate for {rowCount:N0} rows.");
            Require(
                restoredVisibleRows == ExpectedMachine00Rows(rowCount),
                $"Clearing the state filter did not immediately restore text-filtered rows for {rowCount:N0} rows.");

            measurements.Add(new
            {
                requestedRows = rowCount,
                actualRows = workspace.Computers.Count,
                fillMilliseconds = fillWatch.Elapsed.TotalMilliseconds,
                fillAllocatedBytes,
                rapidTypingMilliseconds = typingWatch.Elapsed.TotalMilliseconds,
                typingSteps = filterSteps,
                settled,
                settledVisibleRows = workspace.VisibleRowCount,
                settleMillisecondsIncludingDebounce = settleWatch.Elapsed.TotalMilliseconds,
                visibleCountReads = new
                {
                    reads = 10_000,
                    elapsedMilliseconds = countWatch.Elapsed.TotalMilliseconds,
                    allocatedBytes = countReadAllocatedBytes,
                    checksum = visibleRowsChecksum,
                },
                immediateStateFilters = new
                {
                    offlineRefreshMilliseconds = offlineFilterWatch.Elapsed.TotalMilliseconds,
                    restoreRefreshMilliseconds = restoreFilterWatch.Elapsed.TotalMilliseconds,
                    offlineVisibleRows,
                    restoredVisibleRows,
                },
            });
        }

        RetentionResult removal = MeasureRetention(useReset: false);
        RetentionResult reset = MeasureRetention(useReset: true);
        object liveFilterTransitions = MeasureLiveFilterTransitions();
        using (var monitor = CreateWorkspace())
        {
            monitor.IsMonitoring = true; // empty workspace: no server probes
            if (!monitor.StopCommand.CanExecute(null)) throw new InvalidOperationException("Monitor-only Stop disabled");
            monitor.StopCommand.Execute(null);
            if (monitor.IsMonitoring) throw new InvalidOperationException("Stop did not halt monitoring");
        }

        using (var scheduled = CreateWorkspace())
        {
            var tracker = (ScheduledTaskTracker)typeof(WorkspaceViewModel)
                .GetField("_scheduledTasks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scheduled)!;
            var refresh = typeof(WorkspaceViewModel).GetMethod("RefreshScheduledDisplay", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var row = new Computer("DUMMY-SCHEDULE");
            DateTime future = DateTime.Now.AddHours(2);
            tracker.Record(row.Name, "old-install", "Install updates", DateTime.Now.AddMinutes(-1));
            tracker.Record(row.Name, "next-install", "Install updates", future);
            tracker.Record(row.Name, "later-install", "Install updates", future.AddHours(1));
            refresh.Invoke(scheduled, [row]);
            if (row.ScheduledNextRun != future || !row.ScheduledAction!.Contains("+1 more"))
                throw new InvalidOperationException("Later schedules lost or next time incorrect");
            row.UpdateMessage = "Cancel failed — task may still fire";
            refresh.Invoke(scheduled, [row]);
            if (row.UpdateMessage != "Cancel failed — task may still fire") throw new InvalidOperationException("Cancel warning overwritten");
            tracker.Remove(row.Name);
            refresh.Invoke(scheduled, [row]);
            if (row.ScheduledNextRun is not null || row.ScheduledAction is not null) throw new InvalidOperationException("Removed schedule remains");
        }

        return new
        {
            measurements,
            retention = new
            {
                removal = new
                {
                    workspaceAliveAfterDisposeAndGc = removal.Workspace.IsAlive,
                    retainedRow = removal.Row.Name,
                },
                reset = new
                {
                    workspaceAliveAfterDisposeAndGc = reset.Workspace.IsAlive,
                    retainedRow = reset.Row.Name,
                },
                interpretation =
                    "The retained Computer is held strongly by this probe. Alive after Remove indicates an unexpected root; " +
                    "not alive after either Remove or Clear/Reset confirms outgoing PropertyChanged subscriptions no longer root the disposed " +
                    "workspace. GC results are diagnostic evidence, not a deterministic unit test.",
            },
            liveFilterTransitions,
            limitations = new[]
            {
                "No WorkspaceView/DataGrid is created, so binding, cell realization, column measurement, layout, render, and input costs are excluded.",
                "The STA dispatcher is pumped only while waiting for the final debounced filter result; no production App startup or window occurs.",
                "WorkspaceViewModel construction reads the existing per-user settings cache/file through AppSettingsStore; this probe performs no saves.",
                "All remote-capable dependencies are null inert sentinels. IsMonitoring is disabled before rows are added, and AddComputers is never called.",
                "Timings are single-process Stopwatch samples, not a benchmark; use repeated external runs and medians before drawing release conclusions.",
            },
        };
    }

    private static object MeasureLiveFilterTransitions()
    {
        var results = new List<object>();
        RunStateCase("Offline.IsOnline", RowFilter.Offline, c => c.IsOnline = true, c => c.IsOnline = false, c => c.IsOnline = true);
        RunStateCase("UpdatesAvailable.PatchState", RowFilter.UpdatesAvailable, c => c.UpdatePhase = null, c => c.UpdatePhase = "Available", c => c.UpdatePhase = null);
        RunStateCase("UpdatesAvailable.UpdatesAvailable", RowFilter.UpdatesAvailable, c => c.UpdatesAvailable = 0, c => c.UpdatesAvailable = 1, c => c.UpdatesAvailable = 0);
        RunStateCase("UpdatesAvailable.MissingUpdates", RowFilter.UpdatesAvailable, c => c.MissingUpdates = false, c => c.MissingUpdates = true, c => c.MissingUpdates = false);
        RunStateCase("RebootPending.RebootRequired", RowFilter.RebootPending, c => c.RebootRequired = false, c => c.RebootRequired = true, c => c.RebootRequired = false);
        RunStateCase("RebootPending.PatchState", RowFilter.RebootPending, c => c.UpdatePhase = null, c => c.UpdatePhase = "Deferred", c => c.UpdatePhase = null);
        RunStateCase("Errors.PatchState", RowFilter.Errors, c => c.UpdatePhase = null, c => c.UpdatePhase = "Error", c => c.UpdatePhase = null);
        RunStateCase("Errors.LastError", RowFilter.Errors, c => c.LastError = null, c => c.LastError = "probe error", c => c.LastError = null);
        RunStateCase("Errors.UpdateError", RowFilter.Errors, c => c.UpdateError = null, c => c.UpdateError = "probe error", c => c.UpdateError = null);
        RunStateCase("Done.PatchState", RowFilter.Done, c => c.UpdatePhase = null, c => c.UpdatePhase = "Done", c => c.UpdatePhase = null);
        RunStateCase("Unhealthy.VitalityBand", RowFilter.Unhealthy, c => c.VitalityBand = VitalityBand.Healthy, c => c.VitalityBand = VitalityBand.Warning, c => c.VitalityBand = VitalityBand.Healthy);
        RunStateCase("Server2016.OsBuild", RowFilter.Server2016, c => c.OsBuild = 20348, c => c.OsBuild = 14393, c => c.OsBuild = 20348);
        RunStateCase("NotScanned.UpdatePhase", RowFilter.NotScanned, c => c.UpdatePhase = "Scanning", c => c.UpdatePhase = null, c => c.UpdatePhase = "Scanning");
        RunStateCase("Scheduled.ScheduledNextRun", RowFilter.Scheduled, c => c.ScheduledNextRun = null, c => c.ScheduledNextRun = DateTime.Now.AddHours(1), c => c.ScheduledNextRun = null);

        using (WorkspaceViewModel workspace = CreateWorkspace())
        {
            var row = new Computer("OTHER");
            workspace.Computers.Add(row);
            workspace.FilterText = "MATCH";
            Require(PumpDispatcherUntil(() => workspace.VisibleRowCount == 0, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(300)),
                "Name filter did not settle before the live transition probe.");
            row.Name = "MATCH-HOST";
            Require(PumpDispatcherUntil(() => workspace.VisibleRowCount == 1, TimeSpan.FromSeconds(5)),
                "Name mutation did not add the row through live filtering.");
            row.Name = "OTHER";
            Require(PumpDispatcherUntil(() => workspace.VisibleRowCount == 0, TimeSpan.FromSeconds(5)),
                "Name mutation did not remove the row through live filtering.");
            results.Add(new { filter = "Name", included = true, removed = true });
        }

        return new { cases = results, allPassed = true };

        void RunStateCase(string label, RowFilter filter, Action<Computer> excludeInitially, Action<Computer> include, Action<Computer> excludeAgain)
        {
            using WorkspaceViewModel workspace = CreateWorkspace();
            var row = new Computer("STATE-HOST");
            excludeInitially(row);
            workspace.Computers.Add(row);
            ICollectionView view = GetComputersView(workspace);
            int resetCount = 0;
            ((INotifyCollectionChanged)view).CollectionChanged += CountReset;
            workspace.ActiveFilter = filter;
            Require(resetCount == 1, $"{filter} transition raised {resetCount} Reset events instead of one.");
            Require(workspace.VisibleRowCount == 0, $"{filter} initial excluded row remained visible.");
            include(row);
            Require(PumpDispatcherUntil(() => workspace.VisibleRowCount == 1, TimeSpan.FromSeconds(5)),
                $"{filter} relevant property mutation did not include the row.");
            excludeAgain(row);
            Require(PumpDispatcherUntil(() => workspace.VisibleRowCount == 0, TimeSpan.FromSeconds(5)),
                $"{filter} relevant property mutation did not remove the row.");
            ((INotifyCollectionChanged)view).CollectionChanged -= CountReset;
            results.Add(new { filter = label, transitionResetCount = resetCount, included = true, removed = true });

            void CountReset(object? sender, NotifyCollectionChangedEventArgs e)
            {
                if (e.Action == NotifyCollectionChangedAction.Reset) resetCount++;
            }
        }
    }

    private static ICollectionView GetComputersView(WorkspaceViewModel workspace) =>
        (ICollectionView)(typeof(WorkspaceViewModel)
            .GetField("_computersView", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(workspace) ?? throw new InvalidOperationException("Workspace collection view was not found."));

    private static int ExpectedMachine00Rows(int rowCount) => Math.Min(rowCount, 1_000);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static bool PumpDispatcherUntil(Func<bool> condition, TimeSpan timeout, TimeSpan? minimumDuration = null)
    {
        var elapsed = Stopwatch.StartNew();
        bool IsSatisfied() => elapsed.Elapsed >= (minimumDuration ?? TimeSpan.Zero) && condition();
        if (IsSatisfied())
        {
            return true;
        }

        var frame = new DispatcherFrame();
        var timeoutTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = timeout,
        };
        var pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(10),
        };

        timeoutTimer.Tick += Stop;
        pollTimer.Tick += Poll;
        timeoutTimer.Start();
        pollTimer.Start();
        Dispatcher.PushFrame(frame);
        timeoutTimer.Stop();
        pollTimer.Stop();
        timeoutTimer.Tick -= Stop;
        pollTimer.Tick -= Poll;
        return IsSatisfied();

        void Poll(object? sender, EventArgs e)
        {
            if (IsSatisfied())
            {
                frame.Continue = false;
            }
        }

        void Stop(object? sender, EventArgs e) => frame.Continue = false;
    }

    private static WorkspaceViewModel CreateWorkspace()
    {
        var workspace = new WorkspaceViewModel(
            pinger: null!,
            hostProbe: null!,
            configMgr: null!,
            winRm: null!,
            credentials: new CredentialStore(),
            lists: null!,
            activity: new InertActivityLog(),
            scripts: null!,
            patch: null!,
            patchOptions: new PatchOptions(),
            rebootProbe: null!,
            powerShell: null!,
            vitals: null!,
            remediation: null!,
            deployment: null!,
            software: null!,
            customColumns: null!,
            catalogSize: null!,
            reaper: null!,
            forceReboot: null!,
            bootTime: null!);

        // Construction starts an empty monitor loop. Cancel it before adding any row so none of the
        // null remote sentinels can be reached, even if a host pumps the dispatcher concurrently.
        workspace.IsMonitoring = false;
        return workspace;
    }

    private sealed record RetentionResult(WeakReference Workspace, Computer Row);

    private static RetentionResult MeasureRetention(bool useReset)
    {
        RetentionResult result = CreateRetentionCandidate(useReset);
        for (int attempt = 0; attempt < 3 && result.Workspace.IsAlive; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RetentionResult CreateRetentionCandidate(bool useReset)
    {
        WorkspaceViewModel workspace = CreateWorkspace();
        var row = new Computer(useReset ? "RESET-ROW" : "REMOVE-ROW");
        workspace.Computers.Add(row);

        if (useReset)
        {
            workspace.Computers.Clear();
        }
        else
        {
            workspace.Computers.Remove(row);
        }

        // Dispose stops the coalescing DispatcherTimer, eliminating that global timer root. Its
        // current-row cleanup intentionally exercises the difference between Remove and Clear/Reset.
        workspace.Dispose();
        return new RetentionResult(new WeakReference(workspace), row);
    }

    private sealed class InertActivityLog : IActivityLog
    {
        public ObservableCollection<LogEntry> Entries { get; } = [];

        public void Info(string? machine, string message) { }

        public void Warn(string? machine, string message) { }

        public void Error(string? machine, string message) { }

        public void Clear() => Entries.Clear();
    }
}
