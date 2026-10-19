using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Vivre.Core.Configuration;
using Vivre.Core.Credentials;
using Vivre.Core.Logging;
using Vivre.Core.Models;
using Vivre.Core.Updates;
using Vivre.Desktop;
using Vivre.Desktop.ViewModels;

namespace Vivre.PerformanceAudit;

internal static class TabHandoffProbe
{
    public static object Run(string root)
    {
        File.WriteAllText(Path.Combine(root, "settings.json"), "{\"AutoCheckOnLoad\":true}");
        var settings = (AppSettingsStore)Activator.CreateInstance(typeof(AppSettingsStore), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [Path.Combine(root, "settings.json"), null], null)!;
        var shared = new SharedSettingsStore(Path.Combine(root, "shared"));
        var created = new List<WorkspaceViewModel>();
        bool failFactory = false;
        bool pasteTesting = false;
        WorkspaceViewModel Factory()
        {
            if (failFactory) throw new IOException("Synthetic creation failure");
            var vm = WorkspaceProbe.CreateWorkspace();
            Set(vm, "_appSettings", settings); Set(vm, "_shared", shared);
            vm.IsMonitoring = !pasteTesting; // production default; handoff must stop it before adding rows
            created.Add(vm);
            return vm;
        }
        var shell = new ShellViewModel(Factory, null!, new CredentialStore(), new FakeLog());
        var window = new MainWindow { DataContext = shell };
        try
        {
            var selectedHealth = shell.SelectedHealthTab!;
            shell.NewTabCommand.Execute(null);
            var source = shell.SelectedHealthTab!;
            source.Title = "Monthly servers";
            source.IsMonitoring = false;
            var a = new Computer("DUMMY-A") { CommandResult = "Keep result" };
            var b = new Computer("DUMMY-B");
            source.Computers.Add(a); source.Computers.Add(b);
            source.SelectedComputers.Add(a); source.FocusedComputer = a;
            source.FilterText = "DUMMY-A";
            source.SelectedSource = UpdateSource.MicrosoftUpdate; source.IncludeDrivers = true;
            source.ExcludeText = "SQL"; source.IsInstalledMode = true;
            source.IsBusy = true;
            shell.SelectedHealthTab = selectedHealth; // right-click an inactive tab
            var oldPatch = shell.SelectedPatchingTab!;
            oldPatch.IsMonitoring = false;
            oldPatch.Computers.Add(new Computer("DUMMY-EXISTING"));
            oldPatch.SelectedSource = UpdateSource.Managed;
            var healthTabs = (TabControl)window.FindName("HealthTabs");
            var header = (StackPanel)healthTabs.ItemTemplate.LoadContent();
            header.DataContext = source;
            var menu = header.ContextMenu!;
            menu.PlacementTarget = header;
            Pump();
            var resolver = typeof(MainWindow).GetMethod("ResolveSaveListTab", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var save = menu.Items.OfType<MenuItem>().Single(i => i.Header.ToString()!.StartsWith("Save tab as list"));
            Require(ReferenceEquals(resolver.Invoke(window, new object[] { save }), source), "Save resolved active tab instead of clicked inactive Health tab.");
            Require(resolver.Invoke(window, new object[] { new MenuItem() }) is null, "Unbound save fell back to active tab.");
            save.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump(); // hidden window: no prompt or persistence, exercises handler and guard teardown
            Require(!(bool)typeof(MainWindow).GetField("_saveListPromptOpen", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!, "Save guard remained stuck.");
            var action = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Open tab in Patching"));
            Require(ReferenceEquals(action.DataContext, source), "Tab menu did not bind to the clicked tab.");
            action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump(); // drain deferred auto-check callbacks, if any were wrongly queued
            var target = shell.SelectedPatchingTab!;
            Require(shell.PatchingTabs.Count == 2 && target != oldPatch && target.Title == source.Title, "Handoff did not create a same-title tab.");
            Require(target.Computers.Select(c => c.Name).SequenceEqual(new[] { "DUMMY-A", "DUMMY-B" }), "Handoff used selection/filter instead of all names.");
            Require(!ReferenceEquals(target.Computers[0], a) && target.Computers[0].CommandResult != a.CommandResult, "Handoff shared rows/results.");
            Require(target.SelectedSource == UpdateSource.WindowsUpdate && !target.IncludeDrivers && target.ExcludeText == "" && !target.IsInstalledMode,
                "Handoff copied source patch options.");
            Require(!target.IsMonitoring && !target.IsBusy && target.SavedListName is null, "Handoff started work or inherited saved-list identity.");
            Require(source.IsBusy && source.SelectedComputers.Contains(a) && ReferenceEquals(source.FocusedComputer, a) && a.CommandResult == "Keep result", "Source state changed.");
            Require(oldPatch.Computers.Single().Name == "DUMMY-EXISTING" && oldPatch.SelectedSource == UpdateSource.Managed, "Existing Patching tab changed.");
            Require(shell.ActiveFleetSection == FleetSection.Patching && ReferenceEquals(shell.SelectedTab, target)
                && ((Wpf.Ui.Controls.NavigationViewItem)window.FindName("PatchingNavItem")).IsActive, "Navigation/header routing did not switch to Patching.");
            Require(ReferenceEquals(shell.SelectedHealthTab, selectedHealth), "Inactive source handoff changed Health selection.");
            Require(!shell.OpenTabInPatching(oldPatch), "Accepted a Patching source.");
            var patchHeader = (StackPanel)((TabControl)window.FindName("PatchingTabs")).ItemTemplate.LoadContent();
            patchHeader.DataContext = oldPatch;
            patchHeader.ContextMenu!.PlacementTarget = patchHeader;
            Pump();
            var patchSave = patchHeader.ContextMenu.Items.OfType<MenuItem>().Single(i => i.Header.ToString()!.StartsWith("Save tab as list"));
            Require(ReferenceEquals(resolver.Invoke(window, new object[] { patchSave }), oldPatch), "Save resolved active tab instead of clicked inactive Patching tab.");
            Require(!patchHeader.ContextMenu!.Items.OfType<MenuItem>().Any(i => Equals(i.Header, "Open tab in Patching")), "Action exposed in Patching.");
            Require(shell.OpenTabInPatching(source) && shell.PatchingTabs.Count == 3, "Repeated handoff reused an existing tab.");
            Require(shell.OpenTabInPatching(selectedHealth) && shell.SelectedPatchingTab!.Computers.Count == 0, "Empty tab handoff failed.");
            int count = shell.PatchingTabs.Count;
            var beforeFailure = shell.SelectedPatchingTab;
            failFactory = true;
            try { shell.OpenTabInPatching(source); throw new InvalidOperationException("Creation failure not surfaced."); }
            catch (IOException) { }
            Require(shell.PatchingTabs.Count == count && ReferenceEquals(beforeFailure, shell.SelectedPatchingTab), "Failed creation changed destination.");
            failFactory = false;
            shell.HealthTabs.Remove(source);
            Require(resolver.Invoke(window, new object[] { save }) is null, "Removed tab accepted for saving.");
            Require(!shell.OpenTabInPatching(source) && shell.PatchingTabs.Count == count, "Stale source created a tab.");
            pasteTesting = true;
            File.WriteAllText(Path.Combine(root, "settings.json"), "{\"AutoCheckOnLoad\":false}");
            foreach (var origin in new[] { selectedHealth, oldPatch })
            {
                var originalRows = origin.Computers.ToArray();
                shell.OpenPastedListInNewTab(origin, ["DUMMY-PASTE-A", "dummy-paste-a", "DUMMY-PASTE-B"]);
                var pasted = shell.SelectedTab!;
                Require(pasted != origin && pasted.IsUpdateMode == origin.IsUpdateMode && pasted.Computers.Count == 2, "Paste created wrong section or duplicated names.");
                Require(origin.Computers.SequenceEqual(originalRows), "New-tab paste changed original rows.");
                Require(pasted.SelectedSource == UpdateSource.WindowsUpdate, "Paste inherited another tab's update source.");
                int before = shell.AllTabs.Count();
                try { shell.OpenPastedListInNewTab(origin, []); throw new Exception("Empty paste accepted."); } catch (ArgumentException) { }
                Require(shell.AllTabs.Count() == before, "Empty paste created a tab.");
            }
            var paste = new LoadComputersWindow(selectedHealth, names => shell.OpenPastedListInNewTab(selectedHealth, names));
            Require(((RadioButton)paste.FindName("AddMode")).IsChecked == true && ((RadioButton)paste.FindName("NewTabMode")).Visibility == Visibility.Visible, "Paste choices/default incorrect.");
            ((RadioButton)paste.FindName("NewTabMode")).IsChecked = true;
            Require(((Wpf.Ui.Controls.Button)paste.FindName("ApplyButton")).Content.ToString() == "Create tab", "New-tab submit label incorrect.");
            typeof(LoadComputersWindow).GetMethod("OnLoad", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(paste, [paste, new RoutedEventArgs()]);
            Require(((TextBlock)paste.FindName("ErrorText")).Text.Length > 0, "Blank new-tab input lacks feedback.");
            paste.Close();
            return new { pass = true, inactiveTabMenu = true, allNames = true, independentRowsAndDefaults = true,
                sourcePreserved = true, noAutomaticChecks = true, navigation = true, emptyRepeatedStaleAndFailure = true,
                scope = "Production XAML/handler/shell with dummy workspaces, remote-service null sentinels and temporary settings; no live hosts or visible UI" };
        }
        finally
        {
            window.DataContext = null;
            window.Close();
            foreach (var vm in created) vm.Dispose();
        }
    }
    private static void Set(object obj, string name, object value) => obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(obj, value);
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
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
