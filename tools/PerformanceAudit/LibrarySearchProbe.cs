using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Vivre.Desktop;
using Vivre.Core.Computers;

namespace Vivre.PerformanceAudit;

internal static class LibrarySearchProbe
{
    public static object Run()
    {
        var menu = new ContextMenu { Placement = PlacementMode.AbsolutePoint, HorizontalOffset = -20000, VerticalOffset = -20000 };
        int chosen = -1;
        var parent = new MenuItem { Header = "Lists" };
        typeof(MainWindow).GetMethod("AddListItems", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [parent, new[] { "Alpha", "Beta", "Beta" }, (Action<int, string>)((index, _) => chosen = index)]);
        menu.Items.Add(parent);
        try
        {
            menu.IsOpen = true;
            parent.IsSubmenuOpen = true;
            Pump();
            var host = (MenuItem)parent.Items[0];
            var search = (Wpf.Ui.Controls.TextBox)host.Header;
            search.Focus();
            Pump();
            if (!search.IsKeyboardFocused) throw new InvalidOperationException("List search cannot receive keyboard focus.");
            var input = new TextCompositionEventArgs(InputManager.Current.PrimaryKeyboardDevice,
                new TextComposition(InputManager.Current, search, "bEt")) { RoutedEvent = TextCompositionManager.TextInputEvent };
            search.RaiseEvent(input);
            Pump();
            if (search.Text != "bEt" || ((MenuItem)parent.Items[1]).Visibility != Visibility.Collapsed
                || ((MenuItem)parent.Items[2]).Visibility != Visibility.Visible || !menu.IsOpen || !parent.IsSubmenuOpen)
                throw new InvalidOperationException("Routed text input failed to filter lists or closed the submenu.");
            var clear = Descendants(search).OfType<Button>().FirstOrDefault(b => b.Name.Contains("Clear", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Search clear button not found.");
            ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(clear)
                .GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
            Pump();
            if (search.Text.Length != 0 || ((MenuItem)parent.Items[1]).Visibility != Visibility.Visible || !menu.IsOpen || !parent.IsSubmenuOpen)
                throw new InvalidOperationException("Clear button failed to restore lists or closed submenu.");
            search.Text = "missing";
            if (((MenuItem)parent.Items[^1]).Visibility != Visibility.Visible) throw new InvalidOperationException("No-match feedback missing.");
            search.Clear();
            ((MenuItem)parent.Items[3]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            if (chosen != 2) throw new InvalidOperationException("Duplicate labels selected the wrong recovery record.");
            VerifyOrganizedLists();
            return new { routedTyping = true, keyboardFocus = true, clearButton = true, noMatches = true, exactDuplicateRecord = true,
                organizedLists = true,
                scope = "Production menu helper; offscreen native popup and routed WPF input, not physical mouse/keyboard or DPI testing" };
        }
        finally { menu.IsOpen = false; }
    }

    private static void VerifyOrganizedLists()
    {
        var parent = new MenuItem { Header = "Open list" };
        var folders = new ListFolderSnapshot(["October Updates"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Monthly"] = "October Updates" });
        string? opened = null;
        typeof(MainWindow).GetMethod("AddOrganizedListItems", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [parent, new[] { "Current", "Monthly" }, folders, (Action<string>)(name => opened = name)]);
        if (parent.Items.Count != 4 || ((MenuItem)parent.Items[1]).Header.ToString() != "Current"
            || ((MenuItem)parent.Items[2]).Header.ToString() != "October Updates")
            throw new InvalidOperationException("Top-level lists or folder grouping changed unexpectedly.");
        var search = (Wpf.Ui.Controls.TextBox)((MenuItem)parent.Items[0]).Header;
        var root = (MenuItem)parent.Items[1];
        var folder = (MenuItem)parent.Items[2];
        var child = (MenuItem)folder.Items[0];
        search.Text = "monthly";
        if (root.Visibility != Visibility.Collapsed || folder.Visibility != Visibility.Visible || child.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Search failed to find a list inside its folder.");
        search.Text = "october";
        if (folder.Visibility != Visibility.Visible || child.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Folder search did not expose its lists.");
        child.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        if (opened != "Monthly") throw new InvalidOperationException("Folder list opened the wrong saved list.");

        // Flat organization and move destinations are covered by ListOrganizationProbe using
        // the production window/store; the retired cascading submenu no longer exists.

    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
}
