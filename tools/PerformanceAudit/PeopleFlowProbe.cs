using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vivre.Core.People;
using Vivre.Desktop;
using Vivre.Desktop.ViewModels;

namespace Vivre.PerformanceAudit;

internal static class PeopleFlowProbe
{
    public static void Run(string root)
    {
        var alice = new DirectoryPerson("alice-id", "Alice Example", "alice", "alice@example.test", "alice@example.test", "IT", "Engineer", "Test", true);
        var pending = new TaskCompletionSource<PersonLookupResult>();
        var disabled = alice with { Id = "disabled-id", Enabled = false, Email = "", Name = "Alice Example (disabled account)" };
        var candidates = new List<PersonCandidate>
        {
            new(alice with { Email = "alice.long.email.address.for.review@example.test" }, "Similar name", 0),
            new(disabled, "Similar name", 1)
        };
        for (int i = 0; i < 20; i++) candidates.Add(new(alice with { Id = "dummy-" + i, Name = "Alice Example " + i }, "Similar name", i + 2));
        using var vm = new PeopleViewModel(new Fake((input, _) => input switch
        {
            "alice" => Task.FromResult(new PersonLookupResult([new(alice, "Exact", 0)], true)),
            "waiting" => pending.Task,
            _ => Task.FromResult(new PersonLookupResult(candidates, false))
        }));
        string settingsPath = Path.Combine(WorkspaceProbe.SettingsRoot!, "people-flow.json");
        AppSettingsStore Store() => (AppSettingsStore)Activator.CreateInstance(typeof(AppSettingsStore), BindingFlags.Instance | BindingFlags.NonPublic, null, [settingsPath, null], null)!;
        var store = Store();
        using var view = new PeopleView(vm, store);
        T Control<T>(string name) => (T)view.FindName(name);
        void Layout(int width = 1280, int height = 680)
        {
            view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
            // Theme/virtualized column updates can be deferred even in an offscreen tree.
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            view.UpdateLayout();
        }
        void Render(string name, int width, int height)
        {
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(root, name + ".png")); encoder.Save(output);
        }
        void Click(string name) => Control<System.Windows.Controls.Primitives.ButtonBase>(name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        ApplyTheme(view, true); Layout();
        Require(vm.ShowEmptyPaste && vm.EmptyTitle.StartsWith("Add or paste") && !Control<Button>("ReviewQueueButton").IsEnabled && !Control<Button>("CopyResolvedButton").IsEnabled, "Initial People actions/states are misleading.");
        Require(Control<StackPanel>("EmptyState").Visibility == Visibility.Visible && Control<Button>("EmptyPasteButton").Visibility == Visibility.Visible && Control<Button>("EmptyPasteButton").IsHitTestVisible, "Empty-state Paste is inaccessible.");
        Render("flow-empty", 1280, 680);
        vm.Add("alice\nambiguous\nanother similar name"); vm.LookupAsync().GetAwaiter().GetResult(); Layout();
        Require(vm.PendingReviewCount == 2 && vm.VisibleCount == 3 && vm.CanCopyResolved, "Initial queue/count/copy state is wrong.");
        vm.Filter = "Resolved"; Layout();
        Require(vm.Summary.StartsWith("Showing 1 of 3") && Control<Button>("ReviewQueueButton").IsEnabled, "Filtered count changed the global review queue.");
        Control<DataGrid>("ResultsGrid").SelectedItem = vm.Rows[0];
        Click("ReviewQueueButton"); Layout();
        Require(vm.ReviewRow == vm.Rows[1] && vm.SelectedCandidate is null && vm.ReviewTitle.Contains("2 need review"), "Queue button opened selected resolved row or preselected a guess.");
        var splitter = Control<GridSplitter>("ReviewSplitter");
        double before = Control<Border>("ReviewPanel").ActualHeight;
        bool moved = (bool)typeof(GridSplitter).GetMethod("KeyboardMoveSplitter", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(splitter, [0d, -55d])!;
        Layout();
        Require(moved && Control<Border>("ReviewPanel").ActualHeight > before + 20, "Review panel cannot resize.");
        var resized = Control<RowDefinition>("ReviewRowDefinition").Height;
        vm.SelectedCandidate = candidates[1]; Layout();
        Require(Control<TextBlock>("CandidateNotice").Text.Contains("disabled") && Control<TextBlock>("CandidateNotice").Text.Contains("No email"), "Disabled/missing-mail warning absent.");
        vm.Accept(); Layout();
        Require(vm.ReviewRow == vm.Rows[2] && vm.PendingReviewCount == 1 && vm.SelectedCandidate is null && Control<RowDefinition>("ReviewRowDefinition").Height == resized, "Queue advance lost count, deliberate selection, or resized height.");
        vm.Skip(); Layout();
        Require(!vm.HasReview && !Control<Button>("ReviewQueueButton").IsEnabled && Control<RowDefinition>("ReviewRowDefinition").ActualHeight == 0, "Zero queue remains enabled or consumes review space.");
        vm.ClearFilters(); Control<DataGrid>("ResultsGrid").SelectedItem = vm.Rows[2]; Layout();
        Require(Control<Button>("OpenReviewButton").IsEnabled, "Skipped-row review was disabled with an empty queue.");
        Click("OpenReviewButton"); Layout();
        Require(vm.ReviewRow == vm.Rows[2] && vm.ReviewTitle.Contains("0 need review") && Control<RowDefinition>("ReviewRowDefinition").Height == resized, "Selected review cannot reopen or preserve height.");

        foreach (bool dark in new[] { true, false })
        foreach (int width in new[] { 800, 1280 })
        foreach (int height in new[] { 400, 680 })
        {
            ApplyTheme(view, dark);
            vm.SelectedCandidate = candidates[1]; Layout(width, height);
            var background = ((SolidColorBrush)view.Background).Color;
            Require(dark ? background.R < 100 : background.R > 150, "The requested theme did not reach the rendered People view.");
            var grid = Control<DataGrid>("CandidatesGrid");
            Require(grid.ActualHeight > 45 && grid.FrozenColumnCount == 1, "Review lost usable viewport or pinned identity.");
            Require(Control<Button>("CopyResolvedButton").TransformToAncestor(view).Transform(new Point()).X + Control<Button>("CopyResolvedButton").ActualWidth <= width, "Copy action is clipped at narrow width.");
            Render($"flow-review-{width}-{height}-{(dark ? "dark" : "light")}", width, height);
        }
        vm.CloseReview(); Control<DataGrid>("ResultsGrid").SelectedItem = null;
        vm.SearchText = "no matching dummy person"; Layout();
        Require(vm.ShowEmptyState && vm.EmptyTitle.StartsWith("No people match") && vm.Summary.StartsWith("Showing 0 of 3") && !vm.CanOpenReview && vm.CanCopyResolved, "No-match state changed global copy or selected review eligibility.");
        Require(Control<StackPanel>("EmptyState").Visibility == Visibility.Visible && Control<Button>("EmptyClearFiltersButton").Visibility == Visibility.Visible && Control<Button>("EmptyClearFiltersButton").IsHitTestVisible, "No-match recovery is inaccessible.");
        Render("flow-no-matches", 1280, 680);
        vm.MarkExported(); Click("EmptyClearFiltersButton"); Layout();
        Require(vm.VisibleCount == 3 && !vm.HasFilters && !vm.HasUnexportedResults, "Clear filters changed results/export state.");
        vm.Add("waiting"); vm.Filter = "Resolved"; vm.SearchText = "none";
        var work = vm.LookupAsync(); Layout();
        Require(vm.EmptyTitle.StartsWith("Looking up") && vm.CanStop && !vm.ShowEmptyPaste, "Loading looks like an idle empty list.");
        Require(Control<StackPanel>("EmptyState").Visibility == Visibility.Visible
            && Control<StackPanel>("EmptyState").Children.OfType<TextBlock>().Any(t => t.Text == vm.EmptyTitle),
            "Loading guidance did not reach the visible empty-state controls.");
        pending.SetException(new IOException("Synthetic directory unavailable")); work.GetAwaiter().GetResult(); Layout();
        Require(vm.Message.Contains("Synthetic directory unavailable") && vm.Rows[^1].Status == "Lookup failed", "Directory failure was lost behind empty filtering.");
        vm.ClearFilters(); vm.StartReview(vm.Rows[2]); Layout();
        var reviewGrid = Control<DataGrid>("CandidatesGrid");
        var layout = (GridLayoutPersistence)typeof(PeopleView).GetField("_candidatesLayout", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        reviewGrid.Columns[2].Width = 275; reviewGrid.Columns[2].DisplayIndex = 1; layout.SaveUserLayout(); store.Flush(TimeSpan.FromSeconds(10));
        using var restored = new PeopleView(new PeopleViewModel(new Fake((_, _) => throw new InvalidOperationException("No lookup expected"))), Store());
        var restoredLayout = (GridLayoutPersistence)typeof(PeopleView).GetField("_candidatesLayout", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(restored)!;
        restoredLayout.Restore();
        var restoredGrid = (DataGrid)restored.FindName("CandidatesGrid");
        Require(restoredGrid.Columns[2].Width.Value == 275 && restoredGrid.Columns[2].DisplayIndex == 1 && restoredGrid.Columns[0].DisplayIndex == 0, "Review changes lost persisted column identity/order/width.");
        Console.WriteLine("People flow: queue/selected review, resize, warnings, filtered/loading/error states, saved layout and 8 review layouts passed.");
    }
    internal static void ApplyTheme(FrameworkElement view, bool dark)
    {
        // Offscreen roots are not registered application windows, so global theme invalidation does
        // not reach their cached DynamicResources. Scope the actual library dictionary to this tree.
        view.Resources.MergedDictionaries.Clear();
        view.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary
        {
            Theme = dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light
        });
    }
    private sealed class Fake(Func<string, CancellationToken, Task<PersonLookupResult>> read) : IPeopleLookup
    {
        public Task<PersonLookupResult> LookupAsync(string input, CancellationToken token) => read(input, token);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
