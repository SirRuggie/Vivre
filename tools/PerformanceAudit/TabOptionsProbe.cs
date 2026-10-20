using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Vivre.Core.Models;
using Vivre.Core.Updates;
using Vivre.Desktop;
using Vivre.Desktop.ViewModels;

namespace Vivre.PerformanceAudit;

internal static class TabOptionsProbe
{
    public static object Run()
    {
        var defaults = new PatchOptions();
        using var a = WorkspaceProbe.CreateWorkspaceWithOptions(defaults);
        using var b = WorkspaceProbe.CreateWorkspaceWithOptions(defaults);
        a.SelectedSource = UpdateSource.MicrosoftUpdate;
        a.IncludeDrivers = true;
        a.ExcludeText = "SQL, Silverlight";
        a.IsInstalledMode = true;
        Require(b.SelectedSource == UpdateSource.WindowsUpdate && !b.IncludeDrivers && b.ExcludeText == "" && !b.IsInstalledMode,
            "Tab A changed tab B's display.");
        Require(defaults.Source == UpdateSource.WindowsUpdate && !defaults.IncludeDrivers && defaults.Scope == UpdateScope.Applicable && defaults.ExcludeNameContains.Count == 0,
            "Tab A mutated defaults for future tabs.");
        using var c = WorkspaceProbe.CreateWorkspaceWithOptions(defaults);
        Require(c.UpdateSourceLabel == "Windows Update", "New tab inherited another tab's source.");
        var af = Options(a); var bf = Options(b);
        Require(!ReferenceEquals(af, bf) && !ReferenceEquals(af, defaults), "Options are still shared.");
        Require(af.Source == UpdateSource.MicrosoftUpdate && af.IncludeDrivers && af.Scope == UpdateScope.Installed && af.ExcludeNameContains.SequenceEqual(new[] { "SQL", "Silverlight" }), "A's effective settings differ from its display.");
        Require(bf.Source == UpdateSource.WindowsUpdate && !bf.IncludeDrivers && bf.Scope == UpdateScope.Applicable && bf.ExcludeNameContains.Count == 0, "B's effective settings changed invisibly.");
        b.SelectedSource = UpdateSource.Managed;
        b.ExcludeText = "Defender";
        Require(af.Source == UpdateSource.MicrosoftUpdate && af.ExcludeNameContains.Count == 2, "Reverse-direction leakage.");
        var notifications = new List<string?>();
        b.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        b.SelectedSource = UpdateSource.WindowsUpdate;
        Require(notifications.Contains(nameof(WorkspaceViewModel.UpdateSourceLabel)), "Source label doesn't notify bindings.");
        b.SelectedSource = UpdateSource.Managed;
        Require(b.UpdateSourceLabel == "Managed (WSUS/SCCM)", "Managed label is ambiguous.");

