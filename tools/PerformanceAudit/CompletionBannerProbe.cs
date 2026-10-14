using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vivre.Desktop;
using Vivre.Desktop.ViewModels;
using Wpf.Ui.Controls;

namespace Vivre.PerformanceAudit;
internal static class CompletionBannerProbe
{
    public static object Run()
    {
        var window = new MainWindow(); // no data context, services, settings, or Window.Show
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var enqueue = typeof(MainWindow).GetMethod("EnqueueCompletionBar", flags)!;
        var tick = typeof(MainWindow).GetMethod("OnCompletionBarTick", flags)!;
        var closed = typeof(MainWindow).GetMethod("OnWindowClosed", flags)!;
        var bar = (InfoBar)window.FindName("CompletionBar");
        try
        {
            for (int i = 0; i < 30; i++) enqueue.Invoke(window, [$"Dummy completion {i}", i == 1 ? OperationSeverity.Error : OperationSeverity.Success]);
            if (bar.Message != "Dummy completion 0" || !bar.IsOpen) throw new InvalidOperationException("First banner missing");
            tick.Invoke(window, [null, EventArgs.Empty]);
            if (!bar.Message.Contains("25 earlier") || bar.Severity != InfoBarSeverity.Error) throw new InvalidOperationException("Overflow summary or severity lost");
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1280, 720)); content.Arrange(new Rect(0, 0, 1280, 720)); content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            for (int i = 26; i < 30; i++)
            {
                tick.Invoke(window, [null, EventArgs.Empty]);
                if (bar.Message != $"Dummy completion {i}" || !bar.IsOpen) throw new InvalidOperationException("Recent banner ordering wrong");
            }
            tick.Invoke(window, [null, EventArgs.Empty]);
            if (bar.IsOpen) throw new InvalidOperationException("Banner did not dismiss");
            return new { completions = 30, omitted = 25, recentRetained = 4, summaryPreservesErrorSeverity = true,
                realControlRenderedOffscreen = true, drainsAndDismisses = true,
                limitation = "No visible window, native tray interaction, production completion event or server operations exercised." };
        }
        finally { closed.Invoke(window, [null, EventArgs.Empty]); }
    }
}
