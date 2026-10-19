using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Serilog;
using Vivre.Core.Configuration;
using Vivre.Core.Logging;
using Vivre.Desktop;
using Vivre.Desktop.ViewModels;

namespace Vivre.PerformanceAudit;

internal static class SettingsClarityProbe
{
    public static object Run(string root)
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        var log = (ActivityLog)Activator.CreateInstance(typeof(ActivityLog), BindingFlags.NonPublic | BindingFlags.Instance, null, [logger], null)!;
        string title = "Health · Original #1";
        var scoped = new WorkspaceActivityLog(log, () => title);
        scoped.Info("DUMMY", "Unchanged message");
        title = "Health · Renamed #1";
        scoped.Warn("DUMMY", "Second message");
        new WorkspaceActivityLog(log, () => "Patching · Other #2").Error("DUMMY", "Other tab");
        log.Info(null, "App event");
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Require(log.Entries.Single(e => e.Message == "Unchanged message").Origin == "Health · Original #1", "Historical origin changed after rename.");
        Require(log.Entries.Single(e => e.Message == "App event").Origin is null, "App event assigned a false workspace.");
        string copied = (string)typeof(MainWindow).GetMethod("FormatActivityEntries", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [log.Entries])!;
        Require(copied.Contains("\tHealth · Original #1\tUnchanged message") && copied.Contains("\t\t\tApp event"), "Copy lost origin or untagged column alignment.");
        var activity = new ActivityLogViewModel(log) { SearchText = "Other #2" };
        Require(activity.Entries.Cast<LogEntry>().Count() == 1, "Origin search failed.");
        using var workspace = WorkspaceProbe.CreateWorkspace();
        Require(workspace.Activity is WorkspaceActivityLog, "Workspace does not scope untagged activity.");
        var main = new MainWindow();
        try
        {
            var grid = (DataGrid)main.FindName("ActivityGrid");
            Require(grid.Columns.OfType<DataGridTextColumn>().Any(c => Equals(c.Header, "Tab") && ((System.Windows.Data.Binding)c.Binding).Path.Path == "Origin"), "Activity grid does not display origin.");
        }
        finally { main.Close(); }

