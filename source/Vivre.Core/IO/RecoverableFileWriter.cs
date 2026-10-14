namespace Vivre.Core.IO;

/// <summary>
/// Writes a whole file through a same-directory temporary and retains a last-known-good backup.
/// When the current destination is known to be unreadable, an existing good backup is preserved.
/// </summary>
internal static class RecoverableFileWriter
{
    internal static void Write(string path, string contents, bool preserveExistingBackup)
        => Write(path, contents, preserveExistingBackup, File.Move);

    internal static void WriteForTest(
        string path,
        string contents,
        bool preserveExistingBackup,
        Action<string, string> publishPrimary)
        => Write(path, contents, preserveExistingBackup, publishPrimary);

    private static void Write(string path, string contents, bool preserveExistingBackup, Action<string, string> publishPrimary)
    {
        string backup = path + ".bak";
        string suffix = "." + Guid.NewGuid().ToString("N") + ".tmp";
        string temporary = path + suffix;
        string backupTemporary = backup + suffix;

        try
        {
            File.WriteAllText(temporary, contents);

            if (File.Exists(path))
            {
                File.Replace(temporary, path, preserveExistingBackup ? null : backup);
                return;
            }

            if (!preserveExistingBackup)
            {
                // On genuine first use, publish a recoverable copy before the primary. If publishing
                // the primary then fails, the next load can still recover from the completed backup.
                File.Copy(temporary, backupTemporary, overwrite: true);
                File.Move(backupTemporary, backup);
            }
            publishPrimary(temporary, path);
        }
        finally
        {
            TryDelete(temporary);
            TryDelete(backupTemporary);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The original write/replacement exception is the actionable failure.
        }
        catch (UnauthorizedAccessException)
        {
            // The original write/replacement exception is the actionable failure.
        }
    }
}
