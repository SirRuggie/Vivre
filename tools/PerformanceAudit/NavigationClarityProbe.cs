using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Vivre.Core.Computers;
using Vivre.Core.Credentials;
using Vivre.Core.Logging;
using Vivre.Core.Models;
using Vivre.Desktop;
using Vivre.Desktop.ViewModels;

namespace Vivre.PerformanceAudit;

internal static class NavigationClarityProbe
{
    public static object Run(string root)
    {
        VerifyToolbar();
        VerifyEmptyViews();
        VerifyScrollbars();
        VerifyFolders(root);
        var scripts = ScriptEditorProbe.Run();
        return new { passed = true, toolbarScopesAndStaleGuards = true, emptySearchRecovery = true,
            verticalAndHorizontalPaging = true, thumbDragAndHitArea = true, folderPicker = true, scripts,
            scope = "Production WPF controls and temporary list store; routed events/offscreen modal dialog; no live servers, AD, physical input or production settings" };
    }

    private static void VerifyToolbar()
    {
        var created = new List<WorkspaceViewModel>();
        WorkspaceViewModel Factory() { var vm = WorkspaceProbe.CreateWorkspace(); created.Add(vm); return vm; }
        var shell = new ShellViewModel(Factory, null!, new CredentialStore(), new FakeLog());
        var window = new MainWindow { DataContext = shell };
        try
        {
            var cluster = (StackPanel)window.FindName("ActionCluster");
            foreach (var section in new[] { FleetSection.Health, FleetSection.Patching })
            {
                shell.ActiveFleetSection = section;
                var vm = shell.SelectedTab!;
                var a = new Computer("DUMMY-A") { IsOnline = true, CommandResult = "Keep" };
                var b = new Computer("DUMMY-B") { IsOnline = false, CommandResult = "Keep too" };
                vm.Computers.Add(a); vm.Computers.Add(b);
                foreach (bool selected in new[] { false, true })
                {
                    vm.SetSelection(selected ? [a] : []);
                    foreach (bool compact in new[] { false, true })
                    {
                        window.ToolbarCompact = compact;
                        Pump();
                        var buttons = cluster.Children.OfType<ButtonBase>().Where(b => b.Visibility == Visibility.Visible).ToArray();
                        string first = AutomationProperties.GetName(buttons[0]);
                        Require(first == (section == FleetSection.Health ? "Ping all" : selected ? "Scan selected machines" : "Scan all machines"),
                            "Primary toolbar action order changed: " + first);
                        Require(buttons.Any(b => b.Command == vm.StopCommand), "Stop vanished from toolbar.");
                        Require(!buttons.Any(b => b.Command == vm.ClearResultsCommand || b.Command == vm.RemoveOfflineCommand),
                            "Housekeeping is duplicated in primary toolbar.");
                        Require(buttons.Any(b => ReferenceEquals(b, window.FindName("SelectedActionsButton")) && b.IsEnabled), "Actions unavailable.");
                    }
                }
                ContextMenu Menu() => (ContextMenu)typeof(MainWindow).GetMethod("CreateToolbarActionsMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [vm])!;
                vm.SetSelection([]);
                var menu = Menu();
                Require(menu.Items.OfType<MenuItem>().Take(4).All(i => !i.IsEnabled), "Selected shortcuts enabled without selection.");
                vm.SetSelection([a]);
                menu = Menu();
                Item(menu, "Clear results").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Require(a.CommandResult is null && b.CommandResult is null, "Clear results scope narrowed to selection.");
                a.CommandResult = "Retain";
                var stale = Menu();
                shell.ActiveFleetSection = section == FleetSection.Health ? FleetSection.Patching : FleetSection.Health;
                Item(stale, "Clear results").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Item(stale, "Remove offline").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Require(a.CommandResult == "Retain" && vm.Computers.Contains(b), "Stale Actions affected previous tab.");
                shell.ActiveFleetSection = section;
                Item(Menu(), "Remove offline").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Require(vm.Computers.Count == 1 && ReferenceEquals(vm.Computers[0], a), "Remove offline scope changed.");
                vm.Computers.Clear(); vm.SetSelection([]);
            }
        }
        finally { window.DataContext = null; window.Close(); foreach (var vm in created) vm.Dispose(); }
    }

    private static void VerifyEmptyViews()
    {
        var help = new HelpWindow();
        try
        {
            var vm = (HelpViewModel)help.DataContext;
            int original = vm.Topics.Cast<object>().Count();
            vm.SearchText = "DUMMY-NO-HELP-XYZ"; Pump();
            Require(vm.NoMatches && ((FrameworkElement)help.FindName("NoHelpMatches")).Visibility == Visibility.Visible, "Help no-match guidance missing.");
            vm.ClearSearchCommand.Execute(null); Pump();
            Require(!vm.NoMatches && vm.Topics.Cast<object>().Count() == original, "Clear help search did not recover topics.");
        }
        finally { help.Close(); }
        using var workspace = WorkspaceProbe.CreateWorkspace();
        var view = new WorkspaceView { DataContext = workspace };
        try
        {
            foreach (bool patching in new[] { false, true })
            {
                workspace.IsUpdateMode = patching;
                view.Measure(new Size(800, 600)); view.Arrange(new Rect(0, 0, 800, 600)); view.UpdateLayout(); Pump();
                Require(((Button)view.FindName("EmptyPasteMachinesButton")).ActualWidth > 0
                    && ((Button)view.FindName("EmptyOpenListButton")).ActualWidth > 0, "Empty Fleet actions are not laid out.");
            }
        }
        finally { view.DataContext = null; }
    }

    private static void VerifyScrollbars()
    {
        var viewer = new ScrollViewer { Width = 320, Height = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Visible, Content = new Border { Width = 1600, Height = 1800 } };
        var host = new Window { Width = 360, Height = 280, Content = viewer, ShowInTaskbar = false };
        RunDialog(host, () =>
        {
        viewer.Measure(new Size(320, 220)); viewer.Arrange(new Rect(0, 0, 320, 220)); viewer.UpdateLayout(); Pump();
        foreach (var orientation in new[] { Orientation.Vertical, Orientation.Horizontal })
        {
            var bar = Descendants(viewer).OfType<ScrollBar>().Single(b => b.Orientation == orientation);
            bar.ApplyTemplate(); viewer.UpdateLayout();
            var track = (Track)bar.Template.FindName("PART_Track", bar);
            Require(track.IncreaseRepeatButton.IsEnabled && track.DecreaseRepeatButton.IsEnabled, "Track paging disabled.");
            double Offset() => orientation == Orientation.Vertical ? viewer.VerticalOffset : viewer.HorizontalOffset;
            void Click(RepeatButton button) => typeof(RepeatButton).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, null);
            double start = Offset();
            Click(track.IncreaseRepeatButton); Pump(); viewer.UpdateLayout();
            Require(Offset() > start, orientation + " track click did not page forward.");
            Click(track.DecreaseRepeatButton); Pump(); viewer.UpdateLayout();
            Require(Offset() < (orientation == Orientation.Vertical ? viewer.ViewportHeight : viewer.ViewportWidth), orientation + " track click did not page back.");
            var thumb = track.Thumb; thumb.ApplyTemplate(); viewer.UpdateLayout();
            var root = (FrameworkElement)VisualTreeHelper.GetChild(thumb, 0);
            Require((orientation == Orientation.Vertical ? root.ActualWidth : root.ActualHeight) >= 14, "Thumb pointer area remained narrow.");
            var edge = orientation == Orientation.Vertical ? new Point(1, root.ActualHeight / 2) : new Point(root.ActualWidth / 2, 1);
            Require(root.InputHitTest(edge) is not null, "Thumb transparent edge cannot receive pointer input.");
            start = Offset();
            thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            thumb.RaiseEvent(new DragDeltaEventArgs(orientation == Orientation.Horizontal ? 20 : 0, orientation == Orientation.Vertical ? 20 : 0)
                { RoutedEvent = Thumb.DragDeltaEvent });
            thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            Pump(); viewer.UpdateLayout();
            Require(Offset() > start, orientation + " thumb drag stopped scrolling.");
        }
        });
    }

