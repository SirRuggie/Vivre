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

    [Fact]
    public async Task Actual_ps51_smb_script_uses_bound_manifest_and_reports_confirmed_cleanup()
    {
        string source = Path.Combine(_root, "bound-source.txt");
        string target = Path.Combine(_root, "bound-target.txt");
        File.WriteAllText(source, "new package"); File.WriteAllText(target, "previous package");
        PackagePublicationPlan plan = PackagePublication.Prepare(source, false, target, true);
        string manifest = JsonSerializer.Serialize(plan.Manifest).Replace("'", "''");
        string script = "& {\n" + PackagePublication.BuildSmbScript(plan) + "\n} -Source '" + source.Replace("'", "''") + "' -ShareRoot '" + _root.Replace("'", "''") + "' -ManifestJson '" + manifest + "'";
        using JsonDocument result = await RunScript(script);
        Assert.True(result.RootElement.GetProperty("ok").GetBoolean(), result.RootElement.ToString());
        Assert.True(result.RootElement.GetProperty("temporaryPathsCleaned").GetBoolean());
        Assert.Equal("new package", File.ReadAllText(target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_ps51_large_manifest_chunks_are_verified_before_publication(bool corruptManifest)
    {
        string source = Path.Combine(_root, "folder");
        string target = Path.Combine(_root, "published");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "previous.txt"), "previous package");
        for (int i = 0; i < 700; i++) File.WriteAllText(Path.Combine(source, $"dummy-{i:D5}.txt"), "");
        PackagePublicationPlan plan = PackagePublication.Prepare(source, true, target, true);
        using JsonDocument result = await TransferAndPublish(plan, false, externalManifest: true, corruptManifest);
        Assert.Equal(!corruptManifest, result.RootElement.GetProperty("ok").GetBoolean());
        if (corruptManifest)
        {
            Assert.Contains("manifest SHA-256", result.RootElement.GetProperty("message").GetString());
            Assert.Equal("previous package", File.ReadAllText(Path.Combine(target, "previous.txt")));
        }
        else
        {
            Assert.Equal(700, Directory.GetFiles(target).Length);
            Assert.True(result.RootElement.GetProperty("temporaryPathsCleaned").GetBoolean());
        }
    }

    private async Task<JsonDocument> TransferAndPublish(PackagePublicationPlan plan, bool corruptLastChunk, bool externalManifest = false, bool corruptManifest = false)
    {
        string id = Guid.NewGuid().ToString("N");
        string owner = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(_root, ".vivre-transfer-" + id);
        using StagingArchive archive = await StagingArchive.CreateAsync(plan.Source, plan.IsFolder, CancellationToken.None, _root);
        AssertOk(await RunScript(StagingTransferScripts.BuildInitialize(directory, id, owner)));
        var chunks = new List<StagingArchiveChunk>();
        await foreach (StagingArchiveChunk chunk in archive.ReadChunksAsync()) chunks.Add(chunk);
        Assert.NotEmpty(chunks);
        if (!corruptLastChunk && !externalManifest) Assert.True(chunks.Count > 1, "Exercise actual multi-chunk publication.");
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

        string? manifestHash = null;
        if (externalManifest)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(plan.Manifest);
            Assert.True(bytes.Length > 64 * 1024);
            manifestHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            if (corruptManifest) bytes[^1] ^= 1;
            for (int offset = 0; offset < bytes.Length; offset += 64 * 1024)
                AssertOk(await RunScript(StagingTransferScripts.BuildChunk(directory, id, owner,
                    Convert.ToBase64String(bytes, offset, Math.Min(64 * 1024, bytes.Length - offset)), offset, "manifest.json")));
        }
        string publish = PackagePublication.BuildPublishScript("$payload", true, plan.Target, plan.Stage, plan.Backup,
            plan.IsFolder, externalManifest ? "" : PackagePublication.ManifestBase64(plan.Manifest), archive.Sha256, cleanupSourceZip: false,
            manifestExpression: externalManifest ? "[IO.File]::ReadAllText($manifestPath, [Text.Encoding]::UTF8)" : null);
        if (externalManifest) Assert.True(publish.Length < 32 * 1024);
        JsonDocument result = await RunScript(StagingTransferScripts.WrapPublication(directory, id, owner, publish, manifestHash));
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
