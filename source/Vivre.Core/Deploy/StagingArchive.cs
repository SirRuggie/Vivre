using System.Buffers;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Vivre.Core.Deploy;

internal sealed record StagingArchiveChunk(long Offset, string Base64);

/// <summary>
/// Owns a temporary ZIP archive and exposes it as bounded base64 chunks without retaining the
/// archive, or its complete base64 representation, in memory.
/// </summary>
internal sealed class StagingArchive : IDisposable
{
    internal const int RawChunkSize = 1024 * 1024;
    private const int CopyBufferSize = 128 * 1024;
    private int _disposed;

    private StagingArchive(string path, long length, string sha256)
    {
        Path = path;
        Length = length;
        Sha256 = sha256;
    }

    internal string Path { get; }
    internal long Length { get; }
    internal string Sha256 { get; }

    internal static async Task<StagingArchive> CreateAsync(
        string sourcePath,
        bool isFolder,
        CancellationToken token,
        string? tempDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        token.ThrowIfCancellationRequested();

        string source = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(sourcePath));
        ValidateSource(source, isFolder);

        string archiveDirectory = tempDirectory is null
            ? System.IO.Path.GetTempPath()
            : System.IO.Path.GetFullPath(tempDirectory);
        if (!Directory.Exists(archiveDirectory))
        {
            throw new DirectoryNotFoundException($"Temporary archive directory does not exist: {archiveDirectory}");
        }

        string archivePath = System.IO.Path.Combine(
            archiveDirectory,
            $"VivreStage_{Guid.NewGuid():N}.zip");
        bool ownsArchive = false;
        try
        {
            await using (var output = new FileStream(
                archivePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                ownsArchive = true;
                await PackAsync(source, isFolder, output, token).ConfigureAwait(false);
            }

            (long length, string hash) = await MeasureAndHashAsync(archivePath, token).ConfigureAwait(false);
            return new StagingArchive(archivePath, length, hash);
        }
        catch (Exception error)
        {
            if (ownsArchive)
            {
                try
                {
                    File.Delete(archivePath);
                }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
                {
                    if (error is OperationCanceledException)
                    {
                        throw new OperationCanceledException(
                            $"Archive creation was cancelled; temporary ZIP cleanup failed at {archivePath}: {cleanupError.Message}",
                            cleanupError,
                            token);
                    }

                    throw new IOException(
                        $"Archive creation failed; temporary ZIP cleanup also failed at {archivePath}: {cleanupError.Message}",
                        new AggregateException(error, cleanupError));
                }
            }

            throw;
        }
    }

    internal async IAsyncEnumerable<StagingArchiveChunk> ReadChunksAsync(
        [EnumeratorCancellation] CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(RawChunkSize);
        try
        {
            await using var stream = new FileStream(
                Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            long offset = 0;
            while (offset < Length)
            {
                token.ThrowIfCancellationRequested();
                int count = 0;
                int requested = (int)Math.Min(RawChunkSize, Length - offset);
                while (count < requested)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(count, requested - count), token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new EndOfStreamException(
                            $"Temporary ZIP ended at {offset + count:N0} bytes; expected {Length:N0} bytes.");
                    }

                    count += read;
                }

                yield return new StagingArchiveChunk(offset, Convert.ToBase64String(buffer, 0, count));
                offset += count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        File.Delete(Path);
    }

    private static async Task PackAsync(string source, bool isFolder, Stream output, CancellationToken token)
    {
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        if (!isFolder)
        {
            await AddFileAsync(archive, source, System.IO.Path.GetFileName(source), token).ConfigureAwait(false);
            return;
        }

        foreach ((string fullPath, string entryName, bool isDirectory) in EnumerateFolder(source, token))
        {
            token.ThrowIfCancellationRequested();
            if (isDirectory)
            {
                archive.CreateEntry(entryName + "/");
            }
            else
            {
                await AddFileAsync(archive, fullPath, entryName, token).ConfigureAwait(false);
            }
        }
    }

    private static async Task AddFileAsync(
        ZipArchive archive,
        string filePath,
        string entryName,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using Stream destination = entry.Open();
        await using var source = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, CopyBufferSize, token).ConfigureAwait(false);
    }

    private static IEnumerable<(string FullPath, string EntryName, bool IsDirectory)> EnumerateFolder(
        string root,
        CancellationToken token)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            string[] entries = Directory.GetFileSystemEntries(directory);
            Array.Sort(entries, StringComparer.OrdinalIgnoreCase);

            var childDirectories = new List<string>();
            foreach (string entry in entries)
            {
                token.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("The source contains a reparse point.");
                }

                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                string relative = Relative(root, entry);
                yield return (entry, relative, isDirectory);
                if (isDirectory)
                {
                    childDirectories.Add(entry);
                }
            }

            for (int index = childDirectories.Count - 1; index >= 0; index--)
            {
                pending.Push(childDirectories[index]);
            }
        }
    }

    private static async Task<(long Length, string Sha256)> MeasureAndHashAsync(
        string archivePath,
        CancellationToken token)
    {
        await using var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long length = 0;
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, CopyBufferSize), token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
                length += read;
            }

            return (length, Convert.ToHexString(hash.GetHashAndReset()));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ValidateSource(string source, bool isFolder)
    {
        if (isFolder ? !Directory.Exists(source) : !File.Exists(source))
        {
            throw new IOException($"The selected {(isFolder ? "folder" : "file")} does not exist: {source}");
        }

        if (isFolder && File.Exists(source) || !isFolder && Directory.Exists(source))
        {
            throw new IOException("The selected source type does not match the package type.");
        }

        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Reparse-point sources are not supported.");
        }
    }

    private static string Relative(string root, string path) =>
        System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
}
