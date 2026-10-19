using System.IO;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Vivre.Core.Credentials;
using Vivre.Core.Logging;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vivre.Core.People;
using Vivre.Desktop;
using Vivre.Desktop.ViewModels;

namespace Vivre.PerformanceAudit;

internal static class PeopleProbe
{
    private static readonly DirectoryPerson Alice = new("alice-id", "Alice Example", "alice", "alice@example.test", "alice@example.test", "IT", "Engineer", "Test", true);
    private static readonly DirectoryPerson Bob = Alice with { Id = "bob-id", Name = "Bob Example", Username = "bob", Email = "" };
    private static PersonLookupResult Exact(DirectoryPerson person) => new([new(person, "Exact match", 0)], true);
    public static object Run(string root)
    {
        int queries = 0;
        using var vm = new PeopleViewModel(new Fake((input, token) =>
        {
            queries++;
            return Task.FromResult(input switch {
                "alice" or "alias" => Exact(Alice),
                "bob" => Exact(Bob),
                "ambiguous" => new PersonLookupResult([new(Alice,"Name suggestion",0),new(Bob,"Similar name",1)],false),
                _ => new PersonLookupResult([],false) });
        }));
        Require(vm.Add("alice\nalias\nbob\nambiguous\nmissing\nALICE") == 5, "Paste dedupe failed.");
        vm.LookupAsync().GetAwaiter().GetResult();
        Require(queries == 5 && vm.Rows.Count(r => r.Status == "Resolved") == 2, "Exact lookup failed.");
        Require(vm.Rows[2].Status == "No email" && vm.Rows[2].Email == "", "Invented email.");
        Require(vm.Rows[3].Status == "Needs review" && vm.Rows[3].Person is null, "Suggestion auto-selected.");
        Require(vm.Rows[4].Status == "Not found", "Missing state wrong.");
        Require(PeopleViewModel.Emails(vm.Rows).SequenceEqual(new[] { Alice.Email }), "Email copy includes guesses/duplicates.");
        vm.StartReview(); Require(vm.SelectedCandidate is null, "Candidate preselected.");
        vm.SelectedCandidate = vm.ReviewRow!.Candidates[1]; vm.Accept();
        Require(vm.Rows[3].Status == "No email", "Review choice lost.");
        vm.StartReview(vm.Rows[3]); vm.SelectedCandidate = vm.ReviewRow!.Candidates[0]; vm.Accept();
        Require(vm.Rows[3].Email == Alice.Email, "Cannot revise match.");
        vm.StartReview(vm.Rows[3]); vm.Skip();
        Require(vm.Rows[3].Status == "Skipped" && vm.Rows[3].Email == "", "Skipped guess leaked.");
        Require(vm.Add("alice") == 0 && vm.Rows[0].Status == "Resolved", "Paste reset old results.");
        vm.Filter = "Skipped"; Require(vm.Results.Cast<object>().Count() == 1, "Filter failed.");
        vm.MarkExported(); Require(!vm.HasUnexportedResults, "Export state failed.");
        vm.Edit(vm.Rows[4], "=formula"); Require(vm.HasUnexportedResults && PeopleView.Csv(vm.Rows).Contains("\t=formula"), "Export formula guard missing.");
        vm.Filter = "All"; vm.StartReview(vm.Rows[3]);
        vm.StartReview(vm.Rows[4]);
        Require(vm.ReviewRow is null, "No-candidate row left unrelated review actionable.");
        vm.StartReview(vm.Rows[3]);

        var pending = new TaskCompletionSource<PersonLookupResult>();
        using var stopped = new PeopleViewModel(new Fake((_, _) => pending.Task));
        stopped.Add("DUMMY-STOP\nDUMMY-QUEUED");
        var work = stopped.LookupAsync(); stopped.Stop(); pending.SetResult(Exact(Alice)); work.GetAwaiter().GetResult();
        Require(stopped.Rows.All(r => r.Status == "Stopped") && stopped.Rows.All(r => r.Person is null), "Late result applied after Stop.");
        int failedCalls = 0;
        using var failed = new PeopleViewModel(new Fake((_, _) => { failedCalls++; throw new IOException("Synthetic AD unavailable"); }));
        failed.Add("DUMMY-FAIL\nDUMMY-NEXT"); failed.LookupAsync().GetAwaiter().GetResult();
        Require(failedCalls == 1 && failed.Rows[0].Status == "Lookup failed" && failed.Rows[1].Status == "Stopped", "Directory outage queried every row or became Not found.");

