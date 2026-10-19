using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Vivre.Core.Models;
using Vivre.Core.Updates;
using Vivre.Desktop;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Vivre.PerformanceAudit;

internal static class PatchingClarityProbe
{
    public static object Run()
    {
        using var first = WorkspaceProbe.CreateWorkspace();
        using var second = WorkspaceProbe.CreateWorkspace();
        first.IsUpdateMode = true;
        first.IncludeDrivers = true;
        first.ExcludeText = " SQL, ,Defender\n";
        var view = new WorkspaceView { DataContext = first };
        view.Measure(new Size(720, 600)); view.Arrange(new Rect(0, 0, 720, 600)); view.UpdateLayout();
        var summary = (TextBlock)view.FindName("PatchOptionsSummary");
        Require(summary.Text.Contains("Drivers: included") && summary.Text.Contains("Exclusions: 2"), "Options summary doesn't reflect parsed options.");
        second.IsUpdateMode = true;
        view.DataContext = second;
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        Require(summary.Text.Contains("Drivers: excluded") && summary.Text.Contains("Exclusions: 0"), "Summary leaked across tabs.");
        second.IsUpdateMode = false;
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        Require(summary.Visibility == Visibility.Collapsed, "Health shows patch-only options.");
        view.DataContext = null;

        var pending = new Computer("DUMMY-PENDING") { RebootRequired = true };
        var clean = new Computer("DUMMY-CLEAN") { RebootRequired = false };
        var approval = RebootApproval.Pending([pending, clean], true);
        var dialog = (Wpf.Ui.Controls.MessageBox)Call(typeof(MainWindow), "CreatePendingRebootDialog", approval, 2)!;
        try
        {
            Require(dialog.PrimaryButtonText == "Install anyway (2)" && dialog.SecondaryButtonText == "Force-reboot first (1)", "Reboot is primary or counts are misleading.");
            Require(dialog.Content.ToString()!.Contains("without rebooting") && dialog.Content.ToString()!.Contains("unsaved work will be lost"), "Choice consequences missing.");
        }
        finally { ((Window)dialog).Close(); }
        int installs = 0, reboots = 0;
        Func<Task> install = () => { installs++; return Task.CompletedTask; };
        Func<Task> reboot = () => { reboots++; return Task.CompletedTask; };
        foreach (var choice in new[] { Wpf.Ui.Controls.MessageBoxResult.Primary, Wpf.Ui.Controls.MessageBoxResult.Secondary, Wpf.Ui.Controls.MessageBoxResult.None })
        {
            ((Task)Call(typeof(MainWindow), "ExecutePendingRebootChoiceAsync", choice, install, reboot)!).GetAwaiter().GetResult();
            Require(installs == 1 && reboots == (choice == Wpf.Ui.Controls.MessageBoxResult.Primary ? 0 : 1), "Choice invoked the wrong operation or Cancel executed work.");
        }
        var scheduleScope = InstallTargetScope.Capture([pending, clean]);
        Require(scheduleScope.IsUnchanged(), "Unchanged schedule snapshot refused.");
        clean.Name = "DUMMY-RENAMED";
        Require(!scheduleScope.IsUnchanged(), "Schedule identity snapshot accepted a renamed target.");
        foreach (string action in new[] { "install", "reboot" })
        {
            var schedule = new ScheduleWindow(action, ["DUMMY-ONE", "DUMMY-TWO"]);
            try
            {
                Require(((TextBlock)schedule.FindName("Intro")).Text.Contains("Targets: 2"), "Target count missing from scheduling.");
                var button = (Wpf.Ui.Controls.Button)schedule.FindName("ScheduleButton");
                Require(button.Content.ToString()!.Contains("(2)"), "Schedule action count missing.");
                Require(action != "reboot" || !button.IsDefault, "Forced reboot became default Enter action.");
                Require(((TextBlock)schedule.FindName("TimeZoneSummary")).Text.Contains(TimeZoneInfo.Local.Id), "Actual host timezone not displayed.");
                ((DatePicker)schedule.FindName("DatePick")).SelectedDate = DateTime.Today.AddDays(-1);
                typeof(ScheduleWindow).GetMethod("OnSchedule", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(schedule, [schedule, new RoutedEventArgs()]);
                Require(schedule.Value is null && ((TextBlock)schedule.FindName("Hint")).Text.Contains("future"), "Past date accepted.");
            }
            finally { schedule.Close(); }
        }
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        string Describe(DateTime at) => (string)Call(typeof(ScheduleWindow), "DescribeTimeZone", at, eastern)!;
        Require(Describe(new DateTime(2027, 7, 1, 12, 0, 0)).Contains("UTC−04:00"), "Summer offset is wrong.");
        Require(Describe(new DateTime(2027, 1, 1, 12, 0, 0)).Contains("UTC−05:00"), "Winter offset is wrong.");
        Require(Describe(new DateTime(2026, 11, 1, 1, 30, 0)).Contains("occurs twice"), "Repeated-hour ambiguity not disclosed.");
        Require(Call(typeof(ScheduleWindow), "ScheduleTimeError", new DateTime(2027, 3, 14, 2, 30, 0), new DateTime(2027, 1, 1), eastern) is string, "Nonexistent DST time accepted.");
        return new { pass = true, perTabSummary = true, primaryInstallsSecondaryRebootsCancelDoesNeither = true,
            scheduleCountsAndHostZone = true, daylightSavingOffsetsAndGap = true, scope = "Production WPF controls and inert delegates; no live operations" };
    }
    private static object? Call(Type type, string name, params object[] args) => type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
