using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Vivre.Core.Deploy;

namespace Vivre.Core.Tests.Deploy;

public sealed class StagingTransferScriptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VivreTransfer_" + Guid.NewGuid().ToString("N"));
    private readonly Guid _id = Guid.NewGuid();
    private readonly Guid _owner = Guid.NewGuid();

    public StagingTransferScriptTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Actual_ps51_initializes_and_appends_only_at_exact_offset()
    {
        string directory = TransferDirectory();
        AssertOk(await RunScript(StagingTransferScripts.BuildInitialize(directory, _id.ToString("N"), _owner.ToString("N"))));
        AssertOk(await RunScript(StagingTransferScripts.BuildChunk(directory, _id.ToString("N"), _owner.ToString("N"),
            Convert.ToBase64String([1, 2, 3]), 0)));
        AssertOk(await RunScript(StagingTransferScripts.BuildChunk(directory, _id.ToString("N"), _owner.ToString("N"),
            Convert.ToBase64String([4, 5]), 3)));

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, File.ReadAllBytes(Path.Combine(directory, "payload.zip")));
        using JsonDocument mismatch = await RunScript(StagingTransferScripts.BuildChunk(directory, _id.ToString("N"), _owner.ToString("N"),
            Convert.ToBase64String([9]), 2));
        Assert.False(mismatch.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, File.ReadAllBytes(Path.Combine(directory, "payload.zip")));
    }

    [Fact]
    public async Task Actual_ps51_refuses_wrong_owner_and_preserves_payload()
    {
        string directory = TransferDirectory();
        AssertOk(await RunScript(StagingTransferScripts.BuildInitialize(directory, _id.ToString("N"), _owner.ToString("N"))));
        Guid wrong = Guid.NewGuid();

        using JsonDocument result = await RunScript(StagingTransferScripts.BuildChunk(directory, _id.ToString("N"), wrong.ToString("N"),
            Convert.ToBase64String([1]), 0));

        Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(File.Exists(Path.Combine(directory, "payload.zip")));
        Assert.True(File.Exists(Path.Combine(directory, "owner.json")));
    }

    [Fact]
    public async Task Actual_ps51_cleanup_refuses_busy_lock_then_removes_only_owned_directory()
    {
        string directory = TransferDirectory();
        AssertOk(await RunScript(StagingTransferScripts.BuildInitialize(directory, _id.ToString("N"), _owner.ToString("N"))));
        using (File.Open(Path.Combine(directory, "transfer.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using JsonDocument busy = await RunScript(StagingTransferScripts.BuildCleanup(directory, _id.ToString("N"), _owner.ToString("N")));
            Assert.False(busy.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(busy.RootElement.GetProperty("cleanupPending").GetBoolean());
            Assert.True(Directory.Exists(directory));
        }

        AssertOk(await RunScript(StagingTransferScripts.BuildCleanup(directory, _id.ToString("N"), _owner.ToString("N"))));
        Assert.False(Directory.Exists(directory));
        using JsonDocument missing = await RunScript(StagingTransferScripts.BuildCleanup(directory, _id.ToString("N"), _owner.ToString("N")));
        Assert.True(missing.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(missing.RootElement.GetProperty("cleanupPending").GetBoolean());
        Assert.True(missing.RootElement.GetProperty("absent").GetBoolean());
    }

    [Fact]
    public async Task Actual_ps51_cleanup_preserves_unknown_file_and_reports_pending()
    {
        string directory = TransferDirectory();
        AssertOk(await RunScript(StagingTransferScripts.BuildInitialize(directory, _id.ToString("N"), _owner.ToString("N"))));
        string unrelated = Path.Combine(directory, "unrelated.txt");
        File.WriteAllText(unrelated, "do not delete");

        using JsonDocument result = await RunScript(StagingTransferScripts.BuildCleanup(directory, _id.ToString("N"), _owner.ToString("N")));

        Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(result.RootElement.GetProperty("cleanupPending").GetBoolean());
        Assert.Equal("do not delete", File.ReadAllText(unrelated));
        Assert.True(File.Exists(Path.Combine(directory, "owner.json")));
    }

    [Fact]
    public async Task Actual_ps51_publication_holds_lock_and_surfaces_corrupt_zip_failure()
    {
        string directory = TransferDirectory();
        AssertOk(await RunScript(StagingTransferScripts.BuildInitialize(directory, _id.ToString("N"), _owner.ToString("N"))));
        AssertOk(await RunScript(StagingTransferScripts.BuildChunk(directory, _id.ToString("N"), _owner.ToString("N"),
            Convert.ToBase64String(Encoding.UTF8.GetBytes("not a zip")), 0)));
        string extract = Path.Combine(_root, "extract");
        string publication = $"Expand-Archive -LiteralPath $payload -DestinationPath '{extract.Replace("'", "''")}' -Force\n" +
            "[PSCustomObject]@{ ok=$true; message='unexpected' }";

        using JsonDocument result = await RunScript(StagingTransferScripts.WrapPublication(
            directory, _id.ToString("N"), _owner.ToString("N"), publication));

        Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("publication failed", result.RootElement.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(directory, "payload.zip")));
    }

    [Fact]
    public async Task Actual_ps51_refuses_reparse_transfer_path()
    {
        string realParent = Path.Combine(_root, "real");
        string linkedParent = Path.Combine(_root, "linked");
        Directory.CreateDirectory(realParent);
        Vivre.Core.Tests.IO.ReparsePointFixture.CreateDirectoryLink(linkedParent, realParent);
        string directory = Path.Combine(linkedParent, $".vivre-transfer-{_id:N}");

        try
        {
            using JsonDocument result = await RunScript(StagingTransferScripts.BuildInitialize(directory, _id.ToString("N"), _owner.ToString("N")));
            Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
            Assert.False(Directory.Exists(Path.Combine(realParent, $".vivre-transfer-{_id:N}")));
        }
        finally
        {
            // Remove the junction itself before generic cleanup removes its target directory.
            Directory.Delete(linkedParent);
        }
    }

    [Vivre.Core.Tests.IO.FileSymlinkTheory]
    [InlineData("owner.json", false)]
    [InlineData("transfer.lock", false)]
    [InlineData("payload.zip", true)]
    public async Task Actual_ps51_refuses_reparse_owned_file_without_touching_target(string name, bool publish)
    {
        string directory = TransferDirectory();
        AssertOk(await RunScript(StagingTransferScripts.BuildInitialize(directory, _id.ToString("N"), _owner.ToString("N"))));
        if (publish)
            AssertOk(await RunScript(StagingTransferScripts.BuildChunk(directory, _id.ToString("N"), _owner.ToString("N"), Convert.ToBase64String([1]), 0)));
        string outside = Path.Combine(_root, name + ".outside");
        File.WriteAllText(outside, "outside");
        File.Delete(Path.Combine(directory, name));
        try { File.CreateSymbolicLink(Path.Combine(directory, name), outside); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { throw new InvalidOperationException("Validation prerequisite unavailable: creating symlinks is required to exercise transfer reparse-point protection.", ex); }

        using JsonDocument result = publish
            ? await RunScript(StagingTransferScripts.WrapPublication(directory, _id.ToString("N"), _owner.ToString("N"), "[PSCustomObject]@{ ok=$true }") )
            : await RunScript(StagingTransferScripts.BuildChunk(directory, _id.ToString("N"), _owner.ToString("N"), Convert.ToBase64String([2]), 0));

        Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("outside", File.ReadAllText(outside));
    }

    [Fact]
    public async Task Actual_ps51_cleanup_resumes_partially_deleted_owned_tombstone()
    {
        string directory = TransferDirectory();
        AssertOk(await RunScript(StagingTransferScripts.BuildInitialize(directory, _id.ToString("N"), _owner.ToString("N"))));
        AssertOk(await RunScript(StagingTransferScripts.BuildChunk(directory, _id.ToString("N"), _owner.ToString("N"), Convert.ToBase64String([1]), 0)));
        string tombstone = directory + ".cleanup-" + _owner.ToString("N");
        Directory.Move(directory, tombstone);
        File.Delete(Path.Combine(tombstone, "owner.json"));

        using JsonDocument result = await RunScript(StagingTransferScripts.BuildCleanup(directory, _id.ToString("N"), _owner.ToString("N")));

        AssertOk(result);
        Assert.False(result.RootElement.GetProperty("absent").GetBoolean());
        Assert.False(Directory.Exists(tombstone));
    }

    [Fact]
    public async Task Actual_ps51_cleanup_recovers_deterministic_initializer_directory()
    {
        string directory = TransferDirectory();
        AssertOk(await RunScript(StagingTransferScripts.BuildInitialize(directory, _id.ToString("N"), _owner.ToString("N"))));
        string initial = directory + ".init-" + _owner.ToString("N");
        Directory.Move(directory, initial);

        AssertOk(await RunScript(StagingTransferScripts.BuildCleanup(directory, _id.ToString("N"), _owner.ToString("N"))));

        Assert.False(Directory.Exists(initial));
    }

    [Fact]
    public async Task Actual_ps51_initializer_refuses_existing_owned_initializer_without_modifying_it()
    {
        string directory = TransferDirectory();
        string initial = directory + ".init-" + _owner.ToString("N");
        Directory.CreateDirectory(initial);
        string sentinel = Path.Combine(initial, "sentinel.txt");
        File.WriteAllText(sentinel, "keep");

        using JsonDocument result = await RunScript(StagingTransferScripts.BuildInitialize(directory, _id.ToString("N"), _owner.ToString("N")));

        Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("keep", File.ReadAllText(sentinel));
    }

    [Fact]
    public async Task Actual_ps51_cleanup_refuses_when_live_and_tombstone_both_exist()
    {
        string directory = TransferDirectory();
        AssertOk(await RunScript(StagingTransferScripts.BuildInitialize(directory, _id.ToString("N"), _owner.ToString("N"))));
        string tombstone = directory + ".cleanup-" + _owner.ToString("N");
        Directory.CreateDirectory(tombstone);

        using JsonDocument result = await RunScript(StagingTransferScripts.BuildCleanup(directory, _id.ToString("N"), _owner.ToString("N")));

        Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(Directory.Exists(directory));
        Assert.True(Directory.Exists(tombstone));
    }

    [Fact]
    public void Builders_reject_root_wildcard_and_wrong_leaf()
    {
        Assert.Throws<ArgumentException>(() => StagingTransferScripts.BuildInitialize(Path.GetPathRoot(_root)!, _id.ToString("N"), _owner.ToString("N")));
        Assert.Throws<ArgumentException>(() => StagingTransferScripts.BuildInitialize(Path.Combine(_root, "*"), _id.ToString("N"), _owner.ToString("N")));
        Assert.Throws<ArgumentException>(() => StagingTransferScripts.BuildInitialize(Path.Combine(_root, "wrong"), _id.ToString("N"), _owner.ToString("N")));
    }

    private string TransferDirectory() => Path.Combine(_root, $".vivre-transfer-{_id:N}");

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