        // Exercise the real install option boundary with inert services, never a remote server.
        var seen = new List<PatchOptions>();
        var scans = new List<PatchOptions>();
        var schedules = new List<PatchOptions>();
        var uninstalls = new List<PatchOptions>();
        foreach (var vm in new[] { a, b, c })
        {
            var patch = DispatchProxy.Create<IPatchService, CapturingPatchProxy>();
            typeof(WorkspaceViewModel).GetField("_patch", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, patch);
            var scan = typeof(WorkspaceViewModel).GetMethod("ScanRowAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ((Task)scan.Invoke(vm, [new Computer("DUMMY-SCAN") { IsOnline = true }, CancellationToken.None])!).GetAwaiter().GetResult();
            scans.Add(((CapturingPatchProxy)(object)patch).Options.Single());
            ((CapturingPatchProxy)(object)patch).Options.Clear();
            var method = typeof(WorkspaceViewModel).GetMethod("InstallRowAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ((Task)method.Invoke(vm, [new Computer("DUMMY-OPTIONS") { OsBuild = 20348 }, CancellationToken.None, null, false, false])!).GetAwaiter().GetResult();
            var captured = ((CapturingPatchProxy)(object)patch).Options;
            seen.Add(captured.Single());
            captured.Clear();
            var at = DateTime.Now.AddHours(1);
            ((Task)method.Invoke(vm, [new Computer("DUMMY-SCHEDULE") { OsBuild = 20348 }, CancellationToken.None, at, false, false])!).GetAwaiter().GetResult();
            schedules.Add(captured.Single());
            Require(schedules[^1].RunBehavior == RunBehavior.ScheduleAt && schedules[^1].ScheduleAt == at, "Schedule lost requested time.");
            captured.Clear();
            var uninstallRow = new Computer("DUMMY-UNINSTALL") { OsBuild = 20348 };
            uninstallRow.InstalledUpdates.Add(new SelectableUpdate(new SoftwareUpdate("Dummy removable update", "1234567", true, 0, 0)));
            var uninstall = typeof(WorkspaceViewModel).GetMethod("UninstallRowAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ((Task)uninstall.Invoke(vm, [uninstallRow, CancellationToken.None])!).GetAwaiter().GetResult();
            uninstalls.Add(captured.Single());
        }
        Require(seen.Select(o => o.Source).SequenceEqual(new[] { UpdateSource.MicrosoftUpdate, UpdateSource.Managed, UpdateSource.WindowsUpdate }), "Actual install received another tab's source.");
        Require(scans.Select(o => o.Source).SequenceEqual(seen.Select(o => o.Source)), "Actual scan source crossed tabs.");
        Require(scans[0].Scope == UpdateScope.Installed && scans[1].Scope == UpdateScope.Applicable && scans[2].Scope == UpdateScope.Applicable, "Actual scan scope crossed tabs.");
        Require(seen[0].IncludeDrivers && seen[0].ExcludeNameContains.Count == 2 && !seen[1].IncludeDrivers && seen[1].ExcludeNameContains.SequenceEqual(new[] { "Defender" }), "Install drivers/exclusions crossed tabs.");

        Require(schedules.Select(o => o.Source).SequenceEqual(seen.Select(o => o.Source)), "Scheduled install source crossed tabs.");
        Require(uninstalls.Select(o => o.Source).SequenceEqual(seen.Select(o => o.Source)), "Uninstall source crossed tabs.");
        Require(uninstalls.All(o => o.Scope == UpdateScope.Installed && o.IncludeKbArticleIds!.SequenceEqual(new[] { "1234567" })), "Uninstall lost checked KB scope.");
        Require(Options(a).Scope == UpdateScope.Installed && Options(b).Scope == UpdateScope.Applicable, "Operation scope leaked back into tab settings.");
        VerifyPendingScanSnapshot();
        var window = new MainWindow(); // production XAML, no Show or App startup
        try
        {
            window.DataContext = new { SelectedTab = a };
            var button = (Wpf.Ui.Controls.Button)window.FindName("UpdateOptionsButton");
            var label = ((StackPanel)button.Content).Children.OfType<TextBlock>().Single();
            var tabs = (TabControl)window.FindName("PatchingTabs");
            var header = (StackPanel)tabs.ItemTemplate.LoadContent();
            header.DataContext = a;
            header.Measure(new Size(600, 50));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Require(label.Text == "Update options" && button.ToolTip.ToString()!.Contains("Microsoft Update"), "Update options lost active source tooltip.");
            Require(header.Children.OfType<TextBlock>().Any(t => t.Text == "Microsoft Update"), "Tab header doesn't show source.");
            window.DataContext = new { SelectedTab = b };
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Require(label.Text == "Update options" && button.ToolTip.ToString()!.Contains("Managed (WSUS/SCCM)"), "Update options tooltip stayed on previous tab's source.");
            window.ToolbarCompact = true;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Require(label.Visibility == Visibility.Collapsed, "Source label bypassed compact toolbar layout.");
            window.ToolbarCompact = false;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Require(label.Visibility == Visibility.Visible, "Source label did not return in expanded layout.");
        }
        finally { window.Close(); }
        return new { pass = true, isolatedSettings = true, newTabDefaults = true, actualScanScopes = scans.Select(o => o.Scope.ToString()).ToArray(), actualInstallSources = seen.Select(o => o.Source.ToString()).ToArray(), productionHeaderAndToolbar = true, scheduledInstallsAndUninstalls = true, compactToolbar = true, pendingScanSnapshot = true, scope = "Real workspaces and WPF bindings with inert install service; no live servers or production writes" };
    }

    private static void VerifyPendingScanSnapshot()
    {
        using var vm = WorkspaceProbe.CreateWorkspace();
        vm.SelectedSource = UpdateSource.MicrosoftUpdate;
        vm.IncludeDrivers = true;
        vm.ExcludeText = "SQL";
        vm.IsInstalledMode = true;
        var patch = DispatchProxy.Create<IPatchService, DelayedScanProxy>();
        var capture = (DelayedScanProxy)(object)patch;
        typeof(WorkspaceViewModel).GetField("_patch", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, patch);
        var scan = typeof(WorkspaceViewModel).GetMethod("ScanRowAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task pending = (Task)scan.Invoke(vm, [new Computer("DUMMY-DELAYED") { IsOnline = true }, CancellationToken.None])!;
        bool stable;
        try
        {
            Require(!pending.IsCompleted && capture.Received is not null, "Scan was not suspended at service boundary.");
            vm.SelectedSource = UpdateSource.Managed;
            vm.IncludeDrivers = false;
            vm.ExcludeText = "Defender";
            vm.IsInstalledMode = false;
            var received = capture.Received!;
            stable = received.Source == UpdateSource.MicrosoftUpdate && received.IncludeDrivers
                && received.Scope == UpdateScope.Installed && received.ExcludeNameContains.SequenceEqual(new[] { "SQL" });
        }
        finally
        {
            capture.Release.TrySetResult(new HostPatchStatus(PatchPhase.Done, "Inert delayed scan complete"));
            var frame = new DispatcherFrame();
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background,
                (_, _) => { if (pending.IsCompleted || deadline.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false; }, Dispatcher.CurrentDispatcher);
            try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
            Require(pending.IsCompleted, "Delayed scan did not complete.");
            pending.GetAwaiter().GetResult();
        }
        Require(stable, "In-flight scan options changed when this tab's settings changed.");
    }

    public class DelayedScanProxy : DispatchProxy
    {
        public PatchOptions? Received;
        public TaskCompletionSource<HostPatchStatus> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name != nameof(IPatchService.ScanAsync)) throw new NotSupportedException(method?.Name);
            Received = (PatchOptions)args![1]!;
            return Release.Task;
        }
    }

    public class CapturingPatchProxy : DispatchProxy
    {
        internal List<PatchOptions> Options { get; } = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name is not (nameof(IPatchService.InstallAsync) or nameof(IPatchService.ScanAsync) or nameof(IPatchService.UninstallAsync)))
                throw new NotSupportedException(method?.Name);
            Options.Add(((PatchOptions)args![1]!).Clone());
            return Task.FromResult(new HostPatchStatus(PatchPhase.Done, "Inert probe complete"));
        }
    }

    private static PatchOptions Options(WorkspaceViewModel vm) => (PatchOptions)typeof(WorkspaceViewModel).GetField("_patchOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
