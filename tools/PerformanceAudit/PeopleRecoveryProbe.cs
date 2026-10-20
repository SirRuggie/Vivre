using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using Vivre.Core.People;
using Vivre.Core.Scripts;
using Vivre.Desktop;
using Vivre.Desktop.ViewModels;

namespace Vivre.PerformanceAudit;

internal static class PeopleRecoveryProbe
{
    private static readonly DirectoryPerson Alice = new("dummy-id", "Alice Example", "alice", "alice@example.test", "alice@example.test", "Test", "Engineer", "Test", true);
    private static PersonLookupResult Exact() => new([new(Alice, "Exact", 0)], true);
    public static object Run(string root, string renderRoot)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            VerifyRemoval(root);
            VerifyPendingSubmissions(root);
            VerifyExitAndExport(root);
            VerifyCloseVeto(root);
            VerifyDialogLayout(renderRoot);
            return new { pass = true, removeUndoPreservesRowsAndDecisions = true, busyAndStaleGuards = true,
                pendingSubmissionAndOverlapGuards = true, copyIsNotExport = true, exportCancelFailureAndSuccess = true, busySnapshotAndClose = true,
                shellVetoPreservesSession = true, exitDialogDefaultAndMinimumLayout = true, scope = "Dummy identities, intercepted clipboard/save path, real temporary CSV writes and offscreen WPF dialogs; no live AD or server work" };
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static PeopleView View(PeopleViewModel vm, string root, string name)
    {
        var store = (AppSettingsStore)Activator.CreateInstance(typeof(AppSettingsStore), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [Path.Combine(root, name + ".json"), null], null)!;
        return new PeopleView(vm, store);
    }
    private static void VerifyRemoval(string root)
    {
        int queries = 0;
        using var vm = new PeopleViewModel(new Fake((_, _) => { queries++; return Task.FromResult(Exact()); }));
        using var view = View(vm, root, "remove");
        vm.Add("first\nsecond\nthird\nfourth"); vm.LookupAsync().GetAwaiter().GetResult();
        var original = vm.Rows.ToArray();
        vm.StartReview(original[2]); vm.SelectedCandidate = original[2].Candidates[0]; vm.Accept();
        vm.StartReview(original[2]); vm.SelectedCandidate = original[2].Candidates[0];
        vm.SearchText = "first";
        var grid = (DataGrid)view.FindName("ResultsGrid");
        Layout(view); grid.SelectedItem = original[0];
        MenuItem RemoveItem()
        {
            Call(view, "BuildRowMenu");
            return grid.ContextMenu.Items.OfType<MenuItem>().Single(i => i.Header.ToString()!.StartsWith("Remove from results"));
        }
        RemoveItem().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Require(vm.Rows.SequenceEqual(original.Skip(1)) && ReferenceEquals(vm.ReviewRow, original[2]) && vm.SelectedCandidate is not null,
            "Filtered removal touched hidden rows or unrelated review.");
        Require(vm.HasUndoRemoval && vm.CanUndoRemoval && vm.SearchText == "first", "Undo or filters lost.");
        ((Button)view.FindName("UndoRemovalButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(vm.Rows.SequenceEqual(original) && original[2].Detail.StartsWith("Reviewed:") && queries == 4 && !vm.HasUndoRemoval,
            "Undo lost row order/results/review decisions or queried AD.");
        vm.ClearFilters(); Layout(view); grid.SelectedItem = original[0];
        var stale = RemoveItem(); grid.SelectedItem = original[1];
        stale.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Require(vm.Rows.SequenceEqual(original), "Stale menu removed a different selection.");
        vm.MarkExported();
        Require(vm.RemoveRows([original[1], original[2]]) == 2 && !vm.HasReview && vm.HasUnexportedResults, "Removal left a stale review or clean export state.");
        vm.MarkExported(); vm.UndoRemoval(); Require(vm.HasUnexportedResults && vm.Rows.SequenceEqual(original), "Undo after export didn't require re-export.");
        vm.RemoveRows([original[0]]); vm.Add("second"); Require(vm.HasUndoRemoval, "Duplicate paste invalidated undo.");
        vm.Add("new entry"); Require(!vm.HasUndoRemoval, "Result changes retained stale undo.");
        Require(vm.RemoveRows([original[0], original[1]]) == 0 && vm.Rows.Contains(original[1]), "Missing removal target partially removed others.");
        vm.StartReview(original[0]); Require(vm.ReviewRow is null, "Removed entry reopened review.");
        try { vm.Edit(original[0], "ghost"); throw new Exception("Editing a removed row succeeded."); } catch (InvalidOperationException) { }
        vm.RemoveRows([original[1]]);
        var adding = vm.AddAsync(string.Join('\n', Enumerable.Range(0, 250).Select(i => "DUMMY-" + i)));
        Require(vm.IsAdding && !vm.CanUndoRemoval && vm.RemoveRows([original[2]]) == 0, "Removal/undo allowed while adding.");
        vm.UndoRemoval(); Require(!vm.Rows.Contains(original[1]), "Undo ran while adding.");
        Finish(adding); Require(!vm.HasUndoRemoval, "Adding retained undo after results changed.");
        vm.RemoveRows(vm.Rows.ToArray()); Require(!vm.HasRows && !vm.HasUnexportedResults, "Remove all left phantom unsaved results.");
        vm.UndoRemoval(); Require(vm.HasRows, "Remove all cannot be undone.");
        vm.RemoveRows([vm.Rows[0]]); vm.Clear(); vm.UndoRemoval(); Require(!vm.HasRows && !vm.HasUndoRemoval, "Clear resurrected removed results.");

        var pending = new TaskCompletionSource<PersonLookupResult>();
        using var busy = new PeopleViewModel(new Fake((_, _) => pending.Task));
        using var busyView = View(busy, root, "busy-remove");
        busy.Add("waiting\nqueued"); var lookup = busy.LookupAsync();
        var waiting = busy.Rows[0];
        var busyGrid = (DataGrid)busyView.FindName("ResultsGrid"); Layout(busyView); busyGrid.SelectedItem = waiting; Call(busyView, "BuildRowMenu");
        Require(!busyGrid.ContextMenu.Items.OfType<MenuItem>().Last().IsEnabled && busy.RemoveRows([waiting]) == 0, "Busy removal was enabled.");
        busy.Stop(); Require(busy.RemoveRows([waiting]) == 0, "Stop enabled removal before request drained.");
        pending.SetResult(Exact()); Finish(lookup);
        Require(busy.Rows.All(r => r.Status == "Stopped") && busy.RemoveRows([waiting]) == 1, "Late result leaked or removal stayed blocked.");
    }
    private static void VerifyPendingSubmissions(string root)
    {
        int queries = 0, prompts = 0;
        using var vm = new PeopleViewModel(new Fake((_, _) => { queries++; return Task.FromResult(Exact()); }));
        using var view = View(vm, root, "submitting");
        vm.Add("retained\nremove"); Finish(vm.LookupAsync());
        vm.RemoveRows([vm.Rows[1]]); vm.MarkExported();
        var retained = vm.Rows[0];
        Set(view, "ExitDialogOverride", (Func<PeopleExitWindow, bool?>)(dialog => { prompts++; return false; }));
        void Guarded(string gap)
        {
            Require(vm.IsSubmitting && !vm.IsAdding && !vm.IsBusy && vm.CanStop && !vm.CanAdd && !vm.CanExportComplete && !vm.CanUndoRemoval,
                gap + " submission appeared idle.");
            int count = vm.Rows.Count;
            Require(!view.ConfirmCanClose() && vm.RemoveRows([retained]) == 0, gap + " allowed silent close/removal.");
            vm.Clear(); vm.UndoRemoval(); vm.QueueRetry();
            Require(vm.Rows.Count == count && retained.Status == "Resolved", gap + " allowed conflicting mutation.");
            try { vm.Edit(retained, "changed"); throw new Exception(gap + " allowed editing."); } catch (InvalidOperationException) { }
        }
        var submission = (Task<bool>)Call(view, "AddEntriesAsync", "incoming")!;
        Guarded("Before adding");
        // FIFO at Background places this between the two real Dispatcher.Yield continuations.
        bool checkedSecondGap = false;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Require(vm.Rows.Count == 2, "Second-gap probe did not run after adding.");
            Guarded("Before automatic lookup"); checkedSecondGap = true; vm.Stop();
        }));
        Finish(submission);
        Require(checkedSecondGap && prompts == 2 && queries == 2 && !vm.CanStop && vm.Rows[1].Status == "Stopped",
            "Submission guard/Stop failed to drain or started a stopped lookup.");

        vm.RemoveRows([vm.Rows[1]]); vm.MarkExported();
        Require(vm.HasUndoRemoval && !vm.HasUnexportedResults, "First-gap Stop setup failed.");
        var input = (TextBox)view.FindName("QuickAddBox"); input.Text = "draft";
        var quick = (Task)Call(view, "QuickAddAsync")!;
        Require(vm.IsSubmitting && input.Text.Length == 0, "Quick add did not enter pending state.");
        vm.Stop(); Finish(quick);
        Require(input.Text == "draft" && vm.Rows.Count == 1 && !vm.CanStop && vm.CanUndoRemoval && !vm.HasUnexportedResults,
            "Stop before adding lost the draft/undo, dirtied unchanged exported results, or stayed busy.");

        var pending = new TaskCompletionSource<PersonLookupResult>();
        using var overlap = new PeopleViewModel(new Fake((_, _) => pending.Task));
        using var overlapView = View(overlap, root, "overlap");
        var older = (Task<bool>)Call(overlapView, "AddEntriesAsync", "older")!;
        Drain(); Require(overlap.IsBusy && !overlap.IsSubmitting && overlap.CanAdd, "Lookup incorrectly blocks adding another entry.");
        // Defer only the new first-yield continuation so the older lookup can finish first.
        // WPF restores Background priority on yielded continuations; assuming Normal here
        // would skip the very older completion that this regression must exercise.
        var dispatcher = Dispatcher.CurrentDispatcher;
        int deferred = 0;
        DispatcherHookEventHandler deferSubmission = (_, args) =>
        {
            if (args.Operation.Priority == DispatcherPriority.Background)
            { args.Operation.Priority = DispatcherPriority.ContextIdle; deferred++; }
        };
        Task<bool> newer;
        dispatcher.Hooks.OperationPosted += deferSubmission;
        try { newer = (Task<bool>)Call(overlapView, "AddEntriesAsync", "newer")!; }
        finally { dispatcher.Hooks.OperationPosted -= deferSubmission; }
        Require(deferred == 1 && overlap.IsSubmitting, "The overlap probe did not isolate the new submission's first yield.");
        bool guardHeldAtOlderCompletion = false;
        var observed = older.ContinueWith(_ =>
            guardHeldAtOlderCompletion = overlap.IsSubmitting && !overlap.CanExportComplete && !overlap.CanAdd && overlap.Rows.Count == 1,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        pending.SetResult(Exact());
        Finish(older); Finish(observed);
        Require(guardHeldAtOlderCompletion, "Older completion released the newer submission guard.");
        Finish(newer);
        Require(!overlap.CanStop && overlap.Rows.Count == 2 && overlap.Rows.All(r => r.Status == "Resolved"), "Overlapping submission lost a result or stayed busy.");
    }
    private static void VerifyExitAndExport(string root)
    {
        using var vm = new PeopleViewModel(new Fake((_, _) => Task.FromResult(Exact())));
        using var view = View(vm, root, "exit");
        vm.Add("alice\nmissing"); vm.LookupAsync().GetAwaiter().GetResult();
        vm.Rows[1].Set("Not found", "Synthetic missing account", candidates: []); vm.Refresh();
        string? copied = null;
        Set(view, "ClipboardWriterOverride", (Action<string>)(text => copied = text));
        Call(view, "OnCopyResolved", view, new RoutedEventArgs());
        Require(copied == Alice.Email && vm.HasUnexportedResults && vm.Message.Contains("Export CSV"), "Copy pretended to preserve full results.");
        vm.SearchText = "alice";
        bool Confirm(Action<PeopleExitWindow> action)
        {
            Set(view, "ExitDialogOverride", (Func<PeopleExitWindow, bool?>)(dialog => NavigationClarityProbe.RunDialog(dialog, () => action(dialog))));
            return view.ConfirmCanClose();
        }
        foreach (string mode in new[] { "cancel", "escape", "enter", "close" })
        {
            bool approved = Confirm(dialog =>
            {
                Require(((Button)dialog.FindName("CancelButton")).IsDefault && ((Button)dialog.FindName("CancelButton")).IsCancel, "Cancel is not default/safe.");
                if (mode == "cancel") Click(dialog, "CancelButton");
                else if (mode == "close") dialog.Close();
                else AccessKeyManager.ProcessKey(PresentationSource.FromVisual(dialog), mode == "escape" ? "\x1b" : "\r", false);
                Require(!dialog.IsVisible, mode + " failed to dismiss dialog.");
            });
            Require(!approved && vm.Rows.Count == 2 && vm.HasUnexportedResults, mode + " lost results or approved exit.");
        }
        Set(view, "ExportPathSelector", (Func<Window?, string?>)(_ => null));
        Require(!Confirm(dialog =>
        {
            Click(dialog, "ExportButton");
            Require(dialog.IsVisible && ((TextBlock)dialog.FindName("ExportStatus")).Text.Contains("canceled"), "Save cancellation dismissed exit dialog.");
            Click(dialog, "CancelButton");
        }), "Canceled export approved exit.");
        Set(view, "ExportPathSelector", (Func<Window?, string?>)(_ => root)); // directory is not a writable file
        Require(!Confirm(dialog =>
        {
            Click(dialog, "ExportButton");
            Require(dialog.IsVisible && ((TextBlock)dialog.FindName("ExportStatus")).Text.Contains("Could not export"), "Write failure dismissed exit dialog.");
            Click(dialog, "CancelButton");
        }) && vm.HasUnexportedResults, "Failed export discarded state.");
        string path = Path.Combine(root, "people.csv");
        Set(view, "ExportPathSelector", (Func<Window?, string?>)(_ => path));
        Require(Confirm(dialog => Click(dialog, "ExportButton")), "Successful export didn't approve close.");
        string csv = File.ReadAllText(path);
        Require(csv.Contains("missing") && csv.Contains("Not found") && csv.Contains(Alice.Email) && !vm.HasUnexportedResults && vm.Rows.Count == 2,
            "Export lost hidden/unresolved entries or cleared in-memory results.");
        Set(view, "ExitDialogOverride", (Func<PeopleExitWindow, bool?>)(_ => throw new Exception("Clean results prompted again.")));
        Require(view.ConfirmCanClose(), "Exported idle results cannot close quietly.");

        var pending = new TaskCompletionSource<PersonLookupResult>();
        using var busy = new PeopleViewModel(new Fake((_, _) => pending.Task));
        using var busyView = View(busy, root, "busy-exit");
        busy.Add("pending"); var work = busy.LookupAsync();
        Set(busyView, "ExportPathSelector", (Func<Window?, string?>)(_ => Path.Combine(root, "snapshot.csv")));
        Call(busyView, "OnExport", busyView, new RoutedEventArgs());
        Require(busy.HasUnexportedResults && busy.Message.Contains("still running"), "Active export was marked complete.");
        Set(busyView, "ExitDialogOverride", (Func<PeopleExitWindow, bool?>)(dialog => NavigationClarityProbe.RunDialog(dialog, () =>
        {
            Require(!((Button)dialog.FindName("ExportButton")).IsEnabled && ((TextBlock)dialog.FindName("BusyNotice")).Visibility == Visibility.Visible,
                "Busy exit promises a complete export.");
            Click(dialog, "DiscardButton");
        })));
        Require(busyView.ConfirmCanClose() && busy.IsBusy && busy.StopVersion == 0 && busy.Rows.Count == 1, "Provisional discard canceled work or erased results.");
        busy.Stop(); pending.SetResult(Exact()); Finish(work);

        // A save dialog can pump a queued lookup before it returns a path.
        var late = new TaskCompletionSource<PersonLookupResult>();
        using var reentrant = new PeopleViewModel(new Fake((_, _) => late.Task));
        using var reentrantView = View(reentrant, root, "reentrant");
        reentrant.Add("pending"); Task? started = null;
        string rejectedPath = Path.Combine(root, "must-not-export.csv");
        Set(reentrantView, "ExportPathSelector", (Func<Window?, string?>)(_ => { started = reentrant.LookupAsync(); return rejectedPath; }));
        Require(!(bool)Call(reentrantView, "ExportCsv", null, true)! && !File.Exists(rejectedPath) && reentrant.HasUnexportedResults,
            "Export-and-exit raced lookup during save selection.");
        reentrant.Stop(); late.SetResult(Exact()); Finish(started!);
    }
    private static void VerifyDialogLayout(string renderRoot)
    {
        Directory.CreateDirectory(renderRoot);
        using var vm = new PeopleViewModel(new Fake((_, _) => Task.FromResult(Exact())));
        vm.Add("dummy"); vm.IsSubmitting = true;
        foreach (bool dark in new[] { true, false })
        foreach (var size in new[] { (Width: 540, Height: 350), (Width: 420, Height: 300) })
        {
            var dialog = new PeopleExitWindow(vm, _ => throw new Exception("Layout check invoked export.")) { Width = size.Width, Height = size.Height };
            PeopleFlowProbe.ApplyTheme(dialog, dark);
            // Rasterizing the content alone omits the window's native backdrop.
            ((Panel)dialog.Content).SetResourceReference(Panel.BackgroundProperty, "ApplicationBackgroundBrush");
            NavigationClarityProbe.RunDialog(dialog, () =>
            {
                ((TextBlock)dialog.FindName("ExportStatus")).Text = "Could not export: synthetic file error. People results are kept.";
                dialog.UpdateLayout();
                var content = (FrameworkElement)dialog.Content;
                var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
                foreach (string name in new[] { "ExportButton", "DiscardButton", "CancelButton" })
                {
                    var button = (Button)dialog.FindName(name);
                    var box = button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));
                    Require(bounds.Contains(box) && button.ActualWidth >= button.DesiredSize.Width - button.Margin.Left - button.Margin.Right - 1,
                        "Exit choice is clipped at " + size.Width + ": " + name);
                }
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(renderRoot, "exit-" + (dark ? "dark-" : "light-") + size.Width + ".png")); encoder.Save(output);
            });
        }
        vm.IsSubmitting = false;
    }
    private static void VerifyCloseVeto(string root)
    {
        var window = new MainWindow();
        new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        var people = (PeopleView)window.FindName("PeopleSection");
        var scripts = (ScriptsPage)window.FindName("ScriptsSection");
        var library = new ScriptLibrary(Path.Combine(root, "scripts"), null);
        try
        {
            people.Model.Add("DUMMY-LOCAL-ONLY");
            int prompts = 0;
            Set(people, "ExitDialogOverride", (Func<PeopleExitWindow, bool?>)(dialog =>
            { prompts++; return NavigationClarityProbe.RunDialog(dialog, () => Click(dialog, "DiscardButton")); }));
            scripts.Initialize(library); ((TextEditor)scripts.FindName("Editor")).Text = "# unsaved synthetic script";
            scripts.UnsavedChangesPromptOverride = () => ScriptEditChoice.Cancel;
            for (int i = 0; i < 2; i++)
            {
                var closing = new CancelEventArgs(); Call(window, "OnClosing", closing);
                Require(closing.Cancel && people.Model.Rows.Count == 1 && people.Model.HasUnexportedResults, "Other close veto lost People results.");
            }
            Require(prompts == 2, "Discard approval survived a vetoed close.");
        }
        finally { people.Model.Clear(); scripts.UnsavedChangesPromptOverride = () => ScriptEditChoice.Discard; window.Close(); }
    }
    private static void Layout(FrameworkElement view)
    { view.Measure(new Size(1280, 680)); view.Arrange(new Rect(0, 0, 1280, 680)); view.UpdateLayout(); Drain(); }
    private static void Click(Window window, string name) => typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window.FindName(name), null);
    private static void Set(object target, string name, object value) => target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Finish(Task task)
    {
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(5)) { Drain(); Thread.Sleep(1); }
        Require(task.IsCompleted, "Synthetic async operation timed out."); task.GetAwaiter().GetResult();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Fake(Func<string, CancellationToken, Task<PersonLookupResult>> lookup) : IPeopleLookup
    { public Task<PersonLookupResult> LookupAsync(string input, CancellationToken cancellationToken = default) => lookup(input, cancellationToken); }
}
