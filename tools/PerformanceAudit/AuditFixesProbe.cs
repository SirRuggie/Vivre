using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using Vivre.Core.Models;
using Vivre.Desktop;

namespace Vivre.PerformanceAudit;

internal static class AuditFixesProbe
{
    public static object Run()
    {
        using var workspace = WorkspaceProbe.CreateWorkspace();
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var window = new DeployWindow(workspace, [new Computer("DUMMY-01")], _ =>
        {
            started.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Dummy loader was not released.");
            return [new DeployPackage { Name = "late", Path = "C:\\dummy\\late.exe", IsFolder = false }];
        }, () => "dummy-library");
        var load = (Task)typeof(DeployWindow).GetMethod("LoadPackagesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;
        try
        {
            PumpUntil(() => started.IsSet);
            bool dispatcherResponsive = false;
            window.Dispatcher.BeginInvoke(() => dispatcherResponsive = true);
            PumpUntil(() => dispatcherResponsive);
            Require(!load.IsCompleted, "Blocked dummy folder read completed before release.");
            var chosen = new DeployPackage { Name = "chosen", Path = "C:\\dummy\\chosen.exe", IsFolder = false };
            typeof(DeployWindow).GetMethod("SelectBrowsedPackage", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [chosen]);
            release.Set();
            PumpUntil(() => load.IsCompleted);
            load.GetAwaiter().GetResult();
            Require(ReferenceEquals(((ComboBox)window.FindName("PackageBox")).SelectedItem, chosen), "Late load overwrote the browsed choice.");
        }
        finally { release.Set(); window.Close(); }

        var main = new MainWindow { ToolbarCompact = true };
        try
        {
            foreach (string name in new[] { "InstallButton", "ListsButton", "QuickAddBox", "AddButton" })
            {
                var element = (UIElement)main.FindName(name);
                Require(!string.IsNullOrWhiteSpace(UIElementAutomationPeer.CreatePeerForElement(element)?.GetName()), $"Compact toolbar accessible name missing: {name}.");
            }
        }
        finally { main.Close(); }

        var schedule = new ScheduleWindow("reboot", ["DUMMY-01", "DUMMY-02"]);
        try
        {
            var button = (Button)schedule.FindName("ScheduleButton");
            Require(!button.IsDefault, "Reboot scheduling must not be the implicit Enter action.");
            Require(button.Content.ToString()!.Contains("(2)"), "Schedule target count missing.");
            foreach (string field in new[] { "DatePick", "HourBox", "MinuteBox" })
            {
                var element = (UIElement)schedule.FindName(field);
                Require(!string.IsNullOrWhiteSpace(UIElementAutomationPeer.CreatePeerForElement(element)?.GetName()), $"Accessible name missing for {field}.");
            }
            var hint = (TextBlock)schedule.FindName("Hint");
            Require(AutomationProperties.GetLiveSetting(hint) == AutomationLiveSetting.Polite, "Validation hint is not a live region.");
        }
        finally { schedule.Close(); }

        WeakReference detachedStatus = CreateDetachedStatus();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Require(!detachedStatus.IsAlive, "Unloaded accessible status retained a dependency-property subscription.");

        string testRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Vivre-column-test-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(testRoot);
        var settings = (AppSettingsStore)Activator.CreateInstance(typeof(AppSettingsStore), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [System.IO.Path.Combine(testRoot, "settings.json"), null], null)!;
        typeof(Vivre.Desktop.ViewModels.WorkspaceViewModel).GetField("_appSettings", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(workspace, settings);
        var row = new Computer("DUMMY-02");
        workspace.Computers.Add(row);
        row.CustomValues["obsolete"] = new string('x', 10000);
        workspace.CustomColumns.Clear();
        var originalSpec = new Vivre.Core.Columns.CustomColumnSpec("obsolete", "old dummy");
        workspace.CustomColumns.Add(originalSpec);
        workspace.AddCustomColumn(new Vivre.Core.Columns.CustomColumnSpec("obsolete", "replacement dummy"));
        var liveSpec = typeof(Vivre.Desktop.ViewModels.WorkspaceViewModel).GetMethod("IsLiveCustomColumn", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Require(!(bool)liveSpec.Invoke(workspace, [originalSpec])!, "Old captured column spec survived replacement.");
        Require((bool)liveSpec.Invoke(workspace, [workspace.CustomColumns.Single(c => c.Name == "obsolete")])!, "Current column spec was refused.");
        Require(row.CustomValues["obsolete"] is null, "Replacement retained the prior script's payload.");
        row.CustomValues["obsolete"] = "replacement result";
        workspace.RemoveCustomColumn("obsolete");
        Require(row.CustomValues["obsolete"] is null, "Removed column retained its payload.");
        Require(settings.Flush(TimeSpan.FromSeconds(5)).Status == Vivre.Core.IO.SaveFlushStatus.Success, "Dummy column settings did not flush.");
        System.IO.Directory.Delete(testRoot, recursive: true);
        return new { packageLoadDispatcherResponsive = true, lateResultPreservesBrowse = true,
            scheduleCountAndExplicitClick = true, scheduleAutomationNames = true, statusLiveRegion = true, statusSubscriptionReleased = true,
            customColumnRetired = true, scope = "Isolated production WPF controls; no visible screen-reader/native RDP/live server test" };
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference CreateDetachedStatus()
    {
        var status = new TextBlock();
        AccessibleStatus.SetEnabled(status, true);
        status.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        status.Text = "dummy status";
        status.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        return new WeakReference(status);
    }
    private static void PumpUntil(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Dispatcher probe timed out.");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(1);
        }
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
