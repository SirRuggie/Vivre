using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Vivre.Core.Computers;
using Vivre.Core.Configuration;
using Vivre.Core.Models;
using Vivre.Desktop;
using Vivre.Desktop.ViewModels;

namespace Vivre.PerformanceAudit;

internal static class WorkspaceSafetyProbe
{
    private static readonly Type Actions = typeof(MainWindow).Assembly.GetType("Vivre.Desktop.WorkspaceListActions")!;
    public static object Run(string root)
    {
        using var vm = WorkspaceProbe.CreateWorkspace();
        File.WriteAllText(Path.Combine(root, "settings.json"), "{\"AutoCheckOnLoad\":false}");
        var settings = (AppSettingsStore)Activator.CreateInstance(typeof(AppSettingsStore), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [Path.Combine(root, "settings.json"), null], null)!;
        Set(vm, "_appSettings", settings);
        Set(vm, "_shared", new SharedSettingsStore(Path.Combine(root, "shared")));
        var store = new ComputerListStore(Path.Combine(root, "lists"));
        Set(vm, "_lists", store);
        Require(vm.ListStatus == "Temporary list", "New workspace claims a saved list.");
        var first = new Computer("DUMMY-A") { CommandResult = "Existing result", UpdateMessage = "Existing scan" };
        var second = new Computer("DUMMY-B") { CommandResult = "Keep this too" };
        vm.Computers.Add(first); vm.Computers.Add(second);
        vm.SelectedComputers.Add(first); vm.FocusedComputer = first;
        vm.AddComputers(["dummy-a", "DUMMY-C"]);
        Require(ReferenceEquals(first, vm.Computers[0]) && first.CommandResult == "Existing result"
            && vm.SelectedComputers.Contains(first) && ReferenceEquals(vm.FocusedComputer, first), "Adding lost existing results/selection/focus.");
        vm.SetComputers(["DUMMY-A", "dummy-b", "DUMMY-C"]);
        Require(ReferenceEquals(first, vm.Computers[0]) && vm.SelectedComputers.Contains(first), "Unchanged membership rebuilt rows.");
        vm.SaveCurrentAsList("Identity");
        Require(vm.SavedListName == "Identity" && vm.ListStatus.EndsWith("Saved"), "Successful save lacks identity/feedback.");
        vm.Title = "Renamed tab";
        first.CommandResult = "Existing result";
        RefreshStatus(vm);
        Require(!vm.ListStatus.Contains("Unsaved changes"), "Rename/results dirtied membership.");
        vm.AddComputers(["DUMMY-EXTRA"]);
        RefreshStatus(vm);
        Require(vm.ListStatus.Contains("Unsaved changes"), "Addition failed to mark list changes.");
        vm.SetComputers(["DUMMY-A", "DUMMY-B", "DUMMY-C"]);
        RefreshStatus(vm);
        Require(!vm.ListStatus.Contains("Unsaved changes"), "Restoring saved membership stayed dirty.");
        first.Name = "DUMMY-RENAMED";
        RefreshStatus(vm);
        Require(vm.ListStatus.Contains("Unsaved changes"), "Machine rename missed list changes.");
        first.Name = "DUMMY-A";
        RefreshStatus(vm);
        string beforeFailedSave = vm.ListStatus;
        try { vm.SaveCurrentAsList("Identity"); throw new Exception("Overwrite unexpectedly succeeded."); }
        catch (IOException) { }
        Require(vm.ListStatus == beforeFailedSave && vm.SavedListName == "Identity", "Failed save changed identity/status.");
        vm.RecordListDeleted("unrelated");
        Require(vm.SavedListName == "Identity", "Unrelated delete detached list.");
        vm.RecordListDeleted("IDENTITY");
        Require(vm.SavedListName is null && vm.ListStatus == "Temporary list", "Deleted list still claims saved identity.");
        int prompts = 0;
        bool Deny(string _) { prompts++; return false; }
        Require(!Replace(vm, ["DUMMY-A"], Deny) && vm.Computers.Count == 3, "Cancelled replacement changed membership.");
        Require(prompts == 1 && first.UpdateMessage == "Existing scan", "Cancelled replacement lost results.");
        Require(Replace(vm, ["DUMMY-A", "DUMMY-B", "DUMMY-C", "DUMMY-D"], Deny) && prompts == 1, "Pure additions prompted.");
        vm.IsBusy = true;
        ExpectBlocked(() => Replace(vm, [], _ => throw new Exception("Busy replacement prompted instead of refusing.")));
        Require(vm.Computers.Count == 4, "Busy clear changed rows.");
        Require(Replace(vm, vm.Computers.Select(c => c.Name).ToArray(), Deny), "Unchanged busy list was blocked.");
        vm.IsBusy = false;
        ExpectBlocked(() => Replace(vm, ["DUMMY-A"], _ => { vm.IsBusy = true; return true; }));
        Require(vm.Computers.Count == 4, "Operation starting during confirmation lost rows.");
        vm.IsBusy = false;
        ExpectBlocked(() => Replace(vm, [], _ => { vm.Computers.Add(new Computer("DUMMY-LATE")); return true; }));
        Require(vm.Computers.Count == 5, "Changed membership reused stale approval.");
        Require(Replace(vm, ["DUMMY-A", "DUMMY-B"], _ => true), "Approved replacement failed.");
        Require(vm.Computers.Count == 2 && ReferenceEquals(vm.Computers[0], first) && vm.SelectedComputers.Contains(first), "Replacement destroyed a retained row.");
        vm.SetComputers(["DUMMY-B", "DUMMY-A"]);
        Require(ReferenceEquals(vm.Computers[0], second) && vm.SelectedComputers.Contains(first), "Requested order or selected retained row was lost.");
        VerifyDelayedMonitor(vm);
        store.Save("Saved", ["DUMMY-B"]);
        Require(!Replace(vm, vm.ReadSavedList("Saved"), Deny) && ReferenceEquals(vm.FocusedComputer, first), "Cancelled saved-list open lost focus.");
        Require(Replace(vm, vm.ReadSavedList("Saved"), _ => true) && ReferenceEquals(vm.Computers.Single(), second), "Saved-list replacement rebuilt retained row.");
        Require(vm.SelectedComputers.Count == 0 && vm.FocusedComputer is null, "Removed rows retained selection/focus.");
        Require(!Exit(vm, _ => throw new Exception("Idle workspace prompted.")), "Probe contract error");
        vm.IsBusy = true;
        string warning = "";
        Require(Exit(vm, text => { warning = text; return false; }), "Active exit was not cancelled.");
        Require(vm.IsBusy && warning.Contains("may continue") && warning.Contains("does not undo"), "Exit cancellation changed work or lacked consequences.");
        Require(!Exit(vm, _ => true), "Approved exit was blocked.");
        vm.IsBusy = false;
        second.ScheduledNextRun = DateTime.Now.AddHours(1);
        Require(Exit(vm, _ => false), "Scheduled work was omitted from exit warning.");
        second.ScheduledNextRun = null;
        Require(Replace(vm, [], _ => true) && vm.Computers.Count == 0, "Approved clear failed.");
        for (int i = 0; i < 3190; i++) vm.Computers.Add(new Computer($"DUMMY-LARGE-{i}"));
        int resets = 0, removes = 0;
        vm.Computers.CollectionChanged += (_, e) => { if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++; if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove) removes++; };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Replace(vm, [], _ => true);
        double clearMs = watch.Elapsed.TotalMilliseconds;
        Require(resets == 1 && removes == 0, "Large clear sent per-row removal notifications.");
        int largeClearResetCount = resets;

