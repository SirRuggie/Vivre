using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vivre.Core.Models;
using Vivre.Desktop;
using Vivre.Desktop.ViewModels;

namespace Vivre.PerformanceAudit;

/// <summary>
/// Offscreen rendered measurement of the production WorkspaceView and its virtualizing DataGrid.
/// Uses WorkspaceProbe's inert workspace factory: no Vivre.App startup, window, remote dependency,
/// AddComputers path, settings save, or production operation is involved.
/// </summary>
internal static class RenderedWorkspaceProbe
{
    private static readonly int[] RowCounts = [319, 3_190];
    private static readonly Size Viewport = new(1280, 720);

    public static object Run()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("RenderedWorkspaceProbe.Run must execute on the STA thread.");

        Application app = Application.Current
            ?? throw new InvalidOperationException("Create a plain WPF Application before running the rendered probe.");
        EnsureApplicationResources(app);

        var measurements = new List<object>(RowCounts.Length);
        foreach (int rowCount in RowCounts)
        {
            using WorkspaceViewModel workspace = CreateInertWorkspace();
            for (int index = 0; index < rowCount; index++)
                workspace.Computers.Add(new Computer($"MACHINE-{index:D5}"));

            var view = new WorkspaceView { DataContext = workspace };
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            DataGrid grid = view.FindName("ComputerGrid") as DataGrid
                ?? throw new InvalidOperationException("WorkspaceView.ComputerGrid was not found.");

            RenderResult initial = Render(view, grid);
            Require(grid.Items.Count == rowCount, $"Grid received {grid.Items.Count:N0} of {rowCount:N0} rows.");
            Require(initial.RealizedRows > 0 && initial.RealizedRows < rowCount,
                $"Expected a virtualized subset for {rowCount:N0} rows; realized {initial.RealizedRows:N0}.");

            string[] filters = ["M", "MA", "MAC", "MACH", "MACHI", "MACHIN", "MACHINE", "MACHINE-", "MACHINE-0", "MACHINE-00"];
            var rapid = Stopwatch.StartNew();
            foreach (string filter in filters)
                workspace.FilterText = filter;
            rapid.Stop();

            var settle = Stopwatch.StartNew();
            bool settled = PumpUntil(
                () => settle.Elapsed >= TimeSpan.FromMilliseconds(300)
                    && workspace.VisibleRowCount == ExpectedRows(rowCount),
                TimeSpan.FromSeconds(10));
            Require(settled, $"Filter did not settle for {rowCount:N0} rows.");
            RenderResult filtered = Render(view, grid);
            settle.Stop();

            Require(grid.Items.Count == ExpectedRows(rowCount),
                $"Rendered grid filter count was {grid.Items.Count:N0}; expected {ExpectedRows(rowCount):N0}.");
            Require(filtered.NonTransparentPixels > 0, "Offscreen render produced no visible pixels.");

            measurements.Add(new
            {
                requestedRows = rowCount,
                viewport = new { width = Viewport.Width, height = Viewport.Height },
                baselineComparison = "Unavailable: this probe does not load or mutate an old WorkspaceViewModel implementation.",
                initialRender = initial,
                rapidFilterSetterMilliseconds = rapid.Elapsed.TotalMilliseconds,
                settledFilterAndRenderMilliseconds = settle.Elapsed.TotalMilliseconds,
                settledVisibleRows = workspace.VisibleRowCount,
                filteredRender = filtered,
            });

            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            view.DataContext = null;
        }

