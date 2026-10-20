using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vivre.Core.Computers;
using Vivre.Core.Models;
using Vivre.Desktop;
using Vivre.Desktop.ViewModels;

namespace Vivre.PerformanceAudit;

internal static class ListOrganizationProbe
{
    public static object Run(string root, string renders)
    {
        using var vm = WorkspaceProbe.CreateWorkspace();
        var store = new ComputerListStore(Path.Combine(root, "organization-lists"));
        var fault = new FaultStore(store);
        typeof(WorkspaceViewModel).GetField("_lists", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, fault);
        var row = new Computer("DUMMY-A") { CommandResult = "Preserved result" };
        vm.Computers.Add(row); vm.SelectedComputers.Add(row);
        var folders = new ComputerListFolders(store);
        folders.Create("October Updates"); folders.Create("November");
        var shell = new MainWindow(); // Never shown: no production composition/startup.
        var menu = ((FrameworkElement)shell.FindName("ListsButton")).ContextMenu;
        var organizeItem = menu.Items.OfType<MenuItem>().Single(item => item.Tag is "OrganizeLists");
        Require(organizeItem.Items.Count == 0 && organizeItem.Header.ToString() == "Organize lists…", "Organizer still uses cascading menus.");
        try
        {
            TextPromptWindow Save(Func<Window, string, bool>? approve = null, Action<Window, string>? warning = null)
                => (TextPromptWindow)typeof(MainWindow).GetMethod("CreateSaveListDialog", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(shell, [vm, approve ?? ((_, _) => true), warning ?? ((_, message) => throw new Exception(message))])!;
            var save = Save();
            NavigationClarityProbe.RunDialog(save, () =>
            {
                var choice = Choice(save);
                Require(choice.HasSelection && choice.SelectedFolder is null, "New list does not default to top level.");
                Input(save).Text = "Tuesday, 10/20/2026 | 09:00 AM";
                Click(save, "AcceptButton");
            });
            string first = vm.SavedListName!;
            Require(store.Exists(first) && folders.Snapshot(store.List()).FolderFor(first) is null && vm.Title == first, "Save failed to preserve punctuation/rename/default folder.");
            var inFolder = Save();
            NavigationClarityProbe.RunDialog(inFolder, () => { Choice(inFolder).SelectFolder("October Updates"); Click(inFolder, "AcceptButton"); });
            Require(folders.Snapshot(store.List()).FolderFor(first) == "October Updates", "Save-time folder assignment not persisted.");
            var resave = Save();
            NavigationClarityProbe.RunDialog(resave, () => Require(Choice(resave).SelectedFolder == "October Updates", "Resave silently resets an existing folder."));
            store.Save("Other existing list", ["DUMMY-B"]); folders.Move("Other existing list", "November");
            var exact = Save();
            NavigationClarityProbe.RunDialog(exact, () =>
            {
                Input(exact).Text = "Other existing list";
                Require(Choice(exact).SelectedFolder == "November" && !Choice(exact).HasExplicitSelection, "Name editing did not show the exact target's folder.");
                folders.Move("Other existing list", "October Updates");
                Click(exact, "AcceptButton");
            });
            Require(folders.Snapshot(store.List()).FolderFor("Other existing list") == "October Updates", "Save overwrote a concurrent folder move.");
            var overrideFolder = Save();
            NavigationClarityProbe.RunDialog(overrideFolder, () =>
            {
                Choice(overrideFolder).SelectFolder("November"); Input(overrideFolder).Text = first;
                Require(Choice(overrideFolder).HasExplicitSelection, "Explicit folder choice lost while naming.");
                Click(overrideFolder, "AcceptButton");
            });
            Require(folders.Snapshot(store.List()).FolderFor(first) == "November", "Explicit folder choice ignored.");
            folders.Move(first, "October Updates");
            var reject = Save((_, _) => false);
            string before = store.GetRevision(first)!;
            NavigationClarityProbe.RunDialog(reject, () =>
            {
                Choice(reject).SelectFolder("November"); Click(reject, "AcceptButton");
                Require(Error(reject).Contains("Not overwritten"), "Declined overwrite not shown.");
            });
            Require(store.GetRevision(first) == before && folders.Snapshot(store.List()).FolderFor(first) == "October Updates", "Declined overwrite mutated list/folder.");
            var stale = Save();
            NavigationClarityProbe.RunDialog(stale, () =>
            {
                Input(stale).Text = "Must not save"; Choice(stale).SelectFolder("November"); folders.Remove("November");
                Click(stale, "AcceptButton"); Require(Error(stale).Contains("no longer exists"), "Stale destination not explained.");
            });
            Require(!store.Exists("Must not save"), "Stale folder allowed an unintended save.");
            var failed = Save(); fault.FailSave = true;
            NavigationClarityProbe.RunDialog(failed, () =>
            { Input(failed).Text = "Failed"; Choice(failed).SelectFolder(null); Click(failed, "AcceptButton"); Require(Error(failed).Contains("Synthetic"), "Save failure hidden."); });
            fault.FailSave = false;
            Require(vm.Title == first && !store.Exists("Failed") && folders.Snapshot(store.List()).FolderFor(first) == "October Updates", "Failed save changed tab/folder.");

            var nested = Save();
            NavigationClarityProbe.RunDialog(nested, () =>
            {
                var choice = Choice(nested);
                CompleteNestedPrompt("Created during save", accept: true);
                Click(choice, "NewFolderButton");
                Require(choice.SelectedFolder == "Created during save", "New folder not selected in save dialog.");
                Input(nested).Text = "Saved in new folder"; Click(nested, "AcceptButton");
            });
            Require(folders.Snapshot(store.List()).FolderFor("Saved in new folder") == "Created during save", "Save into new folder failed.");
            var cancelNew = Save();
            NavigationClarityProbe.RunDialog(cancelNew, () =>
            {
                CompleteNestedPrompt("Canceled folder", accept: false); Click(Choice(cancelNew), "NewFolderButton");
                Input(cancelNew).Text = "Canceled save";
            });
            Require(!store.Exists("Canceled save") && !folders.Snapshot(store.List()).Folders.Contains("Canceled folder"), "Cancel wrote a list/folder.");

            string metadata = Path.Combine(store.Directory, "list-folders.json");
            string originalMetadata = File.ReadAllText(metadata);
            int warnings = 0;
            var partial = Save(warning: (_, message) => { Require(message.Contains("was saved") && message.Contains("folder could not"), "Partial save feedback misleading."); warnings++; });
            fault.AfterSave = () => File.WriteAllText(metadata, "{broken");
            NavigationClarityProbe.RunDialog(partial, () => { Input(partial).Text = "Contents saved"; Choice(partial).SelectFolder(null); Click(partial, "AcceptButton"); });
            fault.AfterSave = null;
            Require(warnings == 1 && store.Exists("Contents saved") && vm.Title == "Contents saved", "Partial save lost success state.");
            var unavailable = Save();
            NavigationClarityProbe.RunDialog(unavailable, () =>
            { Require(!Choice(unavailable).IsEnabled, "Broken metadata offered writable folders."); Input(unavailable).Text = "Unorganized fallback"; Click(unavailable, "AcceptButton"); });
            Require(store.Exists("Unorganized fallback") && File.ReadAllText(metadata) == "{broken", "Unavailable folders prevented save or overwrote metadata.");
            File.WriteAllText(metadata, originalMetadata);

            var fallbackChoice = new ListFolderChoice();
            int reads = 0;
            fallbackChoice.Configure(() => ++reads == 1 ? folders.Snapshot(store.List()) : throw new IOException("Synthetic refresh failure"), folders.Create);
            var fallbackPrompt = new TextPromptWindow("Synthetic folder failure", "Name") { AdditionalContent = fallbackChoice };
            NavigationClarityProbe.RunDialog(fallbackPrompt, () =>
            {
                CompleteNestedPrompt("Created despite refresh", accept: true); Click(fallbackChoice, "NewFolderButton");
                Require(folders.Snapshot(store.List()).Folders.Contains("Created despite refresh") && fallbackChoice.HasExplicitSelection,
                    "Created folder lost on refresh failure.");
                Require(((TextBlock)fallbackChoice.FindName("StatusText")).Text.Contains("Folder created"), "Successful creation/failed refresh is only a tooltip.");
                fallbackChoice.SelectFolder(null);
                Require(fallbackChoice.HasSelection, "Refresh failure removed Top level choice.");
            });
            VerifyOrganizer(vm, store, folders, first);
            Require(ReferenceEquals(vm.Computers.Single(), row) && row.CommandResult == "Preserved result" && vm.SelectedComputers.Contains(row), "Organizing/saving changed live rows/results/selection.");
            VerifyLayout(vm, Save, renders);
            return new { flatOrganizer = true, defaultTopLevel = true, saveAndCreateFolder = true,
                preservedExistingFolder = true, overwriteAndCancellation = true, staleAndFailedWrites = true,
                partialSaveTruth = true, corruptedMetadataPreserved = true, folderPersistenceAndListContents = true,
                rowResultsPreserved = true, renderedLayouts = 8,
                limits = "Dummy files and offscreen WPF controls/routed input; no live servers or physical input/DPI/screen-reader checks." };
        }
        finally { shell.Close(); }
    }