        var view = new WorkspaceView { DataContext = vm };
        for (int i = 0; i < 3190; i++) vm.Computers.Add(new Computer($"DUMMY-NEAR-{i}"));
        var retained = vm.Computers[2000];
        var grid = (DataGrid)view.FindName("ComputerGrid");
        view.Measure(new Size(1280, 720)); view.Arrange(new Rect(0, 0, 1280, 720)); view.UpdateLayout();
        foreach (var testedGrid in new[] { grid, (DataGrid)view.FindName("UpdateGrid") })
        {
            Require(testedGrid.FrozenColumnCount == 1 && testedGrid.Columns[0].Header is "Name"
                && testedGrid.Columns[0].IsFrozen && !testedGrid.Columns[0].CanUserReorder, "Name column is not fixed in both grids.");
        }
        Require(((TextBlock)view.FindName("ListStatusText")).Text == vm.ListStatus, "Saved-list status binding is not displayed.");
        grid.SelectedItems.Add(retained);
        watch.Restart();
        Replace(vm, [retained.Name], _ => true);
        view.UpdateLayout();
        double nearTotalMs = watch.Elapsed.TotalMilliseconds;
        Require(ReferenceEquals(vm.Computers.Single(), retained) && grid.SelectedItems.Contains(retained)
            && vm.SelectedComputers.Contains(retained), "Near-total replacement lost retained object or actual grid selection.");
        retained.Name = "DUMMY-" + new string('X', 200);
        foreach (bool patching in new[] { false, true })
        {
            vm.IsUpdateMode = patching;
            RefreshStatus(vm);
            view.Measure(new Size(720, 600)); view.Arrange(new Rect(0, 0, 720, 600)); view.UpdateLayout();
            var testedGrid = (DataGrid)view.FindName(patching ? "UpdateGrid" : "ComputerGrid");
            testedGrid.ScrollIntoView(retained);
            testedGrid.UpdateLayout();
            Require(testedGrid.Columns[0].ActualWidth <= 320, "Long frozen Name monopolized the grid.");
            var scroll = VisualChildren<ScrollViewer>(testedGrid).First();
            scroll.ScrollToHorizontalOffset(0); testedGrid.UpdateLayout();
            var cell = VisualChildren<DataGridCell>(testedGrid).First(c => c.Column == testedGrid.Columns[0]);
            double beforeX = cell.TranslatePoint(new Point(), testedGrid).X;
            scroll.ScrollToHorizontalOffset(300); testedGrid.UpdateLayout();
            Require(scroll.HorizontalOffset > 0 && Math.Abs(cell.TranslatePoint(new Point(), testedGrid).X - beforeX) < 1,
                "Name moved during actual horizontal scrolling.");
        }
        view.DataContext = null;
        Replace(vm, [], _ => true);

