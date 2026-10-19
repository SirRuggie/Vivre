using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Vivre.Desktop;

namespace Vivre.PerformanceAudit;

internal static class GridPreferencesProbe
{
    public static object Run(string root)
    {
        string path = Path.Combine(root, "grid-preferences.json");
        int writes = 0;
        Action<string, string> write = (p, text) => { File.WriteAllText(p, text); Interlocked.Increment(ref writes); };
        var store = (AppSettingsStore)Activator.CreateInstance(typeof(AppSettingsStore), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [path, write], null)!;
        DataGrid Grid() => new() { Columns = {
            new DataGridTextColumn { Header = "Name", Width = 180, CanUserReorder = false },
            new DataGridTextColumn { Header = "Result", Width = 120 },
            new DataGridTextColumn { Header = "Status", Width = DataGridLength.Auto } } };
        var grid = Grid(); var layout = new GridLayoutPersistence(grid, "Health", store);
        grid.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        grid.Columns[1].Width = 275;
        grid.Columns[2].DisplayIndex = 1;
        grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
        store.Flush(TimeSpan.FromSeconds(5));
        Require(writes == 1, "User adjustment did not save exactly once.");
        for (int i = 0; i < 10; i++) layout.SaveUserLayout();
        store.Flush(TimeSpan.FromSeconds(5));
        Require(writes == 1, "Unchanged interactions wrote settings repeatedly.");
        string freshPath = Path.Combine(root, "fresh-process-preferences.json");
        File.Copy(path, freshPath);
        var freshStore = (AppSettingsStore)Activator.CreateInstance(typeof(AppSettingsStore), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [freshPath, null], null)!;
        var diskGrid = Grid(); new GridLayoutPersistence(diskGrid, "Health", freshStore).Restore();
        Require(diskGrid.Columns[1].Width.Value == 275 && diskGrid.Columns[2].DisplayIndex == 1, "Saved JSON did not round-trip through a fresh store.");
        var reopened = Grid(); var reopenedLayout = new GridLayoutPersistence(reopened, "Health", store);
        reopened.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Require(reopened.Columns[1].Width.Value == 275 && reopened.Columns[2].DisplayIndex == 1 && reopened.Columns[0].DisplayIndex == 0, "Layout not restored or identity moved.");
        var patch = Grid(); var patchLayout = new GridLayoutPersistence(patch, "Patching", store); patchLayout.Restore();
        Require(patch.Columns[1].Width.Value == 120 && patch.Columns[2].DisplayIndex == 2, "Health leaked into Patching.");
        grid.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        grid.Columns[1].Width = 390;
        grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
        store.Flush(TimeSpan.FromSeconds(5));
        Require(writes == 1, "Unloaded grid retained input handlers.");
        reopenedLayout.Reset(); store.Flush(TimeSpan.FromSeconds(5));
        Require(reopened.Columns[1].Width.Value == 120 && reopened.Columns[2].DisplayIndex == 2 && reopened.Columns[2].Width.IsAuto, "Reset lost original widths or order.");
        var saved = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))!;
        Require(!saved.GridLayouts.ContainsKey("Health"), "Reset not persisted.");
        store.Load().GridLayouts["Health"] = [new(null!, 100, (int)DataGridLengthUnitType.Pixel, 0), new("Name", -10, (int)DataGridLengthUnitType.Pixel, 2), new("Status", 260, (int)DataGridLengthUnitType.Pixel, 0)];
        reopenedLayout.Restore();
        Require(reopened.Columns[0].DisplayIndex == 0 && reopened.Columns[0].Width.Value == 180, "Malformed preferences moved/collapsed identity.");
        Require(reopened.Columns[2].Width.Value == 260, "Bad key prevented valid preference restore.");
        reopened.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));

        using var workspace = WorkspaceProbe.CreateWorkspace();
        var dialog = new SoftwareCheckWindow(workspace, [], store);
        try
        {
            var query = (Wpf.Ui.Controls.TextBox)dialog.FindName("QueryBox");
            var service = (Wpf.Ui.Controls.TextBox)dialog.FindName("ServiceBox");
            var check = (CheckBox)dialog.FindName("ServiceCheck");
            query.Text = "CrowdStrike";
            Require(check.IsChecked == true && service.Text == "CSFalconService", "Known product not suggested.");
            service.Text = "custom-service"; query.Text = " crowdstrike ";
            Require(service.Text == "custom-service", "Case/space-only edit erased custom service.");
            query.Text = "Different Product";
            Require(check.IsChecked == false && service.Text == "", "Previous product service leaked.");
            check.IsChecked = true;
            Require(service.Text == "Different Product", "Manual service opt-in broken.");
            query.Text = "SentinelOne";
            Require(check.IsChecked == true && service.Text == "SentinelAgent", "Known-to-known suggestion wrong.");
            query.Clear();
            Require(check.IsChecked == false && service.Text == "", "Empty query retained service.");
        }
        finally { dialog.Close(); }
        return new { pass = true, serviceChanges = true, perGridLayoutAndReset = true, noRedundantWrites = true,
            detachedHandlers = true, pinnedIdentity = true, scope = "Real WPF controls and temporary settings; no live checks or production preference writes" };
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