        string sharedRoot = Path.Combine(root, "shared");
        var shared = new SharedSettingsStore(sharedRoot);
        shared.Update(s => { s.MonthlyCu = new MonthlyCu { Kb = "KB1111111", TargetUbr = 100, MonthTag = "Old", Arch = "x64" }; s.WugServer = "DUMMY-WUG"; });
        var personal = (AppSettingsStore)Activator.CreateInstance(typeof(AppSettingsStore), BindingFlags.NonPublic | BindingFlags.Instance, null, [Path.Combine(root, "personal.json"), null], null)!;
        var page = new SettingsPage();
        Set(page, "_sharedStore", shared); Set(page, "_settingsStore", personal); Set(page, "_log", log);
        Call(page, "LoadCuDraft", shared.Load().MonthlyCu);
        Box(page, "LcuKbBox").Text = "2222222";
        Box(page, "LcuUbrBox").Text = "bad";
        Box(page, "LcuMonthTagBox").Text = "New";
        Require(shared.Load().MonthlyCu.Kb == "KB1111111", "Typing partially saved CU settings.");
        Call(page, "OnSaveCuSettings", page, new RoutedEventArgs());
        Require(shared.Load().MonthlyCu.TargetUbr == 100 && Box(page, "LcuUbrBox").Text == "bad" && Status(page).Contains("Nothing saved"), "Invalid CU mutated store or hid input/error.");
        Box(page, "LcuUbrBox").Text = "200";
        Call(page, "OnSaveCuSettings", page, new RoutedEventArgs());
        var saved = shared.Load();
        Require(saved.MonthlyCu.Kb == "KB2222222" && saved.MonthlyCu.TargetUbr == 200 && saved.MonthlyCu.MonthTag == "New" && saved.WugServer == "DUMMY-WUG", "Grouped save lost values or unrelated setting.");
        Require(Status(page) == "CU settings saved.", "Successful CU save not shown.");
        Box(page, "LcuUbrBox").Text = "300";
        shared.Update(s => s.MonthlyCu.MonthTag = "Other operator");
        Call(page, "OnSaveCuSettings", page, new RoutedEventArgs());
        Require(shared.Load().MonthlyCu.TargetUbr == 200 && Status(page).Contains("changed elsewhere"), "Stale CU draft overwrote another operator.");
        Call(page, "OnReloadCuSettings", page, new RoutedEventArgs());
        Require(Box(page, "LcuMonthTagBox").Text == "Other operator" && Box(page, "LcuUbrBox").Text == "200", "Reload did not recover from conflict.");
        var expected = shared.Load().MonthlyCu;
        var package = new MonthlyCu { Kb = "KB3333333", TargetUbr = 333, MonthTag = "Package", Arch = "x64" };
        Require((bool)Call(page, "TrySaveCu", package, expected)!, "Package shared save failed.");
        Require(Box(page, "LcuKbBox").Text == "KB3333333" && shared.Load().MonthlyCu.TargetUbr == 333, "Package save did not synchronize draft baseline.");
        Box(page, "LcuMonthTagBox").Text = "Manual after package";
        Call(page, "OnSaveCuSettings", page, new RoutedEventArgs());
        Require(shared.Load().MonthlyCu.MonthTag == "Manual after package", "Package left typed baseline stale.");
        foreach (var item in new[] { ("MaxInstallsBox", "MaxInstallsError", "OnMaxInstallsChanged", "51"), ("WugStateConcurrencyBox", "WugStateConcurrencyError", "OnWugStateConcurrencyChanged", "3"), ("ServicingWaitBox", "ServicingWaitError", "OnServicingWaitChanged", "21") })
        {
            page.Measure(new Size(900, 900)); page.Arrange(new Rect(0, 0, 900, 900)); page.UpdateLayout();
            double beforeWidth = Box(page, item.Item1).ActualWidth;
            Box(page, item.Item1).Text = "bad";
            Call(page, item.Item3, page, new RoutedEventArgs());
            page.UpdateLayout();
            if (item.Item1 != "WugStateConcurrencyBox") Require(Math.Abs(Box(page, item.Item1).ActualWidth - beforeWidth) < 1, "Error widened numeric field.");
            Require(Box(page, item.Item1).Text == "bad" && ((TextBlock)page.FindName(item.Item2)).Text.Contains("previous setting"), "Numeric error silently reverted input.");
            Box(page, item.Item1).Text = item.Item4;
            Call(page, item.Item3, page, new RoutedEventArgs());
            Require(((TextBlock)page.FindName(item.Item2)).Text == "", "Correcting value left stale error.");
        }
        Require(personal.Load().MaxSimultaneousInstalls == 51 && personal.Load().WugStateConcurrency == 3 && personal.Load().ServicingWaitMinutes == 21, "Valid numeric inputs not applied.");
        personal.Flush(TimeSpan.FromSeconds(10));
        File.WriteAllText(Path.Combine(sharedRoot, "settings.json"), "broken JSON");
        string draft = Box(page, "LcuKbBox").Text;
        Call(page, "OnReloadCuSettings", page, new RoutedEventArgs());
        Require(Box(page, "LcuKbBox").Text == draft && Status(page).StartsWith("Could not reload"), "Failed reload discarded draft or claimed success.");
        Call(page, "OnSaveCuSettings", page, new RoutedEventArgs());
        Require(Status(page).StartsWith("Nothing saved") && File.ReadAllText(Path.Combine(sharedRoot, "settings.json")) == "broken JSON", "Failed write claimed success or replaced unread settings.");
        object logging = LogProbe.Run();
        return new { pass = true, logging, originVisibleSearchableAndCaptured = true, atomicCuSaveAndConflict = true, packageAndTypedFlow = true, inlineNumericValidation = true, failedSavePreservesDraftAndFile = true, scope = "Production controls and real temporary stores; no live targets or production writes" };
    }
    private static string Status(SettingsPage page) => ((TextBlock)page.FindName("CuSettingsStatus")).Text;
    private static Wpf.Ui.Controls.TextBox Box(SettingsPage page, string name) => (Wpf.Ui.Controls.TextBox)page.FindName(name);
    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Set(object obj, string name, object value) => obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(obj, value);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