    private static void VerifyOrganizer(WorkspaceViewModel vm, ComputerListStore store, ComputerListFolders folders, string first)
    {
        string revision = store.GetRevision(first)!;
        var dialog = new OrganizeListsWindow(vm, (_, _) => true);
        NavigationClarityProbe.RunDialog(dialog, () =>
        {
            var list = (ListBox)dialog.FindName("Lists");
            var choice = (ListFolderChoice)dialog.FindName("FolderChoice");
            var search = (Wpf.Ui.Controls.TextBox)dialog.FindName("SearchBox");
            Select(list, first); choice.SelectFolder(null); Click(dialog, "MoveButton");
            Require(folders.Snapshot(store.List()).FolderFor(first) is null && store.GetRevision(first) == revision, "Move to root rewrote contents or failed.");
            choice.SelectFolder("October Updates"); Click(dialog, "MoveButton");
            Require(folders.Snapshot(store.List()).FolderFor(first) == "October Updates", "Move into folder failed.");
            search.Text = "never match anything";
            Require(!((Button)dialog.FindName("MoveButton")).IsEnabled && ((TextBlock)dialog.FindName("EmptyText")).Visibility == Visibility.Visible, "Filtered-out target is actionable.");
            search.Clear(); Select(list, first);
            CompleteNestedPrompt("Renamed October", accept: true); Click(dialog, "RenameButton");
            Require(folders.Snapshot(store.List()).FolderFor(first) == "Renamed October", "Rename lost assignment.");
            Select(list, first); Click(dialog, "RemoveButton");
            Require(folders.Snapshot(store.List()).FolderFor(first) is null && store.GetRevision(first) == revision, "Removing folder changed/deleted list.");
            CompleteNestedPrompt("Move destination", accept: true); Click(choice, "NewFolderButton");
            Select(list, first); choice.SelectFolder("Move destination"); folders.Remove("Move destination");
            Click(dialog, "MoveButton");
            Require(((TextBlock)dialog.FindName("StatusText")).Text.Contains("Could not move list"), "Stale move failure hidden.");
            Require(folders.Snapshot(store.List()).FolderFor(first) is null, "Stale move changed assignment.");
        });
        Require(new ComputerListFolders(new ComputerListStore(store.Directory)).Snapshot(store.List()).FolderFor(first) is null, "Organization not persisted across stores.");
    }

