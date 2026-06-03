using System.IO;

namespace Vivre.Desktop;

/// <summary>
/// One selectable software package for the Stage window: either a single file (<c>.msi</c>/<c>.exe</c>)
/// or a folder of files (an installer plus the admin's own scripts). <see cref="Path"/> is the file or
/// folder on disk; <see cref="IsFolder"/> distinguishes the two (a folder stages into its own subfolder;
/// a single file drops straight into the destination).
/// </summary>
public sealed class DeployPackage
{
    public required string Name { get; init; }

    public required string Path { get; init; }

    public required bool IsFolder { get; init; }

    public override string ToString() => Name;

    /// <summary>Builds a package descriptor from a path the user browsed to (file or directory).</summary>
    public static DeployPackage FromPath(string path)
    {
        bool isFolder = Directory.Exists(path);
        string name = isFolder
            ? new DirectoryInfo(path).Name
            : System.IO.Path.GetFileName(path);
        return new DeployPackage { Name = name, Path = path, IsFolder = isFolder };
    }
}

/// <summary>
/// Lists the stageable packages under the configured Packages folder (each subfolder is a package; each
/// lone <c>.msi</c>/<c>.exe</c> directly in the folder is a package too), zips a package for transport,
/// and works out where a package lands on the target. Pure file-system + path helpers — no UI.
/// </summary>
public static class PackageLibrary
{
    /// <summary>Default folder packages are staged into on the target. Namespaced under
    /// <c>C:\Windows\Temp</c> so staged files are easy to find + clean and don't get lost in temp clutter.</summary>
    public const string DefaultStageRoot = @"C:\Windows\Temp\VivrePackages";

    /// <summary>
    /// Lists packages under <paramref name="packagesFolder"/>: every immediate subfolder, plus every
    /// lone <c>.msi</c>/<c>.exe</c> sitting directly in the folder. Returns an empty list when the
    /// folder is unset or missing (the caller can still Browse… to a package elsewhere).
    /// </summary>
    public static IReadOnlyList<DeployPackage> List(string? packagesFolder)
    {
        if (string.IsNullOrWhiteSpace(packagesFolder) || !Directory.Exists(packagesFolder))
        {
            return [];
        }

        var packages = new List<DeployPackage>();
        foreach (string dir in Directory.EnumerateDirectories(packagesFolder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            packages.Add(new DeployPackage { Name = new DirectoryInfo(dir).Name, Path = dir, IsFolder = true });
        }

        foreach (string file in Directory.EnumerateFiles(packagesFolder)
                     .Where(f => IsInstaller(f))
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            packages.Add(new DeployPackage { Name = Path.GetFileName(file), Path = file, IsFolder = false });
        }

        return packages;
    }

    /// <summary>
    /// Works out where a package lands when staged under <paramref name="stageRoot"/>: a <b>folder</b>
    /// package gets its own subfolder (<c>&lt;root&gt;\&lt;name&gt;</c>) so its files stay together and a
    /// wrapper script's relative paths still resolve; a <b>single file</b> drops straight into the root
    /// (<c>&lt;root&gt;\&lt;file&gt;</c>). Returns that final file/folder path on the target.
    /// </summary>
    public static string ResolveStageTarget(DeployPackage package, string stageRoot)
    {
        string root = stageRoot.Trim().TrimEnd('\\', '/');
        return package.IsFolder
            ? Path.Combine(root, package.Name)
            : Path.Combine(root, Path.GetFileName(package.Path));
    }

    private static bool IsInstaller(string path)
    {
        string ext = Path.GetExtension(path);
        return string.Equals(ext, ".msi", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase);
    }
}
