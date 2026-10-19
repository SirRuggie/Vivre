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
            seen.Add(((CapturingPatchProxy)(object)patch).Options.Single());
        }
        Require(seen.Select(o => o.Source).SequenceEqual(new[] { UpdateSource.MicrosoftUpdate, UpdateSource.Managed, UpdateSource.WindowsUpdate }), "Actual install received another tab's source.");
        Require(scans.Select(o => o.Source).SequenceEqual(seen.Select(o => o.Source)), "Actual scan source crossed tabs.");
        Require(scans[0].Scope == UpdateScope.Installed && scans[1].Scope == UpdateScope.Applicable && scans[2].Scope == UpdateScope.Applicable, "Actual scan scope crossed tabs.");
        Require(seen[0].IncludeDrivers && seen[0].ExcludeNameContains.Count == 2 && !seen[1].IncludeDrivers && seen[1].ExcludeNameContains.SequenceEqual(new[] { "Defender" }), "Install drivers/exclusions crossed tabs.");

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
            Require(label.Text == "Microsoft Update", "Toolbar doesn't identify active source.");
            Require(header.Children.OfType<TextBlock>().Any(t => t.Text == "Microsoft Update"), "Tab header doesn't show source.");
            window.DataContext = new { SelectedTab = b };
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Require(label.Text == "Managed (WSUS/SCCM)", "Toolbar stayed on previous tab's source.");
        }
        finally { window.Close(); }
        return new { pass = true, isolatedSettings = true, newTabDefaults = true, actualScanScopes = scans.Select(o => o.Scope.ToString()).ToArray(), actualInstallSources = seen.Select(o => o.Source.ToString()).ToArray(), productionHeaderAndToolbar = true, scope = "Real workspaces and WPF bindings with inert install service; no live servers or production writes" };
    }

    public class CapturingPatchProxy : DispatchProxy
    {
        internal List<PatchOptions> Options { get; } = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name is not (nameof(IPatchService.InstallAsync) or nameof(IPatchService.ScanAsync)))
                throw new NotSupportedException(method?.Name);
            Options.Add(((PatchOptions)args![1]!).Clone());
            return Task.FromResult(new HostPatchStatus(PatchPhase.Done, "Inert probe complete"));
        }
    }

    private static PatchOptions Options(WorkspaceViewModel vm) => (PatchOptions)typeof(WorkspaceViewModel).GetField("_patchOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