        var delayed = new TaskCompletionSource<PersonLookupResult>();
        using var additions = new PeopleViewModel(new Fake((name, _) => name == "first" ? delayed.Task : Task.FromResult(Exact(Bob))));
        additions.Add("first"); var adding = additions.LookupAsync();
        additions.Add("second"); delayed.SetResult(Exact(Alice)); adding.GetAwaiter().GetResult();
        Require(additions.Rows[0].Status == "Resolved" && additions.Rows[1].Status == "No email", "Add during lookup lost work.");
        using var invalid = new PeopleViewModel(new Fake((name, _) => name == "invalid" ? throw new ArgumentException("Refine input") : Task.FromResult(Exact(Alice))));
        invalid.Add("invalid\nvalid"); invalid.LookupAsync().GetAwaiter().GetResult();
        Require(invalid.Rows[0].Status == "Lookup failed" && invalid.Rows[1].Status == "Resolved", "Input error blocked unrelated entries.");

        // Real XAML binding/layout, no visible windows or production startup.
        var view = new PeopleView(vm);
        foreach (bool dark in new[] { true, false })
        foreach (int height in new[] { 400, 680 })
        foreach (int width in new[] { 800, 1280 })
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light);
            view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
            var scroll = (ScrollViewer)view.Content;
            if (height == 680) Require(scroll.ScrollableHeight <= 1, "Unnecessary page overflow at normal height.");
            if (height == 400) Require(scroll.ScrollableHeight > 0, "Short window cannot reach review controls.");
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(root, $"people-{width}-{height}-{(dark ? "dark" : "light")}.png")); encoder.Save(file);
        }
        var created = new List<WorkspaceViewModel>();
        WorkspaceViewModel Factory() { var workspace = WorkspaceProbe.CreateWorkspace(); created.Add(workspace); return workspace; }
        var shell = new ShellViewModel(Factory, null!, new CredentialStore(), new FakeLog());
        var window = new MainWindow { DataContext = shell };
        try
        {
            var method = typeof(MainWindow).GetMethod("ShowNavSection", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var type = method.GetParameters()[0].ParameterType;
            var people = (PeopleView)window.FindName("PeopleSection");
            people.Model.Add("DUMMY-KEEP");
            foreach (string section in new[] { "People", "Health", "Patching", "People" })
                method.Invoke(window, new[] { Enum.Parse(type, section) });
            Require(((PeopleView)window.FindName("PeopleSection")).Visibility == Visibility.Visible
                && ((Grid)window.FindName("ComputersSection")).Visibility == Visibility.Collapsed, "Navigation isolation failed.");
            Require(people.Model.Rows.Single().Input == "DUMMY-KEEP", "Navigation lost People input.");
            int healthCount = shell.HealthTabs.Count, patchCount = shell.PatchingTabs.Count;
            var originalTab = shell.SelectedTab;
            foreach (string handler in new[] { "OnNewTabKey", "OnCloseTabKey", "OnRenameTabKey", "OnRefreshKey", "OnInstallKey", "OnToggleModeKey" })
                typeof(MainWindow).GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object?[] { window, null });
            Require(shell.HealthTabs.Count == healthCount && shell.PatchingTabs.Count == patchCount && ReferenceEquals(shell.SelectedTab, originalTab)
                && people.Visibility == Visibility.Visible, "People accelerator affected hidden fleet workspace.");
            people.Model.Clear();
        }
        finally { window.DataContext = null; ((PeopleView)window.FindName("PeopleSection")).Model.Clear(); window.Close(); foreach (var workspace in created) workspace.Dispose(); }
        return new { pass = true, exactAndReview = true, missingEmail = true, duplicateReuse = true, stopAndFailure = true,
            copyAndCsv = true, layoutWidths = new[] { 800, 1280 }, navigation = true, liveDirectory = false };
    }
    private sealed class FakeLog : IActivityLog
    {
        public ObservableCollection<LogEntry> Entries { get; } = [];
        public void Info(string? machine, string message) { }
        public void Warn(string? machine, string message) { }
        public void Error(string? machine, string message) { }
        public void Clear() { }
    }
    private sealed class Fake(Func<string,CancellationToken,Task<PersonLookupResult>> read) : IPeopleLookup
    {
        public Task<PersonLookupResult> LookupAsync(string input, CancellationToken token) => read(input, token);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
