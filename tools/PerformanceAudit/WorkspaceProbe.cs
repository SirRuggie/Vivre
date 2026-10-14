using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Vivre.Core.Credentials;
using Vivre.Core.Logging;
using Vivre.Core.Models;
using Vivre.Core.Updates;
using Vivre.Desktop.ViewModels;

namespace Vivre.PerformanceAudit;

/// <summary>
/// Bounded, local-only measurements of the real <see cref="WorkspaceViewModel"/> collection and
/// filtering paths. The console host must create a WPF <c>Application</c> and call this method from
/// its STA main thread. No production <c>App</c> startup, dispatcher pumping, remote calls, or saves
/// are performed here.
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
            var filterWatch = Stopwatch.StartNew();
            foreach (string filter in filterSequence)
            {
                var stepWatch = Stopwatch.StartNew();
                workspace.FilterText = filter;
                int visibleRows = workspace.VisibleRowCount;
                stepWatch.Stop();
                filterSteps.Add(new
                {
                    filter,
                    visibleRows,
                    elapsedTicks = stepWatch.ElapsedTicks,
                    elapsedMilliseconds = stepWatch.Elapsed.TotalMilliseconds,
                });
            }

            filterWatch.Stop();
            measurements.Add(new
            {
                requestedRows = rowCount,
                actualRows = workspace.Computers.Count,
                fillMilliseconds = fillWatch.Elapsed.TotalMilliseconds,
                fillAllocatedBytes,
                filterSequenceMilliseconds = filterWatch.Elapsed.TotalMilliseconds,
                filterSteps,
            });
        }

        RetentionResult removal = MeasureRetention(useReset: false);
        RetentionResult reset = MeasureRetention(useReset: true);

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
                    "alive after Clear/Reset but not Remove is consistent with Reset omitting OldItems and leaving the row's " +
                    "PropertyChanged subscription attached. GC results are diagnostic evidence, not a deterministic unit test.",
            },
            limitations = new[]
            {
                "No WorkspaceView/DataGrid is created, so binding, cell realization, column measurement, layout, render, and input costs are excluded.",
                "The STA dispatcher is not pumped during a measurement, so the 200 ms fleet recompute DispatcherTimer does not tick.",
                "WorkspaceViewModel construction reads the existing per-user settings cache/file through AppSettingsStore; this probe performs no saves.",
                "All remote-capable dependencies are null inert sentinels. IsMonitoring is disabled before rows are added, and AddComputers is never called.",
                "Timings are single-process Stopwatch samples, not a benchmark; use repeated external runs and medians before drawing release conclusions.",
            },
        };
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
