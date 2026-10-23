using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vivre.Core.Models;
using Vivre.Desktop;
using Vivre.Desktop.ViewModels;

namespace Vivre.PerformanceAudit;

internal static class LayoutAccessibilityProbe
{
    public static object Run(string outputDirectory, bool dark = true)
    {
        Directory.CreateDirectory(outputDirectory);
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light);
        using var vm = WorkspaceProbe.CreateWorkspace();
        vm.Computers.Add(new Computer("DUMMY-LAYOUT") { OsBuild = 14393, IsOnline = true, RebootRequired = false });
        var view = new WorkspaceView { DataContext = vm };
        var cases = new List<object>();
        foreach (bool patching in new[] { false, true })
        foreach (double width in new[] { 560d, 800d, 1100d })
        foreach (double scale in new[] { 1d, 1.25d, 1.5d, 2d })
        {
            vm.IsUpdateMode = patching;
            Layout(view, width, 420);
            var chips = Descendants(view).OfType<ToggleButton>().Where(b => b.Tag is RowFilter && Visible(b)).ToArray();
            Require(chips.Length >= 6, "Missing filter controls.");
            foreach (var chip in chips)
            {
                Rect bounds = chip.TransformToAncestor(view).TransformBounds(new Rect(chip.RenderSize));
                Require(bounds.Left >= 0 && bounds.Right <= width + 1, $"Filter clipped at {width}: {chip.Tag} {bounds}");
                Require(!string.IsNullOrWhiteSpace(PeerName(chip)), "Unnamed filter chip: " + chip.Tag);
            }
            var grid = (DataGrid)view.FindName(patching ? "UpdateGrid" : "ComputerGrid");
            Require(grid.ActualHeight >= 100, "Filters consumed the usable grid height.");
            var search = (Wpf.Ui.Controls.TextBox)view.FindName("MachineFilterBox");
            Require(PeerName(search) == "Filter machines by name", "Machine search has no accessible name.");
            Render(view, width, 420, scale, Path.Combine(outputDirectory, $"{(patching ? "patching" : "health")}-{width}-{scale}.png"));
            cases.Add(new { patching, width, rasterScale = scale, visibleChips = chips.Length, gridHeight = grid.ActualHeight });
        }
        // Verify icon-cell labels independently of visible-column virtualization.
        var update = (DataGrid)view.FindName("UpdateGrid");
        var online = update.Columns.Single(c => c.SortMemberPath == "IsOnline");
        var cell = new DataGridCell { DataContext = vm.Computers[0], Style = online.CellStyle };
        Require(AutomationProperties.GetName(cell) == "Online: Yes", "Online status label missing.");
        vm.Computers[0].IsOnline = null;
        Pump();
        Require(AutomationProperties.GetName(cell) == "Online: Unknown", "Unknown status is not represented honestly.");
        var reboot = update.Columns.Single(c => c.SortMemberPath == "RebootRequired");
        cell = new DataGridCell { DataContext = vm.Computers[0], Style = reboot.CellStyle };
        Require(AutomationProperties.GetName(cell) == "Reboot pending: No", "Reboot status label missing.");

