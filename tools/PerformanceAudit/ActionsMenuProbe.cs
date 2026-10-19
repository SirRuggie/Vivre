using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Vivre.Core.Models;
using Vivre.Desktop;

namespace Vivre.PerformanceAudit;

internal static class ActionsMenuProbe
{
    public static object Run()
    {
        using var vm = WorkspaceProbe.CreateWorkspace();
        var a = new Computer("DUMMY-A");
        var b = new Computer("DUMMY-B");
        var c = new Computer("DUMMY-C");
        vm.Computers.Add(a); vm.Computers.Add(b); vm.Computers.Add(c);
        vm.SelectedComputers.Add(a); vm.SelectedComputers.Add(b);
        int launches = 0, rejected = 0;
        bool current = true;
        var actions = new List<WorkspaceAction>();
        ContextMenu Create() => WorkspaceActions.CreateMenu(vm, () => current, (action, targets) =>
        {
            Require(targets.Count == 2 && ReferenceEquals(targets[0], a) && ReferenceEquals(targets[1], b), "Targets widened or changed.");
            actions.Add(action); launches++;
        }, () => rejected++);
        void Click(ContextMenu menu) => ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var menu = Create();
        Require(menu.Items.Cast<MenuItem>().Any(i => i.Header.ToString()!.StartsWith("WhatsUp Gold maintenance")), "Maintenance label is ambiguous.");
        Require(launches == 0 && menu.Items.Count == 4, "Opening menu launched work or has extra entries.");
        foreach (MenuItem item in menu.Items) item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Require(launches == 4 && actions.Distinct().Count() == 4, "Missing shortcut dispatch.");
        current = false; Click(menu); current = true;
        vm.SelectedComputers.Remove(b); Click(menu);
        vm.SelectedComputers.Add(c); Click(menu);
        vm.SelectedComputers.Remove(c); vm.SelectedComputers.Add(b);
        a.Name = "DUMMY-RENAMED"; Click(menu); a.Name = "DUMMY-A";
        vm.Computers.Remove(b); Click(menu); vm.Computers.Add(b);
        Require(launches == 4 && rejected == 5, "Stale selection/tab wasn't rejected.");
        vm.SelectedComputers.Clear();
        menu = Create();
        Require(menu.Items.Cast<MenuItem>().All(i => !i.IsEnabled), "Empty selection enabled.");
        Click(menu);
        WorkspaceActions.Show(null, vm, WorkspaceAction.RunScript, Array.Empty<Computer>());
        Require(launches == 4, "Empty selection launched work.");

