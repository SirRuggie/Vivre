using System.Diagnostics;
using System.Reflection;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Vivre.Core.Credentials;
using Vivre.Core.Models;
using Vivre.Core.Updates;
using Vivre.Desktop;
using Vivre.Desktop.ViewModels;

namespace Vivre.PerformanceAudit;

/// <summary>U-45–U-48 production control/dispatch checks with dummy rows and an inert patch service.</summary>
internal static class OperationalClarityProbe
{
    public static object Run()
    {
        var originalContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            CheckTargets();
            CheckCredentials();
            CheckDecisions();
            foreach (string outcome in new[] { "done", "pending", "deferred", "error", "throw", "cancel", "locked" })
                CheckCleanup(outcome);
            return new { pass = true, selectionFirstAndLiveCount = true, appliedIdentityAndWindowsLogin = true,
                cancelEscapeCloseAndExplicitSkip = true, cleanupActiveTerminalAndLateProgress = true,
                scope = "Production WPF controls, modal dismissal paths, dispatch delegates and injected cleanup service; dummy hosts only, no live installs or production settings writes" };
        }
        finally { SynchronizationContext.SetSynchronizationContext(originalContext); }
    }

    private static void CheckTargets()
    {
        using var vm = WorkspaceProbe.CreateWorkspace();
        var cu = new Computer("DUMMY-CU") { OsBuild = 14393, RequiresStagedPatching = true };
        var other = new Computer("DUMMY-OTHER") { OsBuild = 17763 };
        vm.Computers.Add(cu); vm.Computers.Add(other);
        var view = new WorkspaceView { DataContext = vm };
        var summary = (TextBlock)view.FindName("CuTargetSummaryText");
        void Check(Computer[] selected, Computer[] expected, string caption)
        {
            vm.SetSelection(selected); Drain();
            Require(vm.Server2016ActionTargets().SequenceEqual(expected), "CU scope broadened or lost eligible targets.");
            Require(summary.Text == caption, "CU scope count is stale: " + summary.Text);
        }
        Check([], [cu], "Install CU / Verify: 1 in tab");
        Check([other], [], "Install CU / Verify: 0 selected");
        Check([cu, other], [cu], "Install CU / Verify: 1 selected");
        cu.RequiresStagedPatching = false; Drain();
        Require(summary.Text == "Install CU / Verify: 0 selected", "CU flag change did not refresh count.");
        var guidance = new StagedPatchingNeededDialog(true);
        try
        {
            Require(((Wpf.Ui.Controls.InfoBar)guidance.FindName("GuidanceBar")).Title.Contains("selected"),
                "Ineligible selection guidance does not explain scope.");
        }
        finally { guidance.Close(); view.DataContext = null; }
    }

    private static void CheckCredentials()
    {
        using var password = new SecureString();
        password.AppendChar('x'); password.MakeReadOnly();
        var store = new CredentialStore { Current = new ConnectionCredential("DUMMY", "operator", password) };
        var vm = new SettingsViewModel(store);
        var page = new SettingsPage();
        Set(page, "_credVm", vm);
        ((FrameworkElement)page.FindName("CredentialCard")).DataContext = vm;
        var summary = (TextBlock)page.FindName("AppliedCredentialText");
        var apply = (Wpf.Ui.Controls.Button)page.FindName("ApplyCredentialsButton");
        var login = (RadioButton)page.FindName("UseLoginRadio");
        Drain();
        Require(summary.Text == @"Applied: DUMMY\operator", "Initial applied identity missing.");
        login.SetCurrentValue(RadioButton.IsCheckedProperty, true); Drain();
        Require(!vm.UseExplicitCredentials && apply.IsEnabled, "Windows login disables Apply or draft binding fails.");
        Require(store.HasExplicitCredential && summary.Text == @"Applied: DUMMY\operator", "Draft falsely changed applied identity.");
        apply.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Drain();
        Require(!store.HasExplicitCredential && summary.Text.StartsWith("Applied: Windows login ("), "Apply did not clear credentials and update identity.");
        ((RadioButton)page.FindName("UseExplicitRadio")).SetCurrentValue(RadioButton.IsCheckedProperty, true);
        vm.UserName = "other";
        ((PasswordBox)page.FindName("PasswordBox")).Password = "dummy";
        Drain();
        Require(!store.HasExplicitCredential && summary.Text.StartsWith("Applied: Windows login ("), "Explicit draft silently applied.");
        apply.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Drain();
        Require(store.Current?.DisplayName == @"DUMMY\other" && summary.Text == @"Applied: DUMMY\other", "Explicit Apply/summary failed.");
        store.Current?.Password.Dispose();
    }

    private static void CheckDecisions()
    {
        var cu = new Computer("DUMMY-CU") { OsBuild = 14393, RequiresStagedPatching = true };
        var plan = new StagedInstallPlan([cu], [new Computer("DUMMY-NORMAL")], []);
        var interaction = typeof(WorkspaceView).Assembly.GetType("Vivre.Desktop.StagedInstallInteraction")!;
        var execute = interaction.GetMethod("ExecuteChoiceAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var choice in Enum.GetValues<StagedInstallChoice>().Append((StagedInstallChoice)999))
        {
            int normal = 0, stage = 0, minor = 0;
            Func<Task> n = () => { normal++; return Task.CompletedTask; };
            Func<Task> s = () => { stage++; return Task.CompletedTask; };
            Func<Task> m = () => { minor++; return Task.CompletedTask; };
            ((Task)execute.Invoke(null, [choice, n, s, m])!).GetAwaiter().GetResult();
            Require(normal == (choice is StagedInstallChoice.StageCu or StagedInstallChoice.MinorOnly or StagedInstallChoice.SkipCu ? 1 : 0)
                && stage == (choice == StagedInstallChoice.StageCu ? 1 : 0)
                && minor == (choice == StagedInstallChoice.MinorOnly ? 1 : 0), "Wrong install choice dispatch: " + choice);
        }
        foreach (bool minorStep in new[] { false, true })
        foreach (string exit in new[] { "cancel", "escape", "close" })
        {
            var dialog = new StagedInstallDecisionDialog(plan)
            {
                Opacity = 0, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000, Top = -20000
            };
            Exception? failure = null;
            var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            watchdog.Tick += (_, _) => { failure = new TimeoutException($"Modal dismissal timed out: {exit}, minor step={minorStep}"); dialog.Close(); };
            dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var skip = (Wpf.Ui.Controls.Button)dialog.FindName("SkipCuButton");
                    Require(skip.Visibility == Visibility.Visible && skip.Content.ToString()!.Contains("1"), "Explicit skip/count missing.");
                    if (minorStep) Call(dialog, "OnMinorOnly", dialog, new RoutedEventArgs());
                    if (exit == "cancel")
                        ((Wpf.Ui.Controls.Button)dialog.FindName("CancelInstallButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    else if (exit == "escape")
                    {
                        // WPF access-key scope is the presentation source, not the Window object.
                        AccessKeyManager.ProcessKey(PresentationSource.FromVisual(dialog), "\x1b", false);
                    }
                    else dialog.Close();
                }
                catch (Exception ex) { failure = ex; dialog.Close(); }
            }), DispatcherPriority.ApplicationIdle);
            try { watchdog.Start(); dialog.ShowDialog(); }
            finally { watchdog.Stop(); dialog.Close(); }
            if (failure is not null) throw failure;
            Require(dialog.Choice == StagedInstallChoice.Cancel, "Dismissal authorized an install: " + exit);
        }
        var cuOnly = new StagedInstallDecisionDialog(new StagedInstallPlan([cu], [], []));
        try { Require(((UIElement)cuOnly.FindName("SkipCuButton")).Visibility == Visibility.Collapsed, "Skip offers an empty remainder."); }
        finally { cuOnly.Close(); }
    }

    private static void CheckCleanup(string outcome)
    {
        using var vm = WorkspaceProbe.CreateWorkspace();
        var patch = DispatchProxy.Create<IPatchService, CleanupProxy>();
        var fake = (CleanupProxy)(object)patch;
        Set(vm, "_patch", patch);
        var row = new Computer("DUMMY-CLEANUP") { OsBuild = 14393 };
        vm.Computers.Add(row);
        using var cancel = new CancellationTokenSource();
        var detailVm = vm.CreateDetailViewModel(row);
        var detail = new ComputerDetailWindow(detailVm);
        var label = (TextBlock)detail.FindName("PatchPhaseLabel");
        try
        {
            var task = (Task)Call(vm, "ComponentCleanupLcuRowAsync", row, cancel.Token)!;
            Require(fake.Progress is not null, "Cleanup did not reach injected service.");
            fake.Progress!.Report(new(PatchPhase.Scanning, "bootstrap")); Drain();
            Require(row.UpdatePhase == "Cleaning" && row.UpdateMessage!.StartsWith("Cleaning"), "Bootstrap changed cleanup to scanning.");
            fake.Progress.Report(new(PatchPhase.Cleaning, "cleaning", 70)); Drain();
            Require(label.Text == "Cleaning up", "Detail phase differs from cleanup row.");
            if (outcome == "throw") fake.Completion.SetException(new InvalidOperationException("dummy failure"));
            else if (outcome == "cancel") { cancel.Cancel(); fake.Completion.SetCanceled(cancel.Token); }
            else
            {
                var final = outcome switch
                {
                    "pending" => new HostPatchStatus(PatchPhase.PendingReboot, "reboot needed", RebootPending: true),
                    "deferred" => new HostPatchStatus(PatchPhase.Deferred, "deferred", RebootPending: true),
                    "error" => HostPatchStatus.Failed("dummy failure"),
                    "locked" => HostPatchStatus.Failed("locked") with { CleanupFacts = new(5, true, 0) },
                    _ => new HostPatchStatus(PatchPhase.Done, "done")
                };
                fake.Progress.Report(final); Drain();
                string terminalPhase = row.UpdatePhase!;
                fake.Progress.Report(new(PatchPhase.Cleaning, "late", 2)); Drain();
                Require(row.UpdatePhase == terminalPhase, "Progress replaced an observed terminal.");
                fake.Completion.SetResult(final);
            }
            PumpUntil(() => task.IsCompleted);
            Require(outcome == "cancel" ? task.IsCanceled : task.IsCompletedSuccessfully, "Cleanup task ended incorrectly.");
            Drain();
            string phase = row.UpdatePhase!, message = row.UpdateMessage!;
            Require(!row.IsPatching, "Cleanup left IsPatching set.");
            if (outcome is "done" or "locked") Require(phase == "Cleaned" && label.Text == "Cleaned", "Cleanup success is not labeled Cleaned.");
            if (outcome is "error" or "throw") Require(phase == "Error", "Failure lost error state.");
            if (outcome == "cancel") Require(phase == "Idle" && message == "Cancelled", "Stop did not clear active state.");
            if (outcome is "pending" or "deferred") Require(row.RebootRequired == true, "Terminal lost reboot bookkeeping.");
            fake.Progress.Report(new(PatchPhase.Scanning, "very late"));
            fake.Progress.Report(new(PatchPhase.Cleaning, "very late", 9));
            fake.Progress.Report(HostPatchStatus.Failed("late error")); Drain();
            Require(row.UpdatePhase == phase && row.UpdateMessage == message, "Late progress replaced final result.");
        }
        finally { detailVm.StopRequeryTimer(); detail.Close(); }
    }

    public class CleanupProxy : DispatchProxy
    {
        public IProgress<HostPatchStatus>? Progress;
        public TaskCompletionSource<HostPatchStatus> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name != nameof(IPatchService.ComponentCleanupLcuAsync))
                throw new InvalidOperationException("Unexpected remote service call: " + method?.Name);
            Progress = (IProgress<HostPatchStatus>)args![2]!;
            return Completion.Task;
        }
    }

    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void PumpUntil(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.Elapsed < TimeSpan.FromSeconds(5)) { Drain(); Thread.Sleep(1); }
        Require(done(), "Local async probe timed out.");
    }
    private static object? Call(object obj, string method, params object[] args) =>
        obj.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(obj, args);
    private static void Set(object obj, string field, object value) =>
        obj.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(obj, value);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