        var prompt = new TextPromptWindow("Save list", "Save this tab's machines as a list named:", "Dummy");
        var paste = new LoadComputersWindow(vm);
        var exclude = new ExcludeUpdatesWindow();
        var schedule = new ScheduleWindow("install", ["DUMMY-LAYOUT"]);
        var package = new LcuPackageReadDialog(new("KB1234567", "14393.123456", "x64", "KB2345678", "14393.234567", "x64",
            "Dummy package identity for layout", "dummy-long-package-name.msu", "2026-10-19", "October 2026", "November 2026", false));
        var windows = new Window[] { prompt, paste, exclude, schedule, package };
        try
        {
            foreach (var dialog in windows)
            {
                foreach (double width in new[] { dialog.MinWidth, Math.Max(dialog.MinWidth, double.IsNaN(dialog.Width) ? 400 : dialog.Width) }.Distinct())
                {
                var content = (FrameworkElement)dialog.Content;
                Layout(content, width, 400);
                foreach (var button in Descendants(content).OfType<Button>().Where(b => Visible(b) && b.IsEnabled))
                {
                    Rect bounds = button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));
                    Require(bounds.Left >= -1 && bounds.Right <= width + 1, dialog.Title + " action button clipped.");
                }
                foreach (double scale in new[] { 1d, 1.25d, 1.5d, 2d })
                    Render(content, width, 400, scale, Path.Combine(outputDirectory, dialog.GetType().Name + "-" + width + "-" + scale + ".png"));
                }
            }
            Require(PeerName((UIElement)prompt.FindName("InputBox")) == "Save this tab's machines as a list named:", "Prompt input not labeled by its question.");
            Require(PeerName((UIElement)paste.FindName("NamesBox")) == "Machine names", "Paste input unnamed.");
            Require(AutomationProperties.GetLiveSetting((DependencyObject)prompt.FindName("ErrorText")) == AutomationLiveSetting.Polite, "Prompt errors not announced.");
            VerifyKeyboard(view, vm, prompt);
            VerifyTabKeyboard(vm);
            VerifySettingsAndScripts(outputDirectory);
            VerifyDialogKeyConfiguration();
        }
        finally { foreach (var dialog in windows) dialog.Close(); }
        view.DataContext = null;
        return new { pass = true, theme = dark ? "Dark" : "Light", workspaceCases = cases, dialogs = windows.Select(w => w.GetType().Name).ToArray(),
            keyboardFocusAndTraversal = true, automationNamesAndStates = true,
            scope = "Offscreen WPF layout at 560/800/1100 DIPs and raster renders at 96/120/144/192 DPI; offscreen WPF focus traversal and Automation peers. Not actual Windows DPI changes, physical-input/Enter/Escape testing, or spoken screen-reader verification." };
    }

    private static void VerifyKeyboard(WorkspaceView view, WorkspaceViewModel vm, TextPromptWindow prompt)
    {
        var host = new Window { Content = view, Width = 800, Height = 500, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            host.Show();
            vm.IsUpdateMode = true;
            Pump();
            var search = (Wpf.Ui.Controls.TextBox)view.FindName("MachineFilterBox");
            search.Focus();
            Require(search.IsKeyboardFocused, "Search cannot receive keyboard focus.");
            bool reachedChip = false;
            for (int i = 0; i < 8; i++)
            {
                ((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                if (Keyboard.FocusedElement is ToggleButton { Tag: RowFilter }) { reachedChip = true; break; }
            }
            Require(reachedChip, "Tab traversal cannot reach filters.");
            var chip = (ToggleButton)Keyboard.FocusedElement;
            var ring = (Border)chip.Template.FindName("FocusRing", chip);
            var border = (Border)chip.Template.FindName("Chip", chip);
            Require(ring.BorderBrush is SolidColorBrush focus && focus.Color.A > 0
                && border.Background is SolidColorBrush fill && focus.Color != fill.Color, "Checked filter focus ring is not visually distinct.");
            var before = vm.ActiveFilter;
            Require(before == RowFilter.All, "Focusing a chip executed its filter action.");
            host.Content = null;
        }
        finally { host.Close(); }
    }
    private static void VerifyTabKeyboard(WorkspaceViewModel vm)
    {
        var main = new MainWindow();
        var original = (TabControl)main.FindName("HealthTabs");
        var tabs = new TabControl { ItemTemplate = original.ItemTemplate, ItemContainerStyle = (Style)Application.Current.FindResource("FluentTabItemStyle") };
        tabs.Items.Add(vm);
        var host = new Window { Content = tabs, Width = 800, Height = 400, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            host.Show(); Pump();
            var item = (TabItem)tabs.ItemContainerGenerator.ContainerFromItem(vm);
            item.Focus();
            Require(item.IsKeyboardFocused, "Tab header is not keyboard-focusable.");
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(item), 0, Key.Apps) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            typeof(MainWindow).GetMethod("OnTabContextMenuKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [tabs, args]);
            Pump();
            var header = Descendants(item).OfType<StackPanel>().Single(p => p.ContextMenu is not null);
            var menu = header.ContextMenu!;
            Require(args.Handled && menu.IsOpen && ReferenceEquals(menu.DataContext, vm), "Keyboard tab menu failed or targeted another tab.");
            Require(menu.Items.OfType<MenuItem>().Any(i => Equals(i.Header, "Open tab in Patching")), "Keyboard menu omits handoff action.");
            menu.IsOpen = false;
            var close = Descendants(item).OfType<Button>().Single(b => AutomationProperties.GetName(b).StartsWith("Close tab "));
            close.Focus(); Pump();
            Require(((Border)close.Template.FindName("Bd", close)).BorderThickness.Left == 2, "Tab close has no focus ring.");
            Require(PeerName(close).Contains(vm.Title), "Tab close accessible name lacks tab title.");
        }
        finally { host.Content = null; host.Close(); main.Close(); }
    }

    private static void VerifySettingsAndScripts(string outputDirectory)
    {
        var settings = new SettingsPage();
        foreach (var element in new[] { "ThemeCombo", "AutoToggle", "UserNameBox", "PasswordBox", "WugServerBox", "WugStateConcurrencyBox", "PackagesFolderBox", "MaxInstallsBox", "ServicingWaitBox", "LcuKbBox", "LcuUbrBox", "LcuMonthTagBox" })
            Require(!string.IsNullOrWhiteSpace(AutomationProperties.GetName((DependencyObject)settings.FindName(element))), "Unnamed settings field: " + element);
        foreach (var card in Descendants(settings).OfType<Wpf.Ui.Controls.CardExpander>()) card.IsExpanded = true;
        foreach (double width in new[] { 560d, 800d })
        {
            Layout(settings, width, 420);
            Render(settings, width, 420, 1, Path.Combine(outputDirectory, "Settings-" + width + ".png"));
        }
        var scripts = new ScriptsPage();
        try
        {
            foreach (double width in new[] { 560d, 800d })
            {
                Layout(scripts, width, 420);
                foreach (var button in Descendants(scripts).OfType<Button>().Where(b => Visible(b) && b.Content is string))
                {
                    Rect bounds = button.TransformToAncestor(scripts).TransformBounds(new Rect(button.RenderSize));
                    Require(bounds.Right <= width + 1, "Scripts action clipped: " + button.Content);
                }
                Render(scripts, width, 420, 1, Path.Combine(outputDirectory, "Scripts-" + width + ".png"));
            }
        }
        finally { scripts.Dispose(); }
    }

    private static void VerifyDialogKeyConfiguration()
    {
        var dialog = new TextPromptWindow("Dummy name", "Name:", "DUMMY");
        try
        {
            Require(((Button)dialog.FindName("AcceptButton")).IsDefault, "Name dialog lost default-button wiring.");
            Layout((FrameworkElement)dialog.Content, 400, 260);
            Require(Descendants((FrameworkElement)dialog.Content).OfType<Button>().Any(b => b.IsCancel), "Name dialog lost cancel-button wiring.");
        }
        finally { dialog.Close(); }
    }

    private static bool Visible(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement ui && ui.Visibility != Visibility.Visible) return false;
        return true;
    }
    private static string PeerName(UIElement element) => UIElementAutomationPeer.CreatePeerForElement(element)?.GetName() ?? "";
    internal static void Layout(FrameworkElement view, double width, double height)
    {
        System.Windows.Documents.TextElement.SetForeground(view, (Brush)Application.Current.FindResource("TextFillColorPrimaryBrush"));
        Pump();
        view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout(); Pump();
        view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    internal static void Render(FrameworkElement view, double width, double height, double scale, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle((Brush)Application.Current.FindResource("ApplicationBackgroundBrush"), null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(view) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
        }
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
