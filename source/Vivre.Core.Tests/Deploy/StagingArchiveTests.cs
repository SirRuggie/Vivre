using System.IO.Compression;
using System.Security.Cryptography;
using Vivre.Core.Deploy;

namespace Vivre.Core.Tests.Deploy;

public sealed class StagingArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"VivreArchiveTests_{Guid.NewGuid():N}");

    public StagingArchiveTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task File_archive_streams_bounded_chunks_that_reconstruct_and_match_hash()
    {
        string source = Path.Combine(_root, "payload.bin");
        string reconstructed = Path.Combine(_root, "reconstructed.zip");
        string archiveDirectory = NewArchiveDirectory();
        WriteRandomFile(source, 4 * 1024 * 1024 + 137);

        string archivePath;
        string expectedHash;
        await using (var output = new FileStream(reconstructed, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            using var archive = await StagingArchive.CreateAsync(
                source, isFolder: false, CancellationToken.None, archiveDirectory);
            archivePath = archive.Path;
            expectedHash = archive.Sha256;
            long expectedOffset = 0;
            await foreach (StagingArchiveChunk chunk in archive.ReadChunksAsync())
            {
                byte[] raw = Convert.FromBase64String(chunk.Base64);
                Assert.Equal(expectedOffset, chunk.Offset);
                Assert.InRange(raw.Length, 1, StagingArchive.RawChunkSize);
                Assert.True(chunk.Base64.Length <= 2_097_152);
                await output.WriteAsync(raw);
                expectedOffset += raw.Length;
            }

            Assert.Equal(archive.Length, output.Length);
            Assert.NotEqual(0, archive.Length % StagingArchive.RawChunkSize);
        }

        await using (FileStream reconstructedStream = File.OpenRead(reconstructed))
        {
            Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(reconstructedStream)));
        }
        Assert.False(File.Exists(archivePath));
    }

    [Fact]
    public async Task Folder_archive_uses_relative_names_and_preserves_empty_directories()
    {
        string source = Path.Combine(_root, "folder");
        string archiveDirectory = NewArchiveDirectory();
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        Directory.CreateDirectory(Path.Combine(source, "nested", "also-empty"));
        await File.WriteAllTextAsync(Path.Combine(source, "root.txt"), "root");
        await File.WriteAllTextAsync(Path.Combine(source, "nested", "data.txt"), "nested");

        using var staging = await StagingArchive.CreateAsync(
            source, isFolder: true, CancellationToken.None, archiveDirectory);
        using var zip = ZipFile.OpenRead(staging.Path);
        string[] names = zip.Entries.Select(entry => entry.FullName).Order().ToArray();

        Assert.Equal(
            ["empty/", "nested/", "nested/also-empty/", "nested/data.txt", "root.txt"],
            names);
        Assert.DoesNotContain(names, name => name.StartsWith("folder/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Cancellation_during_pack_removes_temporary_archive()
    {
        string source = Path.Combine(_root, "large.bin");
        string archiveDirectory = NewArchiveDirectory();
        WriteRandomFile(source, 32 * 1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => StagingArchive.CreateAsync(source, isFolder: false, cancellation.Token, archiveDirectory));

        Assert.Empty(Directory.GetFiles(archiveDirectory));
    }

    [Fact]
    public async Task Folder_with_reparse_point_is_rejected_and_temporary_archive_is_removed()
    {
        string source = Path.Combine(_root, "source");
        string outside = Path.Combine(_root, "outside");
        string archiveDirectory = NewArchiveDirectory();
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(outside);
        string link = Path.Combine(source, "link");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception linkError) when (linkError is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        IOException error = await Assert.ThrowsAsync<IOException>(
            () => StagingArchive.CreateAsync(source, isFolder: true, CancellationToken.None, archiveDirectory));

        Assert.Contains("reparse point", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(archiveDirectory));
    }

    [Fact]
    public async Task Peak_live_managed_memory_is_bounded_as_payload_grows()
    {
        int[] payloadSizes = [8 * 1024 * 1024, 32 * 1024 * 1024, 96 * 1024 * 1024];
        var measuredPeaks = new List<long>();

        foreach (int payloadSize in payloadSizes)
        {
            string caseDirectory = Path.Combine(_root, $"memory-{payloadSize}");
            string archiveDirectory = Path.Combine(caseDirectory, "archives");
            string source = Path.Combine(caseDirectory, "payload.bin");
            Directory.CreateDirectory(archiveDirectory);
            WriteRandomFile(source, payloadSize);

            ForceCollection();
            long baseline = GC.GetTotalMemory(forceFullCollection: false);
            long peak = baseline;
            using var sampling = new CancellationTokenSource();
            Task sampler = Task.Run(async () =>
            {
                while (!sampling.IsCancellationRequested)
                {
                    long sample = GC.GetTotalMemory(forceFullCollection: true);
                    long observed;
                    do
                    {
                        observed = Volatile.Read(ref peak);
                    }
                    while (sample > observed && Interlocked.CompareExchange(ref peak, sample, observed) != observed);
                    await Task.Delay(10).ConfigureAwait(false);
                }
            });

            try
            {
                using var archive = await StagingArchive.CreateAsync(
                    source, isFolder: false, CancellationToken.None, archiveDirectory);
                await foreach (StagingArchiveChunk chunk in archive.ReadChunksAsync())
                {
                    Assert.InRange(chunk.Base64.Length, 1, 2_097_152);
                    await Task.Yield();
                }
            }
            finally
            {
                sampling.Cancel();
                await sampler;
            }

            measuredPeaks.Add(Math.Max(0, peak - baseline));
            File.Delete(source);
        }

        long smallestPeak = measuredPeaks.Min();
        long largestPeak = measuredPeaks.Max();
        Console.WriteLine(
            "Sampled peak live managed memory: " +
            string.Join(", ", payloadSizes.Zip(measuredPeaks, (size, peak) => $"{size / 1024 / 1024} MiB => {peak / 1024d / 1024d:F1} MiB")));
        Assert.True(
            largestPeak <= smallestPeak + 16 * 1024 * 1024,
            $"Peak live managed memory grew with payload size: " +
            string.Join(", ", payloadSizes.Zip(measuredPeaks, (size, peak) => $"{size / 1024 / 1024} MiB => {peak / 1024d / 1024d:F1} MiB")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void WriteRandomFile(string path, int length)
    {
        byte[] buffer = new byte[128 * 1024];
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        int remaining = length;
        while (remaining > 0)
        {
            RandomNumberGenerator.Fill(buffer);
            int count = Math.Min(buffer.Length, remaining);
            stream.Write(buffer, 0, count);
            remaining -= count;
        }
    }

    private string NewArchiveDirectory()
    {
        string directory = Path.Combine(_root, $"archives-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
