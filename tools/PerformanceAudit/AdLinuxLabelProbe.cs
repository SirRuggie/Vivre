using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Vivre.Core.Computers;
using Vivre.Core.Models;
using Vivre.Desktop;

namespace Vivre.PerformanceAudit;

internal static class AdLinuxLabelProbe
{
    public static object Run()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        try
        {
            Task<object> task = RunAsync();
            var frame = new DispatcherFrame();
            var deadline = Stopwatch.StartNew();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background,
                (_, _) => { if (task.IsCompleted || deadline.Elapsed > TimeSpan.FromSeconds(15)) frame.Continue = false; }, dispatcher);
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
            if (!task.IsCompleted) throw new TimeoutException("AD label probe stalled.");
            return task.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static async Task<object> RunAsync()
    {
        var lookup = new DelayedLookup();
        using var workspace = WorkspaceProbe.CreateWorkspace(lookup);
        var rows = Enumerable.Range(0, 1000).Select(i => new Computer($"DUMMY-{i:D4}")).ToArray();
        rows[2].OperatingSystem = "Existing independently read OS";
        var watch = Stopwatch.StartNew();
        foreach (var row in rows) workspace.Computers.Add(row);
        double addMs = watch.Elapsed.TotalMilliseconds;
        await lookup.Started.Task;
        if (rows.Any(r => r.DirectoryOperatingSystem is not null)) throw new Exception("Lookup completed before release.");
        workspace.Computers.Remove(rows[0]);
        rows[1].Name = "RENAMED";
        int heartbeats = 0;
        var heartbeat = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Normal,
            (_, _) => heartbeats++, Dispatcher.CurrentDispatcher);
        await Task.Delay(200);
        heartbeat.Stop();
        if (heartbeats < 3) throw new Exception("UI did not remain responsive during delayed AD lookup.");
        lookup.Release.TrySetResult();
        await Until(() => rows.Skip(2).All(r => r.DirectoryOsLabel == "Linux") && rows[1].DirectoryOsLabel == "Linux");
        if (rows[0].DirectoryOperatingSystem is not null) throw new Exception("Removed row received stale label.");
        if (rows[1].DirectoryOperatingSystem != "Linux for RENAMED") throw new Exception("Rename received old result.");
        if (rows[2].OperatingSystem != "Existing independently read OS") throw new Exception("AD changed operational OS fact.");
        if (lookup.MaxBatch > 64 || lookup.MaxActive != 1) throw new Exception("Unbounded workspace lookup.");
        var view = new WorkspaceView { DataContext = workspace };
        var grid = (DataGrid)view.FindName("ComputerGrid");
        var column = (DataGridTemplateColumn)grid.Columns.Single(c => Equals(c.Header, "Name"));
        var cell = (FrameworkElement)column.CellTemplate.LoadContent();
        cell.DataContext = rows[2];
        cell.Measure(new Size(500, 50)); cell.Arrange(new Rect(0, 0, 500, 50));
        await Dispatcher.Yield(DispatcherPriority.Background);
        var badge = ((Panel)cell).Children.OfType<TextBlock>().Last();
        if (badge.Text != "Linux" || badge.Visibility != Visibility.Visible || column.SortMemberPath != "Name")
            throw new Exception("Production name template did not show Linux or preserve sorting.");
        var disposalLookup = new DelayedLookup();
        var disposedWorkspace = WorkspaceProbe.CreateWorkspace(disposalLookup);
        var disposedRow = new Computer("CLOSED");
        disposedWorkspace.Computers.Add(disposedRow);
        await disposalLookup.Started.Task;
        disposedWorkspace.Dispose();
        disposalLookup.Release.TrySetResult();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (disposedRow.DirectoryOperatingSystem is not null) throw new Exception("Closed workspace received result.");
        using var outageWorkspace = WorkspaceProbe.CreateWorkspace(new UnavailableLookup());
        var known = new Computer("KNOWN") { DirectoryOperatingSystem = "Linux" };
        outageWorkspace.Computers.Add(known);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (known.DirectoryOsLabel != "Linux") throw new Exception("Unavailable AD erased an existing label.");
        var resetLookup = new DelayedLookup();
        using var resetWorkspace = WorkspaceProbe.CreateWorkspace(resetLookup);
        var outgoing = new Computer("REPLACED");
        resetWorkspace.Computers.Add(outgoing);
        await resetLookup.Started.Task;
        resetWorkspace.Computers.Clear();
        var incoming = new Computer("REPLACED");
        resetWorkspace.Computers.Add(incoming);
        resetLookup.Release.TrySetResult();
        await Until(() => incoming.DirectoryOsLabel == "Linux");
        if (outgoing.DirectoryOperatingSystem is not null) throw new Exception("Reset row received a late result.");
        return new { pass = true, rows = rows.Length, addMs, delayedDirectoryMs = 200, uiHeartbeats = heartbeats,
            lookup.Calls, lookup.MaxBatch, lookup.MaxActive, removedRowProtected = true, renamedRowProtected = true,
            closedTabProtected = true, resetProtected = true, outagePreservesLabel = true, operationalOsUnchanged = true, renderedLinuxBadge = badge.Text,
            scope = "Real workspace and name template, fake delayed AD, no network/remote scan or production writes" };
    }

    private static async Task Until(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Labels did not complete.");
            await Task.Delay(10);
        }
    }

    private sealed class UnavailableLookup : IAdLinuxLabelLookup
    {
        public Task<AdLinuxLabels> LookupAsync(IReadOnlyList<string> names, CancellationToken token) =>
            Task.FromResult(new AdLinuxLabels(new Dictionary<string, string>(), Available: false));
    }

    private sealed class DelayedLookup : IAdLinuxLabelLookup
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls, MaxBatch, MaxActive;
        private int _active;
        public async Task<AdLinuxLabels> LookupAsync(IReadOnlyList<string> names, CancellationToken token)
        {
            Calls++; MaxBatch = Math.Max(MaxBatch, names.Count); MaxActive = Math.Max(MaxActive, ++_active);
            Started.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(token);
                return new(names.ToDictionary(n => n, n => "Linux for " + n, StringComparer.OrdinalIgnoreCase));
            }
            finally { _active--; }
        }
    }
}