        return new
        {
            probe = nameof(RenderedWorkspaceProbe),
            rendered = true,
            measurements,
            limitations = new[]
            {
                "Rendering is offscreen at a fixed 1280x720 viewport; no visible Window, composition-frame timing, input, or GPU presentation is measured.",
                "The production WorkspaceView, bindings, DataGrid templates, virtualization, measure, arrange, layout and RenderTargetBitmap render paths are exercised.",
                "The inert WorkspaceViewModel factory may read existing per-user settings during construction, as the existing WorkspaceProbe documents; this probe performs no saves.",
                "Stopwatch samples are diagnostic measurements, not release guarantees. Use repeated runs and medians.",
            },
        };
    }

    private static RenderResult Render(WorkspaceView view, DataGrid grid)
    {
        var watch = Stopwatch.StartNew();
        view.Measure(Viewport);
        view.Arrange(new Rect(Viewport));
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            (int)Viewport.Width, (int)Viewport.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        watch.Stop();

        int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        int nonTransparent = 0;
        for (int offset = 3; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset] != 0)
                nonTransparent++;
        }

        int realized = 0;
        for (int index = 0; index < grid.Items.Count; index++)
        {
            if (grid.ItemContainerGenerator.ContainerFromIndex(index) is DataGridRow)
                realized++;
        }

        return new RenderResult(
            watch.Elapsed.TotalMilliseconds,
            grid.Items.Count,
            realized,
            grid.ActualWidth,
            grid.ActualHeight,
            nonTransparent);
    }

    private static WorkspaceViewModel CreateInertWorkspace()
    {
        MethodInfo factory = typeof(WorkspaceProbe).GetMethod(
            "CreateWorkspace", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(WorkspaceProbe).FullName, "CreateWorkspace");
        return (WorkspaceViewModel)(factory.Invoke(null, null)
            ?? throw new InvalidOperationException("WorkspaceProbe returned no workspace."));
    }

    private static void EnsureApplicationResources(Application app)
    {
        if (!app.Resources.MergedDictionaries.Any(d => d.GetType().Name == "ThemesDictionary"))
            app.Resources.MergedDictionaries.Add(CreateDictionary("Wpf.Ui.Markup.ThemesDictionary", theme: "Dark"));
        if (!app.Resources.MergedDictionaries.Any(d => d.GetType().Name == "ControlsDictionary"))
            app.Resources.MergedDictionaries.Add(CreateDictionary("Wpf.Ui.Markup.ControlsDictionary"));

        AddTextStyle(app, "EmptyStateTitle", 14);
        AddTextStyle(app, "EmptyStateSubtitle", 12);
    }

    private static ResourceDictionary CreateDictionary(string typeName, string? theme = null)
    {
        Type type = Type.GetType(typeName + ", Wpf.Ui", throwOnError: true)!;
        var dictionary = (ResourceDictionary)(Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"Could not create {typeName}."));
        if (theme is not null)
        {
            PropertyInfo property = type.GetProperty("Theme")
                ?? throw new MissingMemberException(typeName, "Theme");
            property.SetValue(dictionary, Enum.Parse(property.PropertyType, theme));
        }
        return dictionary;
    }

    private static void AddTextStyle(Application app, string key, double fontSize)
    {
        if (app.Resources.Contains(key)) return;
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.FontSizeProperty, fontSize));
        app.Resources[key] = style;
    }

    private static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        if (condition()) return true;
        var frame = new DispatcherFrame();
        var poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        var stop = new DispatcherTimer(DispatcherPriority.Background) { Interval = timeout };
        poll.Tick += OnPoll;
        stop.Tick += OnStop;
        poll.Start();
        stop.Start();
        Dispatcher.PushFrame(frame);
        poll.Stop();
        stop.Stop();
        poll.Tick -= OnPoll;
        stop.Tick -= OnStop;
        return condition();

        void OnPoll(object? sender, EventArgs e)
        {
            if (condition()) frame.Continue = false;
        }

        void OnStop(object? sender, EventArgs e) => frame.Continue = false;
    }

    private static int ExpectedRows(int rowCount) => Math.Min(rowCount, 1_000);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record RenderResult(
        double RenderMilliseconds,
        int GridItems,
        int RealizedRows,
        double GridWidth,
        double GridHeight,
        int NonTransparentPixels);
}
