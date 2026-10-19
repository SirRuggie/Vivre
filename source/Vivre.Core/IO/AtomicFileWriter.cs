namespace Vivre.Core.IO;

/// <summary>Crash-safe whole-file write: writes to a same-directory temp file, then atomically
/// swaps it over the destination (File.Replace → Win32 ReplaceFile), so a crash mid-write leaves
/// the previous good file intact instead of a truncated one. The same-directory temp guarantees
/// File.Replace's same-volume requirement. Callers serialize their own writers — the
/// exists-check → swap sequence is only race-free within one process under the caller's lock.</summary>
public static class AtomicFileWriter
{
    public static void Write(string path, string contents) => Write(path, contents, overwrite: true);

    public static void Write(string path, string contents, bool overwrite)
    {
        if (!overwrite)
        {
            // Publish a complete new file without ever replacing a concurrently-created target.
            string candidate = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(candidate, contents);
                File.Move(candidate, path, overwrite: false);
            }
            finally { if (File.Exists(candidate)) File.Delete(candidate); }
            return;
        }
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, contents); // truncates any stale .tmp a prior crash left behind
        if (File.Exists(path))
        {
            File.Replace(tmp, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tmp, path); // first-ever save — File.Replace throws if the destination is absent
        }
    }
}
