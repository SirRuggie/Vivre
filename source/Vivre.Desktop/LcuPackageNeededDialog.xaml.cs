using System.Diagnostics;
using System.IO;
using System.Windows;
using Vivre.Desktop.ViewModels;
using Wpf.Ui.Controls;

namespace Vivre.Desktop;

/// <summary>
/// The guided "the Server 2016 update package isn't ready — here's how to fix it" prompt shown by the panel's
/// Stage button before any box is touched. It teaches the operator (and whoever the lane is handed off to)
/// what to download, where to get it, where to drop it, and what to do next. "Stage now" returns
/// <see cref="Window.DialogResult"/> <c>true</c> so the caller re-checks the folder and proceeds if the file
/// is now present; Cancel / close returns null/false.
/// </summary>
public partial class LcuPackageNeededDialog : FluentWindow
{
    private readonly string _folder;
    private readonly string _catalogUrl;

    public LcuPackageNeededDialog(LcuStageReadiness readiness)
    {
        InitializeComponent();

        _folder = readiness.Folder;
        _catalogUrl = readiness.CatalogUrl;

        ProblemBar.Message = readiness.Problem;
        NeedsText.Text = $"{readiness.Kb}   ·   {readiness.Arch}";
        CatalogLinkText.Text = readiness.CatalogUrl;
        FolderBox.Text = readiness.Folder;
    }

    /// <summary>Opens the package folder in Explorer, creating it first if it doesn't exist yet so the
    /// operator always lands somewhere they can drop the file.</summary>
    private async void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_folder))
        {
            return;
        }

        try
        {
            if (sender is System.Windows.Controls.Control control) control.IsEnabled = false;
            await Task.Run(() => Directory.CreateDirectory(_folder));
            if (!IsVisible) return;
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ProblemBar.Message = $"Could not open the package folder: {ex.Message}";
        }
        finally
        {
            if (sender is System.Windows.Controls.Control control) control.IsEnabled = true;
        }
    }

    /// <summary>Opens the Microsoft Update Catalog search (pre-filled to this month's KB) in the default browser.</summary>
    private void OnOpenCatalog(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_catalogUrl))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_catalogUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Open catalog link failed: {ex.Message}");
        }
    }

    private void OnStageNow(object sender, RoutedEventArgs e)
    {
        DialogResult = true; // caller re-checks the folder and stages if the file is now present
        Close();
    }
}
