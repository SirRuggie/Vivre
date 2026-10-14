using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Vivre.Core.Deploy;

namespace Vivre.Core.Tests.Deploy;

public sealed class StagingPublicationIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VivrePublicationIntegration_" + Guid.NewGuid().ToString("N"));

    public StagingPublicationIntegrationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Actual_ps51_archive_chunks_publish_verified_file_over_previous_package()
    {
        string source = Path.Combine(_root, "source.bin");
        string target = Path.Combine(_root, "target.bin");
        File.WriteAllBytes(source, System.Security.Cryptography.RandomNumberGenerator.GetBytes(1_100_000));
        File.WriteAllText(target, "previous package");
        PackagePublicationPlan plan = PackagePublication.Prepare(source, false, target, localTarget: true);

        using JsonDocument result = await TransferAndPublish(plan, corruptLastChunk: false);

        Assert.True(result.RootElement.GetProperty("ok").GetBoolean(), result.RootElement.ToString());
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));
        Assert.False(File.Exists(plan.Backup));
        Assert.False(File.Exists(plan.Stage));
    }

    [Fact]
    public async Task Actual_ps51_truncated_archive_fails_hash_and_preserves_previous_package()
    {
        string source = Path.Combine(_root, "source.bin");
        string target = Path.Combine(_root, "target.bin");
        File.WriteAllBytes(source, Enumerable.Range(0, 200_000).Select(i => (byte)(i % 239)).ToArray());
        File.WriteAllText(target, "previous package");
        PackagePublicationPlan plan = PackagePublication.Prepare(source, false, target, localTarget: true);

        using JsonDocument result = await TransferAndPublish(plan, corruptLastChunk: true);

        Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("SHA-256", result.RootElement.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("previous package", File.ReadAllText(target));
        Assert.False(File.Exists(plan.Backup));
        Assert.False(File.Exists(plan.Stage));
    }

    private async Task<JsonDocument> TransferAndPublish(PackagePublicationPlan plan, bool corruptLastChunk)
    {
        string id = Guid.NewGuid().ToString("N");
        string owner = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(_root, ".vivre-transfer-" + id);
        using StagingArchive archive = await StagingArchive.CreateAsync(plan.Source, plan.IsFolder, CancellationToken.None, _root);
        AssertOk(await RunScript(StagingTransferScripts.BuildInitialize(directory, id, owner)));
        var chunks = new List<StagingArchiveChunk>();
        await foreach (StagingArchiveChunk chunk in archive.ReadChunksAsync()) chunks.Add(chunk);
        Assert.NotEmpty(chunks);
        if (!corruptLastChunk) Assert.True(chunks.Count > 1, "Exercise actual multi-chunk publication.");
        for (int index = 0; index < chunks.Count; index++)
        {
            StagingArchiveChunk chunk = chunks[index];
            string base64 = chunk.Base64;
            if (corruptLastChunk && index == chunks.Count - 1)
            {
                byte[] bytes = Convert.FromBase64String(base64);
                base64 = Convert.ToBase64String(bytes[..^1]);
            }
            AssertOk(await RunScript(StagingTransferScripts.BuildChunk(directory, id, owner, base64, chunk.Offset)));
        }

        string publish = PackagePublication.BuildPublishScript("$payload", true, plan.Target, plan.Stage, plan.Backup,
            plan.IsFolder, PackagePublication.ManifestBase64(plan.Manifest), archive.Sha256, cleanupSourceZip: false);
        JsonDocument result = await RunScript(StagingTransferScripts.WrapPublication(directory, id, owner, publish));
        AssertOk(await RunScript(StagingTransferScripts.BuildCleanup(directory, id, owner)));
        return result;
    }

    private static void AssertOk(JsonDocument result) =>
        Assert.True(result.RootElement.GetProperty("ok").GetBoolean(), result.RootElement.ToString());

    private async Task<JsonDocument> RunScript(string script)
    {
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".ps1");
        await File.WriteAllTextAsync(path, "& {\n" + script + "\n} | ConvertTo-Json -Compress", new UTF8Encoding(true));
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(path);
        start.Environment.Remove("PSModulePath");
        using var process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
        string output = await stdout;
        Assert.True(process.ExitCode == 0, (await stderr) + output);
        return JsonDocument.Parse(output);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