    private static void VerifyFolders(string root)
    {
        var store = new ComputerListStore(Path.Combine(root, "lists"));
        store.Save("DUMMY list", ["DUMMY-A", "DUMMY-B"]);
        string? revision = store.GetRevision("DUMMY list");
        var folders = new ComputerListFolders(store); folders.Create("October"); folders.Create("November");
        var snapshot = folders.Snapshot(["DUMMY list"]);
        var parent = new MenuItem();
        int opens = 0, moves = 0;
        typeof(MainWindow).GetMethod("AddOrganizedListItems", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
            [parent, new[] { "DUMMY list" }, snapshot, (Action<string>)(_ => opens++)]);
        typeof(MainWindow).GetMethod("AttachListMoveMenus", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
            [parent, (Action<string>)(name => { Require(name == "DUMMY list", "Move targeted another list."); moves++; }), true]);
        var leaf = parent.Items.OfType<MenuItem>().Single(i => i.Tag is string);
        ((MenuItem)leaf.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Require(moves == 1 && opens == 0, "Move also opened the saved list.");
        var picker = new ListFolderPickerWindow("DUMMY list", snapshot, folder => folders.Move("DUMMY list", folder));
        RunDialog(picker, () =>
        {
            var search = (Wpf.Ui.Controls.TextBox)picker.FindName("FolderSearch");
            var choices = (ListBox)picker.FindName("Destinations");
            var move = (Button)picker.FindName("MoveButton");
            Require(!move.IsEnabled, "Current folder allows a redundant move.");
            search.Text = "DUMMY-missing";
            Require(choices.Items.Count == 0 && !move.IsEnabled, "Empty filter still allows a move.");
            search.Text = "oct";
            Require(choices.Items.Count == 1 && folders.Snapshot(["DUMMY list"]).FolderFor("DUMMY list") is null, "Typing moved the list.");
            choices.SelectedIndex = 0;
            Require(move.IsEnabled, "Destination cannot be selected.");
            move.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        Require(folders.Snapshot(["DUMMY list"]).FolderFor("DUMMY list") == "October" && store.GetRevision("DUMMY list") == revision,
            "Folder move changed list contents or failed to persist.");
        var canceled = new ListFolderPickerWindow("DUMMY list", folders.Snapshot(["DUMMY list"]), _ => throw new Exception("Cancel moved a list."));
        RunDialog(canceled, () => canceled.Close());
        var failed = new ListFolderPickerWindow("DUMMY list", folders.Snapshot(["DUMMY list"]), _ => throw new IOException("Synthetic write failure"));
        RunDialog(failed, () =>
        {
            ((ListBox)failed.FindName("Destinations")).SelectedIndex = 0; // root, outside folders
            ((Button)failed.FindName("MoveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(((TextBlock)failed.FindName("ErrorText")).Text.Contains("Synthetic write failure"), "Move failure was hidden.");
            failed.Close();
        });
        Require(folders.Snapshot(["DUMMY list"]).FolderFor("DUMMY list") == "October", "Cancel/failure changed assignment.");
    }

    internal static bool? RunDialog(Window window, Action action)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -20000; window.Top = -20000;
        Exception? failure = null;
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try { action(); } catch (Exception ex) { failure = ex; }
            finally { if (window.IsVisible) window.Close(); }
        }));
        bool? result = window.ShowDialog();
        if (failure is not null) throw failure;
        return result;
    }
    private static MenuItem Item(ContextMenu menu, string prefix) => menu.Items.OfType<MenuItem>().Single(i => i.Header.ToString()!.StartsWith(prefix));
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class FakeLog : IActivityLog
    {
        public ObservableCollection<LogEntry> Entries { get; } = [];
        public void Info(string? machine, string message) { }
        public void Warn(string? machine, string message) { }
        public void Error(string? machine, string message) { }
        public void Clear() { }
    }
}
