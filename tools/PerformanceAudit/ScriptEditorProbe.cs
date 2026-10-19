using System.Collections.ObjectModel;
using System.IO;
using System.Management.Automation;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using Vivre.Core.Credentials;
using Vivre.Core.Logging;
using Vivre.Core.Models;
using Vivre.Core.PowerShell;
using Vivre.Core.Scripts;
using Vivre.Desktop;
using Vivre.Desktop.ViewModels;

namespace Vivre.PerformanceAudit;

internal static class ScriptEditorProbe
{
    public static object Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "Vivre-script-editor-" + Guid.NewGuid().ToString("N"));
        string live = Path.Combine(root, "live");
        string bundle = Path.Combine(root, "bundle");
        Directory.CreateDirectory(Path.Combine(bundle, "Info"));
        File.WriteAllText(Path.Combine(bundle, "Info", "Built.ps1"), "built-pristine");
        var library = new ScriptLibrary(live, bundle);
        ScriptFile a = library.Save("A", "a-one", "Probe");
        ScriptFile b = library.Save("B", "b-one", "Probe");
        var errors = new List<string>();
        var window = new ScriptRunnerWindow([], new FakeHost(), new CredentialStore(), new FakeLog(), library, a)
        {
            ErrorPresenterOverride = (title, message) => errors.Add(title + ": " + message)
        };
        try
        {
            var editor = RequireControl<TextEditor>(window, "Editor");
            var scriptList = RequireControl<ListView>(window, "ScriptList");
            var vm = (ScriptRunnerViewModel)window.DataContext;
            PumpBindings(window);
            Require(scriptList.Items.Count == vm.Scripts.Count, "Runner grouped list binding did not materialize all scripts.");
            Require(vm.SaveCategory == "Probe", "Runner initial script did not preserve its category.");
            int prompts = 0;
            window.UnsavedChangesPromptOverride = () => { prompts++; return ScriptEditChoice.Cancel; };
            Require(window.ConfirmCanClose(), "A newly opened editor was falsely dirty.");
            Require(prompts == 0, "A newly opened editor prompted to save.");

            VerifySearch(window, editor, scriptList);
            editor.Text = "a-edited";
            window.UnsavedChangesPromptOverride = () => ScriptEditChoice.Save;
            scriptList.SelectedItem = vm.Scripts.Single(s => s.Name == "B");
            Require(vm.SelectedScript?.Name == "B" && editor.Text == "b-one",
                $"Save-then-switch inconsistent: selected='{vm.SelectedScript?.Name}', editor='{editor.Text}', list='{(scriptList.SelectedItem as ScriptFile)?.Name}'.");
            Require(library.Load(library.List().Single(s => s.Name == "A")) == "a-edited", "Save choice did not persist A.");
            Require(library.List().Count(s => s.Name == "A") == 1, "Saving the initial script created a duplicate in another category.");

            editor.Text = "b-dirty";
            window.UnsavedChangesPromptOverride = () => ScriptEditChoice.Cancel;
            scriptList.SelectedItem = vm.Scripts.Single(s => s.Name == "A");
            Require(vm.SelectedScript?.Name == "B" && editor.Text == "b-dirty",
                "Cancel did not restore the captured selection and dirty editor.");

            library.Save("B", "b-external", "Probe");
            Require(editor.Text == "b-dirty", "External refresh overwrote dirty editor content.");
            window.UnsavedChangesPromptOverride = () => ScriptEditChoice.Discard;
            Require(window.ConfirmCanClose(), "Discard did not clear dirty state.");
            window.UnsavedChangesPromptOverride = () => ScriptEditChoice.Cancel;
            Require(!window.ConfirmCanClose(), "Discard prematurely marked unsaved text clean before replacement.");
            window.UnsavedChangesPromptOverride = () => ScriptEditChoice.Discard;
            scriptList.SelectedItem = vm.Scripts.Single(s => s.Name == "A");
            Require(vm.SelectedScript?.Name == "A" && editor.Text == "a-edited",
                "Discard did not proceed to the selected replacement.");
            library.Save("A", "a-clean-refresh", "Probe");
            Require(editor.Text == "a-clean-refresh", "Clean editor did not accept external refresh.");
            vm.SaveCategory = "Typed Runner Folder";
            library.Save("Runner category trigger", "trigger", "Probe");
            Require(vm.SaveCategory == "Typed Runner Folder", "Runner refresh replaced a typed save category.");
            vm.SaveCategory = "Probe";

            ScriptFile built = library.List().Single(s => s.Name == "Built");
            scriptList.SelectedItem = vm.Scripts.Single(s => s.FullPath == built.FullPath);
            Require(string.IsNullOrEmpty(vm.SelectedScriptNotice), "A pristine built-in showed the edited-copy notice.");
            library.Save("Built", "built-edited", "Info");
            Require(!string.IsNullOrEmpty(vm.SelectedScriptNotice), "An edited built-in copy did not show its reset notice.");

            VerifyScriptsPage(library);
            VerifyOwnerCloseTransaction(library, a, b);
            VerifyDirectCloseRechecks(window, editor);

            editor.Text = "cannot-save";
            window.UnsavedChangesPromptOverride = () => ScriptEditChoice.Save;
            Directory.Delete(live, recursive: true);
            File.WriteAllText(live, "blocks directory recreation");
            Require(!window.ConfirmCanClose(), "A save failure allowed editor close.");
            Require(errors.Any(e => e.Contains("not saved", StringComparison.OrdinalIgnoreCase)),
                "A save failure was not surfaced through the isolated error seam.");

            vm.IsBusy = true;
            Require(!window.ConfirmCanClose(), "An active script run allowed editor close.");
            Require(errors.Any(e => e.Contains("still running", StringComparison.OrdinalIgnoreCase)),
                "Active-run close refusal was not surfaced.");

            return new { librarySearchPreservesEditsAndRenames = true, initialClean = true, saveThenSwitchConsistent = true, cancelRestoresSelection = true,
                dirtyRefreshPreserved = true, discardRequiresReplacement = true, cleanRefreshApplied = true, saveFailureVetoed = true,
                busyCloseVetoed = true, builtInNotices = true, scriptsPageWiring = true,
                ownerCloseTransactional = true, directCloseRechecked = true,
                scope = "Real ScriptRunnerWindow controls; temporary library and fake host; no dialogs or remote operations" };
        }
        finally
        {
            window.UnsavedChangesPromptOverride = () => ScriptEditChoice.Discard;
            ((ScriptRunnerViewModel)window.DataContext).IsBusy = false;
            window.Close();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            else if (File.Exists(root)) File.Delete(root);
        }
    }

    private static void VerifyScriptsPage(IScriptLibrary library)
    {
        var page = new ScriptsPage();
        page.Initialize(library);
        var editor = RequireControl<TextEditor>(page, "Editor");
        var scriptList = RequireControl<ListView>(page, "ScriptList");
        var vm = (ScriptsViewModel)page.DataContext;
        PumpBindings(page);
        Require(scriptList.Items.Count == vm.Scripts.Count, "Scripts page grouped list binding did not materialize all scripts.");
        try
        {
            int prompts = 0;
            page.UnsavedChangesPromptOverride = () => { prompts++; return ScriptEditChoice.Cancel; };
            Require(page.ConfirmCanClose() && prompts == 0, "A newly initialized Scripts page was falsely dirty.");

            scriptList.SelectedItem = vm.Scripts.Single(s => s.Name == "A");
            VerifySearch(page, editor, scriptList);
            editor.Text = "page-a-edited";
            page.UnsavedChangesPromptOverride = () => ScriptEditChoice.Save;
            scriptList.SelectedItem = vm.Scripts.Single(s => s.Name == "B");
            Require(vm.SelectedScript?.Name == "B" && editor.Text == library.Load(vm.SelectedScript),
                "Scripts page save-then-switch left selection and editor inconsistent.");
            Require(library.Load(library.List().Single(s => s.Name == "A")) == "page-a-edited",
                "Scripts page save choice did not persist the previous script.");

            editor.Text = "page-b-dirty";
            page.UnsavedChangesPromptOverride = () => ScriptEditChoice.Cancel;
            scriptList.SelectedItem = vm.Scripts.Single(s => s.Name == "A");
            Require(vm.SelectedScript?.Name == "B" && editor.Text == "page-b-dirty",
                "Scripts page cancel did not restore selection and dirty editor.");
            library.Save("B", "page-b-external", "Probe");
            Require(editor.Text == "page-b-dirty", "Scripts page external refresh overwrote dirty text.");

            page.UnsavedChangesPromptOverride = () => ScriptEditChoice.Discard;
            scriptList.SelectedItem = vm.Scripts.Single(s => s.Name == "A");
            Require(vm.SelectedScript?.Name == "A" && editor.Text == "page-a-edited",
                "Scripts page discard did not apply the selected replacement.");
            library.Save("A", "page-a-clean-refresh", "Probe");
            Require(editor.Text == "page-a-clean-refresh", "Scripts page clean editor did not accept external refresh.");
            vm.SaveCategory = "Typed Page Folder";
            library.Save("Page category trigger", "trigger", "Probe");
            Require(vm.SaveCategory == "Typed Page Folder", "Scripts page refresh replaced a typed save category.");
            vm.SaveCategory = "Probe";
        }
        finally
        {
            page.Dispose();
        }
    }

    private static void VerifySearch(FrameworkElement owner, TextEditor editor, ListView list)
    {
        var search = RequireControl<Wpf.Ui.Controls.TextBox>(owner, "ScriptSearchBox");
        ScriptFile selected = (ScriptFile)list.SelectedItem;
        string text = editor.Text;
        editor.Text += " # dirty search";
        search.Text = "built";
        PumpBindings(owner);
        Require(list.Items.Cast<ScriptFile>().Any(s => s.Name == "Built"), "Search failed case-insensitive name match.");
        Require(!list.Items.Cast<ScriptFile>().Any(s => s.Name == "B"), "Search retained unrelated script.");
        // Cancel a switch to another visible result without discarding the active query or edits.
        list.SelectedItem = list.Items.Cast<ScriptFile>().Single(s => s.Name == "Built");
        Require(((ScriptFile)list.SelectedItem).FullPath == selected.FullPath && editor.Text == text + " # dirty search",
            "Cancel during filtered selection failed to preserve the editor and selection.");
        search.Text = "no-match-xyz";
        Require(list.Items.Count == 1 && ((ScriptFile)list.SelectedItem).FullPath == selected.FullPath,
            "Search lost the current script selection.");
        Require(editor.Text == text + " # dirty search", "Search changed dirty editor text.");
        search.Text = "pRoBe";
        Require(list.Items.Cast<ScriptFile>().Any(s => s.Name == "B"), "Search failed category match.");
        search.Clear();
        foreach (var choice in new[] { ScriptEditChoice.Discard, ScriptEditChoice.Save })
        {
            if (owner is ScriptRunnerWindow runnerWindow) runnerWindow.UnsavedChangesPromptOverride = () => choice;
            else ((ScriptsPage)owner).UnsavedChangesPromptOverride = () => choice;
            editor.Text = text + " # switch " + choice;
            search.Text = "built";
            list.SelectedItem = list.Items.Cast<ScriptFile>().Single(s => s.Name == "Built");
            Require(((ScriptFile)list.SelectedItem).Name == "Built" && editor.Text.StartsWith("built-"), "Filtered switch failed for " + choice);
            Require(list.Items.Count == 1, "Previously open nonmatching script remained pinned after switch.");
            search.Clear();
            list.SelectedItem = list.Items.Cast<ScriptFile>().Single(s => s.FullPath == selected.FullPath);
        }
        if (owner is ScriptRunnerWindow restoredRunner) restoredRunner.UnsavedChangesPromptOverride = () => ScriptEditChoice.Cancel;
        else ((ScriptsPage)owner).UnsavedChangesPromptOverride = () => ScriptEditChoice.Cancel;
        editor.Text = text;
        // Save under a new name/category while the query excludes both identities.
        search.Text = "no-match-xyz";
        if (owner.DataContext is ScriptRunnerViewModel runner) { runner.ScriptName = "Search moved runner"; runner.SaveCategory = "Moved"; }
        else if (owner.DataContext is ScriptsViewModel page) { page.ScriptName = "Search moved page"; page.SaveCategory = "Moved"; }
        owner.GetType().GetMethod("OnSave", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, [owner, new RoutedEventArgs()]);
        PumpBindings(owner);
        Require(list.Items.Count == 1 && ((ScriptFile)list.SelectedItem).Category == "Moved", "Search failed to follow renamed saved script.");
        Require(editor.Text == text, "Save during search changed editor text.");
        search.Clear();
        list.SelectedItem = list.Items.Cast<ScriptFile>().Single(s => s.FullPath == selected.FullPath);
        Require(((ScriptFile)list.SelectedItem).FullPath == selected.FullPath, "Clearing search failed to restore library navigation.");
    }

    private static void VerifyDirectCloseRechecks(ScriptRunnerWindow window, TextEditor editor)
    {
        ((ScriptRunnerViewModel)window.DataContext).IsBusy = false;
        editor.Text += " # direct-close-dirty";
        int prompts = 0;
        window.UnsavedChangesPromptOverride = () => { prompts++; return ScriptEditChoice.Cancel; };
        MethodInfo handler = typeof(ScriptRunnerWindow).GetMethod("OnWindowClosing", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Runner close handler was not found.");
        var first = new System.ComponentModel.CancelEventArgs();
        handler.Invoke(window, [window, first]);
        var second = new System.ComponentModel.CancelEventArgs();
        handler.Invoke(window, [window, second]);
        Require(first.Cancel && second.Cancel && prompts == 2,
            "A vetoed direct close bypassed the dirty check on the next close attempt.");
    }

    private static void VerifyOwnerCloseTransaction(IScriptLibrary library, ScriptFile a, ScriptFile b)
    {
        var owner = new MainWindow();
        new System.Windows.Interop.WindowInteropHelper(owner).EnsureHandle();
        var runnerA = new ScriptRunnerWindow([], new FakeHost(), new CredentialStore(), new FakeLog(), library, a) { Owner = owner };
        var runnerB = new ScriptRunnerWindow([], new FakeHost(), new CredentialStore(), new FakeLog(), library, b) { Owner = owner };
        try
        {
            RequireControl<TextEditor>(runnerA, "Editor").Text += " # dirty-a";
            RequireControl<TextEditor>(runnerB, "Editor").Text += " # dirty-b";
            runnerA.UnsavedChangesPromptOverride = () => ScriptEditChoice.Discard;
            runnerB.UnsavedChangesPromptOverride = () => ScriptEditChoice.Cancel;

            var closing = new System.ComponentModel.CancelEventArgs();
            typeof(MainWindow).GetMethod("OnClosing", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(owner, [closing]);
            Require(closing.Cancel, "A later owned runner cancellation did not veto owner close.");

            runnerA.UnsavedChangesPromptOverride = () => ScriptEditChoice.Cancel;
            Require(!runnerA.ConfirmCanClose(), "Earlier discard preflight prematurely accepted dirty text after owner close was vetoed.");
        }
        finally
        {
            runnerA.UnsavedChangesPromptOverride = () => ScriptEditChoice.Discard;
            runnerB.UnsavedChangesPromptOverride = () => ScriptEditChoice.Discard;
            runnerA.Close();
            runnerB.Close();
            owner.Close();
        }
    }

    private static T RequireControl<T>(FrameworkElement owner, string name) where T : class
        => owner.FindName(name) as T ?? throw new InvalidOperationException($"Control '{name}' was not found.");

    private static void PumpBindings(FrameworkElement element)
    {
        element.ApplyTemplate();
        element.Measure(new Size(1200, 800));
        element.Arrange(new Rect(0, 0, 1200, 800));
        element.UpdateLayout();
        for (int i = 0; i < 3; i++)
        {
            element.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            element.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeHost : IPowerShellHost
    {
        public Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PSExecutionResult> RunRemoteAsync(string host, string script, PSCredential? credential = null, int port = 5985, bool useSsl = false, CancellationToken cancellationToken = default, bool background = false) => throw new NotSupportedException();
        public Task<PSExecutionResult> RunRemoteStreamingAsync(string host, string script, Action<PSObject> onOutput, PSCredential? credential = null, int port = 5985, bool useSsl = false, CancellationToken cancellationToken = default, bool background = false) => throw new NotSupportedException();
    }

    private sealed class FakeLog : IActivityLog
    {
        public ObservableCollection<LogEntry> Entries { get; } = [];
        public void Info(string? machine, string message) { }
        public void Warn(string? machine, string message) { }
        public void Error(string? machine, string message) { }
        public void Clear() { }
    }
}