    private static void VerifyLayout(WorkspaceViewModel vm, Func<Func<Window, string, bool>?, Action<Window, string>?, TextPromptWindow> saveFactory, string renders)
    {
        Directory.CreateDirectory(renders);
        foreach (bool dark in new[] { true, false })
        foreach (int width in new[] { 440, 640 })
        foreach (bool organize in new[] { true, false })
        {
            Window dialog = organize ? new OrganizeListsWindow(vm) : saveFactory(null, null);
            dialog.Width = width;
            if (organize) dialog.Height = width == 440 ? 460 : 560;
            PeopleFlowProbe.ApplyTheme(dialog, dark);
            ((Panel)dialog.Content).SetResourceReference(Panel.BackgroundProperty, "ApplicationBackgroundBrush");
            NavigationClarityProbe.RunDialog(dialog, () =>
            {
                dialog.UpdateLayout();
                var content = (FrameworkElement)dialog.Content;
                var bounds = new Rect(content.RenderSize);
                var choice = organize ? (ListFolderChoice)dialog.FindName("FolderChoice") : Choice((TextPromptWindow)dialog);
                foreach (FrameworkElement control in new FrameworkElement[] { (FrameworkElement)choice.FindName("Folders"), (FrameworkElement)choice.FindName("NewFolderButton"), (FrameworkElement)dialog.FindName(organize ? "MoveButton" : "AcceptButton") })
                {
                    var box = control.TransformToAncestor(content).TransformBounds(new Rect(control.RenderSize));
                    Require(bounds.Contains(box) && control.ActualWidth >= control.MinWidth, "A list dialog control is clipped.");
                }
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(renders, $"{(organize ? "organize" : "save")}-{(dark ? "dark" : "light")}-{width}.png")); encoder.Save(output);
            });
        }
    }
    private static void CompleteNestedPrompt(string value, bool accept)
    {
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var prompt = Application.Current.Windows.OfType<TextPromptWindow>().Last();
            Input(prompt).Text = value;
            if (accept) Click(prompt, "AcceptButton"); else prompt.Close();
        }));
    }
    private static void Select(ListBox list, string name) => list.SelectedItem = list.Items.Cast<object>().Single(item => item.GetType().GetProperty("Name")!.GetValue(item)?.ToString() == name);
    private static ListFolderChoice Choice(TextPromptWindow dialog) => ((Panel)dialog.AdditionalContent!).Children.OfType<ListFolderChoice>().Single();
    private static Wpf.Ui.Controls.TextBox Input(TextPromptWindow dialog) => (Wpf.Ui.Controls.TextBox)dialog.FindName("InputBox");
    private static string Error(TextPromptWindow dialog) => ((TextBlock)dialog.FindName("ErrorText")).Text;
    private static void Click(FrameworkElement root, string name) => ((Button)root.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class FaultStore(ComputerListStore inner) : IComputerListStore
    {
        public bool FailSave { get; set; }
        public Action? AfterSave { get; set; }
        public string Directory => inner.Directory;
        public IReadOnlyList<string> List() => inner.List();
        public IReadOnlyList<string> Load(string name) => inner.Load(name);
        public IReadOnlyList<string> LoadRequired(string name) => inner.LoadRequired(name);
        public string? GetRevision(string name) => inner.GetRevision(name);
        public bool Exists(string name) => inner.Exists(name);
        public void Save(string name, IEnumerable<string> machines, bool overwrite = false, string? expectedRevision = null)
        { if (FailSave) throw new IOException("Synthetic save failure"); inner.Save(name, machines, overwrite, expectedRevision); AfterSave?.Invoke(); }
        public void Delete(string name, string? revision) => inner.Delete(name, revision);
        public IReadOnlyList<DeletedComputerList> DeletedLists() => inner.DeletedLists();
        public void Restore(string id, string name) => inner.Restore(id, name);
    }
}
