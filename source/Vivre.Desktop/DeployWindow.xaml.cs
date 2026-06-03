using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Vivre.Core.Models;
using Vivre.Desktop.ViewModels;
using Wpf.Ui.Controls;

namespace Vivre.Desktop;

/// <summary>
/// Picks a package (a single MSI/EXE or a folder of files) and a destination, then <b>stages</b> it to
/// the in-scope machines — Vivre copies the files there; it does NOT run or install anything. Progress
/// shows in the main window (each row's Command result + the activity log). The admin runs the install
/// themselves afterwards (e.g. Run script… pointing at the staged path). A review-before-run
/// <see cref="FluentWindow"/> dialog that never auto-runs; on Stage it zips the payload, fires the copy
/// in the background via <see cref="WorkspaceViewModel"/>, and closes.
/// </summary>
public partial class DeployWindow : FluentWindow
{
    private readonly WorkspaceViewModel _vm;
    private readonly IReadOnlyList<Computer> _computers;
    private readonly AppSettingsStore _settings = new();

    public DeployWindow(WorkspaceViewModel vm, IReadOnlyList<Computer> computers)
    {
        InitializeComponent();
        _vm = vm;
        _computers = computers;

        Intro.Text = $"Copy a package to {computers.Count} machine(s) so you can install it your way. Pick a package "
            + "(a single MSI/EXE, or a folder of files), choose where to drop it, then Stage. Vivre copies the files "
            + "to each machine and reports the path per row — it does not run them. Install afterwards however you "
            + "like (e.g. right-click ▸ Run script… pointing at the staged path).";
        StageButton.Content = $"Stage to {computers.Count}";
        StagePathBox.Text = PackageLibrary.DefaultStageRoot;

        LoadPackages();
    }

    private string? PackagesFolder
    {
        get
        {
            try
            {
                return _settings.Load().PackagesFolder;
            }
            catch
            {
                // A settings read failure just means no pre-listed packages — Browse… still works.
                return null;
            }
        }
    }

    /// <summary>Fills the package dropdown from the configured Packages folder (subfolders + lone
    /// installers). Adds a hint when the folder is unset/empty so the user knows to set it or Browse….</summary>
    private void LoadPackages()
    {
        string? folder = PackagesFolder;
        IReadOnlyList<DeployPackage> packages = PackageLibrary.List(folder);
        PackageBox.ItemsSource = packages;

        if (packages.Count > 0)
        {
            PackageBox.SelectedIndex = 0;
        }
        else
        {
            PackageHint.Text = string.IsNullOrWhiteSpace(folder)
                ? "No package library folder set. Set one below, or use Browse… to pick a package anywhere."
                : "No packages found in your library folder. Use Browse… to pick one anywhere, or set a different library folder.";
        }
    }

    private DeployPackage? SelectedPackage => PackageBox.SelectedItem as DeployPackage;

    private void OnPackageChanged(object sender, SelectionChangedEventArgs e) => OnPackageSelected();

    /// <summary>Reacts to a package selection: show its source path and refresh the destination preview.</summary>
    private void OnPackageSelected()
    {
        DeployPackage? package = SelectedPackage;
        if (package is not null)
        {
            PackageHint.Text = package.Path;
        }

        UpdatePreview();
    }

    /// <summary>Keep the destination preview in step with the Stage-to box as the user types/pastes.</summary>
    private void OnStagePathChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void OnBrowseFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Pick a package file",
            Filter = "Installers (*.msi;*.exe)|*.msi;*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        string? folder = PackagesFolder;
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            dialog.InitialDirectory = folder;
        }

        if (dialog.ShowDialog(this) == true)
        {
            SelectBrowsedPackage(DeployPackage.FromPath(dialog.FileName));
        }
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Pick a package folder",
        };
        string? folder = PackagesFolder;
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            dialog.InitialDirectory = folder;
        }

        if (dialog.ShowDialog(this) == true)
        {
            SelectBrowsedPackage(DeployPackage.FromPath(dialog.FolderName));
        }
    }

    /// <summary>Sets (and persists) the package library folder, then reloads the dropdown from it. Lets
    /// the user point Vivre at their package collection once and pick from it on every later stage.</summary>
    private void OnSetLibraryFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Pick your package library folder" };
        string? current = PackagesFolder;
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            AppSettings s = _settings.Load();
            s.PackagesFolder = dialog.FolderName;
            _settings.Save(s);
        }
        catch (Exception ex)
        {
            ShowStatus($"Couldn't save the package library folder: {ex.Message}");
            return;
        }

        LoadPackages();
        OnPackageSelected();
    }

    /// <summary>Adds a browsed-to package to the dropdown (if not already there) and selects it, so the
    /// dropdown always reflects the chosen package even when it lives outside the Packages folder.</summary>
    private void SelectBrowsedPackage(DeployPackage package)
    {
        var items = (PackageBox.ItemsSource as IEnumerable<DeployPackage>)?.ToList() ?? [];
        DeployPackage? existing = items.FirstOrDefault(
            p => string.Equals(p.Path, package.Path, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            items.Add(package);
            PackageBox.ItemsSource = items;
            existing = package;
        }

        PackageBox.SelectedItem = existing;
        // SelectionChanged fires the rest (hint, preview).
    }

    /// <summary>Shows where the package will land on each machine so the admin can review before staging.</summary>
    private void UpdatePreview()
    {
        if (TryResolveTarget(out string targetPath, out _))
        {
            ShowStatus($"Will copy to each machine (no install runs):\n{targetPath}");
        }
    }

    /// <summary>Works out the final file/folder path on the target, from the selected package and the
    /// Stage-to box. False (with an error) when no package is picked or the destination is blank.</summary>
    private bool TryResolveTarget(out string targetPath, out string? error)
    {
        targetPath = string.Empty;
        error = null;

        DeployPackage? package = SelectedPackage;
        if (package is null)
        {
            error = "Pick a package first.";
            return false;
        }

        string root = StagePathBox.Text?.Trim() ?? string.Empty;
        if (root.Length == 0)
        {
            error = "Enter where to stage the files (e.g. C:\\Windows\\Temp\\VivrePackages).";
            return false;
        }

        targetPath = PackageLibrary.ResolveStageTarget(package, root);
        return true;
    }

    private void OnStage(object sender, RoutedEventArgs e)
    {
        if (!TryResolveTarget(out string targetPath, out string? error))
        {
            ShowStatus(error ?? "Can't work out where to stage the package.");
            return;
        }

        DeployPackage package = SelectedPackage!;

        // Fire-and-forget the copy (the service copies the source files itself — SMB, else WinRM), so the
        // window closes and the user can keep working; the per-row Command result + activity log carry
        // progress. Nothing is zipped/read here — the source files are copied at stage time.
        _ = _vm.StageSelectedAsync(_computers, package.Path, package.IsFolder, targetPath, package.Name);
        Close();
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