        var window = new MainWindow { DataContext = new { SelectedTab = vm } };
        try
        {
            var button = (FrameworkElement)window.FindName("SelectedActionsButton");
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            Require(!button.IsEnabled, "Toolbar enabled without selection.");
            vm.SetSelection(new[] { a });
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            Require(button.IsEnabled, "Toolbar did not enable for selection.");
            window.ToolbarCompact = true;
            Require(System.Windows.Automation.AutomationProperties.GetName(button) == "Actions for selected machines", "Compact action lacks accessible label.");
            vm.SetSelection(Array.Empty<Computer>());
        }
        finally { window.DataContext = null; window.Close(); }
        var view = new WorkspaceView { DataContext = vm };
        var build = typeof(WorkspaceView).GetMethod("BuildContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var field = typeof(WorkspaceView).GetField("_gridMenu", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (bool patching in new[] { false, true })
        {
            vm.IsUpdateMode = patching;
            vm.SetSelection(Array.Empty<Computer>());
            build.Invoke(view, new object[] { vm });
            var grid = (ContextMenu)field.GetValue(view)!;
            var items = grid.Items.OfType<MenuItem>().ToArray();
            MenuItem Find(string name) => items.Single(i => (string)i.Header == name);
            Require(Find("Connection").Items.Count == 2 && Find("WhatsUp Gold").Items.Count == 2, "Grouping lost commands.");
            Require(!Find("SCCM client actions").IsEnabled, "SCCM actions enabled without selection.");
            Require(!Find("Copy").Items.OfType<MenuItem>().Single(i => i.Header.ToString()!.StartsWith("Selected rows")).IsEnabled, "Copy rows enabled without selection.");
            Require(!Find("Connection").Items.OfType<MenuItem>().Single(i => i.Header.ToString()!.StartsWith("Enable WinRM")).IsEnabled, "Enable WinRM enabled without selection.");
            var script = Find("Run script");
            Require(!((MenuItem)script.Items[0]).IsEnabled, "Selected script enabled without selection.");
            Require(items.Any(i => (string)i.Header == "Software"), "Software menu lost.");
            Require(Find("Software").Items.OfType<MenuItem>().All(i => i.Header.ToString()!.Contains("all in tab (3)")), "Empty-selection software scope wrong.");
            Require(Find("Copy").Items.OfType<MenuItem>().Single(i => i.Header.ToString()!.StartsWith("Update message")).Header.ToString()!.Contains("this machine"), "Clicked field scope missing.");
            vm.SetSelection(new[] { a });
            build.Invoke(view, new object[] { vm });
            items = grid.Items.OfType<MenuItem>().ToArray();
            Require(Find("Software").Items.OfType<MenuItem>().All(i => i.Header.ToString()!.Contains("selected (1)")), "Selected software scope wrong.");
            Require(Find("Schedule").Items.OfType<MenuItem>().Single(i => i.Header.ToString()!.StartsWith("Cancel Vivre")).Header.ToString()!.Contains("selected (1)"), "Cancel scope missing.");
            Require(Find("SCCM client actions").IsEnabled && Find("SCCM client actions").Items.Count == 5, "SCCM selection commands lost.");
            Require(Find("Copy").Items.OfType<MenuItem>().Single(i => i.Header.ToString()!.StartsWith("Selected rows")).IsEnabled, "Selected copy not enabled.");
            Require(Find("Connection").Items.OfType<MenuItem>().Single(i => i.Header.ToString()!.StartsWith("Enable WinRM")).IsEnabled, "Selected Enable WinRM not enabled.");
        }
        vm.SetSelection(new[] { a });
        var valid = WorkspaceActions.CaptureContextValidity(vm);
        Require(valid(), "Fresh menu invalid.");
        vm.SetSelection(new[] { b });
        Require(!valid(), "Changed selection accepted.");
        vm.SetSelection(new[] { a });
        a.Name = "RENAMED";
        Require(!valid(), "Renamed menu target accepted.");
        a.Name = "DUMMY-A";
        vm.Computers.Remove(c);
        Require(!valid(), "Changed tab membership accepted.");
        vm.Computers.Add(c);
        int dispatched = 0, menuRejected = 0;
        bool fresh = true;
        var guarded = WorkspaceActions.GuardContextItem(new MenuItem(), () => fresh, () => menuRejected++);
        guarded.Click += (_, _) => dispatched++;
        guarded.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        fresh = false;
        guarded.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Require(dispatched == 1 && menuRejected == 1, "Stale routed click reached its action.");
        int reviews = 0, executed = 0, stale = 0;
        string prompt = "";
        Task Execute(IReadOnlyList<Computer> rows)
        {
            Require(rows.Count == 1 && ReferenceEquals(rows[0], a), "Cancellation retargeted.");
            executed++;
            return Task.CompletedTask;
        }
        Task Review(Func<string, Task<bool>> confirm) =>
            ScheduledTaskCancellation.ReviewAsync(vm, confirm, Execute, () => stale++);
        Review(text => { reviews++; prompt = text; return Task.FromResult(false); }).GetAwaiter().GetResult();
        Require(executed == 0 && prompt.Contains("1 selected") && prompt.Contains("Vivre_*") && prompt.Contains("already started"), "Cancellation disclosure/refusal failed.");
        Review(text => { vm.SetSelection(new[] { b }); return Task.FromResult(true); }).GetAwaiter().GetResult();
        Require(executed == 1, "Approval failed or retargeted after selection change.");
        vm.SetSelection(new[] { a });
        Review(text => { a.Name = "RENAMED"; return Task.FromResult(true); }).GetAwaiter().GetResult();
        a.Name = "DUMMY-A";
        Review(text => { vm.Computers.Remove(a); return Task.FromResult(true); }).GetAwaiter().GetResult();
        vm.Computers.Add(a);
        Require(executed == 1 && stale == 2, "Changed cancellation targets dispatched.");
        vm.SetSelection(Array.Empty<Computer>());
        Review(text => { reviews++; return Task.FromResult(true); }).GetAwaiter().GetResult();
        Require(reviews == 1 && executed == 1, "Empty cancellation prompted or dispatched.");

        // Exercise the old widening boundary with null operational services: empty must return.
        typeof(WorkspaceView).GetMethod("OpenScriptRunner", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, new object?[] { Array.Empty<Computer>(), null });
        view.DataContext = null;
        using var closed = WorkspaceProbe.CreateWorkspace();
        closed.Computers.Add(a); closed.SetSelection(new[] { a });
        var closedValidity = WorkspaceActions.CaptureContextValidity(closed);
        ScheduledTaskCancellation.ReviewAsync(closed, _ => { closed.Dispose(); return Task.FromResult(true); },
            Execute, () => stale++).GetAwaiter().GetResult();
        Require(stale == 3 && executed == 1 && !closedValidity(), "Closed tab dispatched cancellation or passed scope guard.");
        return new { passed = true, shortcuts = 4, staleCasesRejected = rejected, emptySelectionSafe = true, contextGroupsBothModes = true, liveOperations = false };
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
