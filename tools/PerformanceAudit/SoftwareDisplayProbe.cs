using System.Windows;
using System.Windows.Controls;
using Vivre.Core.Models;
using Vivre.Desktop;

namespace Vivre.PerformanceAudit;

internal static class SoftwareDisplayProbe
{
    public static object Run()
    {
        using var workspace = WorkspaceProbe.CreateWorkspace();
        var row = new Computer("DUMMY-SOFTWARE");
        string warning = string.Join("; ", Enumerable.Repeat("Dummy inventory entry unreadable", 200));
        SoftwareCheckState.Complete(row, "Dummy", new(true, "Dummy Agent", "1", "Running", warning), 0);
        workspace.Computers.Add(row);
        var view = new WorkspaceView { DataContext = workspace };
        var grid = (DataGrid)view.FindName("ComputerGrid");
        var column = grid.Columns.Single(c => Equals(c.Header, "Software"));
        if (!grid.CanUserResizeColumns || !column.CanUserResize || column.MaxWidth < 720)
            throw new InvalidOperationException("Software column still prevents widening.");
        column.Width = new DataGridLength(720);
        view.Measure(new Size(1600, 800));
        view.Arrange(new Rect(0, 0, 1600, 800));
        view.UpdateLayout();
        if (Math.Abs(column.ActualWidth - 720) > 1)
            throw new InvalidOperationException($"Software width was clamped to {column.ActualWidth}.");
        if (row.SoftwareCheck!.Contains("unreadable") || !row.SoftwareCheck.Contains("inventory incomplete"))
            throw new InvalidOperationException("Software summary leaked diagnostics.");
        var detailVm = workspace.CreateDetailViewModel(row);
        var details = new ComputerDetailWindow(detailVm);
        try
        {
            ((TabItem)details.FindName("SoftwareDetailsTab")).IsSelected = true;
            var content = (FrameworkElement)details.Content;
            content.Measure(new Size(820, 760));
            content.Arrange(new Rect(0, 0, 820, 760));
            content.UpdateLayout();
            details.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var text = (TextBox)details.FindName("SoftwareInventoryWarning");
            text.GetBindingExpression(TextBox.TextProperty)!.UpdateTarget();
            if (text.Text != warning || !text.IsReadOnly)
                throw new InvalidOperationException($"Full diagnostic binding was lost: {text.Text.Length} characters, context {text.DataContext?.GetType().Name}.");
            SoftwareCheckState.Complete(row, "Dummy", new(true, "Dummy Agent", "2", "Running", "New diagnostic"), 0);
            if (text.Text != "New diagnostic")
                throw new InvalidOperationException("Open details did not refresh after a recheck.");
            return new { pass = true, softwareWidth = column.ActualWidth, summary = row.SoftwareCheck,
                retainedDiagnosticCharacters = warning.Length, liveRecheckRefreshed = true, scope = "Offscreen production controls; dummy data; no remote operations" };
        }
        finally { detailVm.StopRequeryTimer(); details.Close(); }
    }
}
