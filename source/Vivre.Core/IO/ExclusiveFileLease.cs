using System.Diagnostics;

namespace Vivre.Core.IO;

/// <summary>
/// Bounded cross-process ownership for a file-backed read/merge/write transaction. The lease file
/// is separate from the protected data file so atomic replacement of that data does not release
/// ownership. Sharing violations are retried; every other open failure propagates immediately.
/// </summary>
internal static class ExclusiveFileLease
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);

    internal static FileStream Acquire(string path) => Acquire(path, DefaultTimeout);

    internal static FileStream Acquire(string path, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var elapsed = Stopwatch.StartNew();
        IOException? lastSharingViolation = null;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                lastSharingViolation = ex;
                if (elapsed.Elapsed >= timeout)
                {
                    throw new IOException(
                        $"Timed out after {timeout.TotalSeconds:0.###} seconds waiting for the persistence lease '{path}'.",
                        lastSharingViolation);
                }

                Thread.Sleep(RetryDelay);
            }
        }
    }

    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xffff) is 32 or 33;
}