        var paste = new LoadComputersWindow(vm);
        try
        {
            Require(((RadioButton)paste.FindName("AddMode")).IsChecked == true, "Paste doesn't default to Add.");
            Require(((TextBox)paste.FindName("NamesBox")).Text == "", "Paste contains old names instead of empty input.");
            var apply = (Wpf.Ui.Controls.Button)paste.FindName("ApplyButton");
            Require((string)apply.Content == "Add machines", "Default paste action is ambiguous.");
            ((RadioButton)paste.FindName("ReplaceMode")).IsChecked = true;
            Require((string)apply.Content == "Replace machines", "Replace action label did not follow mode.");
            ((RadioButton)paste.FindName("AddMode")).IsChecked = true;
            ((TextBox)paste.FindName("NamesBox")).Text = "DUMMY-ONE,DUMMY-TWO;dummy-one\tDUMMY-THREE";
            typeof(LoadComputersWindow).GetMethod("OnLoad", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(paste, [paste, new RoutedEventArgs()]);
            Require(vm.Computers.Select(c => c.Name).SequenceEqual(new[] { "DUMMY-ONE", "DUMMY-TWO", "DUMMY-THREE" }), "Actual Paste did not use shared separators.");
        }
        finally { paste.Close(); }
        var prompt = new TextPromptWindow("Save list", "Name") { ValidateAndAccept = ComputerListStore.NameError, AcceptText = "Save list" };
        try
        {
            var input = (Wpf.Ui.Controls.TextBox)prompt.FindName("InputBox");
            input.Text = "A/B";
            typeof(TextPromptWindow).GetMethod("OnOk", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(prompt, [prompt, new RoutedEventArgs()]);
            Require(prompt.Value is null && !string.IsNullOrWhiteSpace(((TextBlock)prompt.FindName("ErrorText")).Text), "Invalid save name closed prompt or hid error.");
            Require(prompt.AcceptText == "Save list", "Save action label missing.");
        }
        finally { prompt.Close(); }
        vm.Dispose();
        ExpectBlocked(() => Replace(vm, [], _ => true));
        return new { pass = true, savedListIdentityAndChanges = true, sharedPasteInput = true, frozenNamesBothGrids = true, actionSpecificLabels = true, preservedRowsResultsSelectionFocus = true, replacementCancelAndApproval = true,
            activeWorkAndStaleApprovalBlocked = true, savedListOpenAndClear = true, exitCancellationAndConsequences = true,
            nearTotalReplacementWithLayoutMs = nearTotalMs, actualGridSelectionRetained = true, largeClearMs = clearMs, largeClearResetCount, delayedMonitorProtected = true, pasteAddDefault = true, inlineNameValidation = true, scope = "Real workspace and production UI templates; isolated dummy storage, no network or production writes" };
    }
    private static void VerifyDelayedMonitor(WorkspaceViewModel vm)
    {
        var pinger = new DelayedPinger();
        Set(vm, "_pinger", pinger);
        var monitor = typeof(WorkspaceViewModel).GetMethod("MonitorRowsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var cancel = new CancellationTokenSource();
        var task = (Task)monitor.Invoke(vm, [new[] { vm.Computers[0] }, cancel.Token])!;
        Require(vm.HasActiveFleetWork && !task.IsCompleted, "Suspended monitor was not tracked.");
        ExpectBlocked(() => Replace(vm, [], _ => true));
        cancel.Cancel(); // fake deliberately ignores cancellation to model an uninterruptible read
        Require(vm.HasActiveFleetWork, "Cancelled but undrained monitor was forgotten.");
        pinger.Release.SetResult(Vivre.Core.Net.PingResult.Offline("Dummy offline"));
        var frame = new System.Windows.Threading.DispatcherFrame();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(10), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => { if (task.IsCompleted || elapsed.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false; }, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        try { System.Windows.Threading.Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Require(task.IsCompleted, "Dummy monitor did not drain.");
        task.GetAwaiter().GetResult();
        Require(!vm.HasActiveFleetWork, "Drained monitor stayed busy.");
    }
    private sealed class DelayedPinger : Vivre.Core.Net.IHostPinger
    {
        public TaskCompletionSource<Vivre.Core.Net.PingResult> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Vivre.Core.Net.PingResult> PingAsync(string host, int timeoutMs, CancellationToken token = default) => Release.Task;
    }

    private static bool Replace(WorkspaceViewModel vm, IEnumerable<string> names, Func<string, bool> confirm) =>
        (bool)Actions.GetMethod("Replace")!.Invoke(null, [vm, names, confirm])!;
    // Return true when exit was cancelled.
    private static bool Exit(WorkspaceViewModel vm, Func<string, bool> confirm) =>
        !(bool)Actions.GetMethod("ConfirmExit")!.Invoke(null, [new[] { vm }, confirm])!;
    private static void ExpectBlocked(Action action)
    {
        try { action(); } catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException or ObjectDisposedException) { return; }
        throw new Exception("Unsafe operation was not blocked.");
    }
    private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in VisualChildren<T>(child)) yield return descendant;
        }
    }
    private static void RefreshStatus(WorkspaceViewModel vm) => typeof(WorkspaceViewModel)
        .GetMethod("RaiseFleetAggregates", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
    private static void Set(object obj, string name, object value) => obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(obj, value);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
