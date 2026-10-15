using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Vivre.Core.Configuration;
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
    // The package library folder is a machine-wide operational setting (shared across operators).
    private readonly SharedSettingsStore _shared = new();
    private readonly Func<string?, IReadOnlyList<DeployPackage>> _listPackages;
    private readonly Func<string?>? _readFolder;
    private int _loadGeneration;
    private bool _closed;

    public DeployWindow(WorkspaceViewModel vm, IReadOnlyList<Computer> computers, Func<string?, IReadOnlyList<DeployPackage>>? listPackages = null, Func<string?>? readFolder = null)
    {
        _listPackages = listPackages ?? PackageLibrary.List;
        _readFolder = readFolder;
        InitializeComponent();
        _vm = vm;
        _computers = computers;

        Intro.Text = $"Copy a package to {computers.Count} machine(s) so you can install it your way. Pick a package "
            + "(a single MSI/EXE, or a folder of files), choose where to drop it, then Stage. Vivre copies the files "
            + "to each machine and reports the path per row — it does not run them. Install afterwards however you "
            + "like (e.g. right-click ▸ Run script… pointing at the staged path).";
        StageButton.Content = $"Stage to {computers.Count}";
        StagePathBox.Text = PackageLibrary.DefaultStageRoot;

        Loaded += async (_, _) => await LoadPackagesAsync();
        Closed += (_, _) => { _closed = true; ++_loadGeneration; };
    }

    private string? PackagesFolder
    {
        get
        {
            try
            {
                return _readFolder is not null ? _readFolder() : _shared.Load().PackagesFolder;
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
    private async Task LoadPackagesAsync()
    {
        int generation = ++_loadGeneration;
        PackageHint.Text = "Loading package library… You can still Browse to a package.";
        try
        {
            var result = await Task.Run(() =>
            {
                string? folder = PackagesFolder;
                return (Folder: folder, Packages: _listPackages(folder));
            });
            if (_closed || generation != _loadGeneration) return;
            PackageBox.ItemsSource = result.Packages;
            if (result.Packages.Count > 0) PackageBox.SelectedIndex = 0;
            else PackageHint.Text = string.IsNullOrWhiteSpace(result.Folder)
                ? "No package library folder set. Set one below, or use Browse… to pick a package anywhere."
                : "No packages found, or the library folder is unavailable. Browse to a package or choose a different library folder.";
        }
        catch (Exception ex)
        {
            if (!_closed && generation == _loadGeneration)
                PackageHint.Text = $"Couldn't load the package library: {ex.Message}. Browse to a package or try another folder.";
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
        if (!string.IsNullOrWhiteSpace(folder))
        {
            dialog.InitialDirectory = folder;
        }

        if (dialog.ShowDialog(this) == true)
        {
            SelectBrowsedPackage(new DeployPackage { Name = Path.GetFileName(dialog.FileName), Path = dialog.FileName, IsFolder = false });
        }
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Pick a package folder",
        };
        string? folder = PackagesFolder;
        if (!string.IsNullOrWhiteSpace(folder))
        {
            dialog.InitialDirectory = folder;
        }

        if (dialog.ShowDialog(this) == true)
        {
            SelectBrowsedPackage(new DeployPackage { Name = new DirectoryInfo(dialog.FolderName).Name, Path = dialog.FolderName, IsFolder = true });
        }
    }

    /// <summary>Sets (and persists) the package library folder, then reloads the dropdown from it. Lets
    /// the user point Vivre at their package collection once and pick from it on every later stage.</summary>
    private async void OnSetLibraryFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Pick your package library folder" };
        string? current = PackagesFolder;
        if (!string.IsNullOrWhiteSpace(current))
        {
            dialog.InitialDirectory = current;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            // Sibling-safe: Update reads the shared file fresh and changes ONLY PackagesFolder; a degraded read
            // refuses the write (throws) rather than stomping the other operational keys with defaults.
            string selectedFolder = dialog.FolderName;
            await Task.Run(() => _shared.Update(s => s.PackagesFolder = selectedFolder));
        }
        catch (Exception ex)
        {
            ShowStatus($"Couldn't save the package library folder: {ex.Message}");
            return;
        }

        await LoadPackagesAsync();
    }

    /// <summary>Adds a browsed-to package to the dropdown (if not already there) and selects it, so the
    /// dropdown always reflects the chosen package even when it lives outside the Packages folder.</summary>
    private void SelectBrowsedPackage(DeployPackage package)
    {
        ++_loadGeneration; // A late library read must not replace the operator's browsed choice.
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
