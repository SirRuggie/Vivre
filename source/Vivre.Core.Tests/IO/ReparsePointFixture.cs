using System.Diagnostics;

namespace Vivre.Core.Tests.IO;

internal static class ReparsePointFixture
{
    // Junction creation needs no symlink privilege and exercises the same directory reparse guard.
    internal static void CreateDirectoryLink(string link, string target)
    {
        string fullLink = Path.GetFullPath(link);
        string fullTarget = Path.GetFullPath(target);
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullLink.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
            || !fullTarget.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
            || fullLink.IndexOfAny(['"', '%', '!', '\r', '\n']) >= 0
            || fullTarget.IndexOfAny(['"', '%', '!', '\r', '\n']) >= 0)
            throw new InvalidOperationException("Reparse fixture paths must be literal temporary paths.");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            Arguments = $"/d /c mklink /J \"{fullLink}\" \"{fullTarget}\"",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(5000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Temporary junction fixture creation timed out.");
        }
        if (process.ExitCode != 0 || (File.GetAttributes(fullLink) & FileAttributes.ReparsePoint) == 0)
            throw new InvalidOperationException("Temporary junction fixture unavailable: " + output.Result + error.Result);
    }
}

/// <summary>Report unavailable Windows file-symlink privilege as an explicit skipped theory.</summary>
public sealed class FileSymlinkTheoryAttribute : Xunit.TheoryAttribute
{
    public FileSymlinkTheoryAttribute()
    {
        string root = Path.Combine(Path.GetTempPath(), "Vivre-symlink-prerequisite-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            string target = Path.Combine(root, "target.txt");
            File.WriteAllText(target, "dummy");
            File.CreateSymbolicLink(Path.Combine(root, "link.txt"), target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Skip = "Validation prerequisite unavailable: Windows file-symlink creation privilege or Developer Mode is required. " + ex.Message;
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
