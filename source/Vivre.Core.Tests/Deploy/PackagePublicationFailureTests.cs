using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vivre.Core.Deploy;

namespace Vivre.Core.Tests.Deploy;

public sealed class PackagePublicationFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VivrePublicationFailures_" + Guid.NewGuid().ToString("N"));

    public PackagePublicationFailureTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Changed_source_with_same_length_fails_hash_verification_without_replacing_old_package()
    {
        var plan = PrepareFile();
        File.WriteAllText(plan.Source, "evil");

        StageResult result = PackagePublication.PublishLocal(plan, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("manifest", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old package", File.ReadAllText(plan.Target));
        Assert.False(File.Exists(plan.Backup));
        Assert.False(File.Exists(plan.Stage));
    }

    [Fact]
    public void Locked_old_package_survives_failed_replacement_and_candidate_is_cleaned()
    {
        var plan = PrepareFile();
        StageResult result;
        using (File.Open(plan.Target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = PackagePublication.PublishLocal(plan, CancellationToken.None);
        }

        Assert.False(result.Ok);
        Assert.Equal("old package", File.ReadAllText(plan.Target));
        Assert.False(File.Exists(plan.Backup));
        Assert.False(File.Exists(plan.Stage));
    }

    [Fact]
    public void Cancellation_before_publication_preserves_old_package()
    {
        var plan = PrepareFile();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => PackagePublication.PublishLocal(plan, cancellation.Token));

        Assert.Equal("old package", File.ReadAllText(plan.Target));
        Assert.False(File.Exists(plan.Backup));
        Assert.False(File.Exists(plan.Stage));
    }

    [Fact]
    public void Existing_candidate_collision_is_never_deleted_as_owned_cleanup()
    {
        var plan = PrepareFile();
        File.WriteAllText(plan.Stage, "another operation");

        StageResult result = PackagePublication.PublishLocal(plan, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("old package", File.ReadAllText(plan.Target));
        Assert.Equal("another operation", File.ReadAllText(plan.Stage));
        Assert.False(File.Exists(plan.Backup));
    }

    [Fact]
    public void Existing_backup_collision_preserves_both_old_package_and_foreign_backup()
    {
        var plan = PrepareFile();
        File.WriteAllText(plan.Backup, "foreign backup");

        StageResult result = PackagePublication.PublishLocal(plan, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("old package", File.ReadAllText(plan.Target));
        Assert.Equal("foreign backup", File.ReadAllText(plan.Backup));
        Assert.False(File.Exists(plan.Stage));
    }

    [Theory]
    [InlineData("changed-source")]
    [InlineData("locked-target")]
    [InlineData("candidate-collision")]
    public async Task Actual_ps51_failure_preserves_old_package(string failure)
    {
        var plan = PrepareFile();
        if (failure == "changed-source") File.WriteAllText(plan.Source, "evil");
        if (failure == "candidate-collision") File.WriteAllText(plan.Stage, "foreign");
        using (var locked = failure == "locked-target"
            ? File.Open(plan.Target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            : null)
        {
            using JsonDocument result = await RunScript(PackagePublication.BuildPublishScript(
                Quote(plan.Source), false, plan.Target, plan.Stage, plan.Backup, false,
                PackagePublication.ManifestBase64(plan.Manifest)));
            Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
        }
        Assert.Equal("old package", File.ReadAllText(plan.Target));
        Assert.False(File.Exists(plan.Backup));
        if (failure == "candidate-collision") Assert.Equal("foreign", File.ReadAllText(plan.Stage));
        else Assert.False(File.Exists(plan.Stage));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_ps51_zip_publication_is_exact_and_removes_owned_transfer_files(bool folder)
    {
        string source = Path.Combine(_root, "incoming");
        string target = Path.Combine(_root, "renamed");
        if (folder)
        {
            Directory.CreateDirectory(Path.Combine(source, "empty"));
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(source, "payload.bin"), "new bytes");
            File.WriteAllText(Path.Combine(target, "obsolete.bin"), "old bytes");
        }
        else { File.WriteAllText(source, "new bytes"); File.WriteAllText(target, "old bytes"); }
        string sibling = Path.Combine(_root, "other.bin");
        File.WriteAllText(sibling, "preserve");
        var plan = PackagePublication.Prepare(source, folder, target, true);
        string zip = Path.Combine(_root, "transfer.zip");
        if (folder) ZipFile.CreateFromDirectory(source, zip);
        else
        {
            using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
            archive.CreateEntryFromFile(source, "incoming");
        }
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)));
        using JsonDocument result = await RunScript(PackagePublication.BuildPublishScript(
            Quote(zip), true, target, plan.Stage, plan.Backup, folder,
            PackagePublication.ManifestBase64(plan.Manifest), hash));
        Assert.True(result.RootElement.GetProperty("ok").GetBoolean(), result.RootElement.ToString());
        Assert.Equal("new bytes", File.ReadAllText(folder ? Path.Combine(target, "payload.bin") : target));
        if (folder)
        {
            Assert.False(File.Exists(Path.Combine(target, "obsolete.bin")));
            Assert.True(Directory.Exists(Path.Combine(target, "empty")));
        }
        Assert.Equal("preserve", File.ReadAllText(sibling));
        Assert.False(File.Exists(zip));
        Assert.False(Directory.Exists(plan.Stage + ".extract"));
        Assert.False(File.Exists(plan.Backup));
        Assert.False(Directory.Exists(plan.Backup));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_ps51_publish_failure_restores_or_reports_retained_backup(bool failRollback)
    {
        var plan = PrepareFile();
        string script = PackagePublication.BuildPublishScript(Quote(plan.Source), false,
            plan.Target, plan.Stage, plan.Backup, false, PackagePublication.ManifestBase64(plan.Manifest));
        // Fault only the filesystem boundary; execute the generated recovery logic unchanged.
        const string publish = "Move-VivreExact $stage $target $folder";
        const string rollback = "Move-VivreExact $backup $target $folder";
        Assert.Contains(publish, script);
        script = script.Replace(publish, "throw 'injected publication failure'", StringComparison.Ordinal);
        if (failRollback)
        {
            Assert.Contains(rollback, script);
            script = script.Replace(rollback, "throw 'injected rollback failure'", StringComparison.Ordinal);
        }
        using JsonDocument result = await RunScript(script);
        Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(result.RootElement.GetProperty("safeToFallback").GetBoolean());
        Assert.False(File.Exists(plan.Stage));
        if (failRollback)
        {
            Assert.False(File.Exists(plan.Target));
            Assert.Equal("old package", File.ReadAllText(plan.Backup));
            Assert.Equal(plan.Backup, result.RootElement.GetProperty("recoveryPath").GetString());
        }
        else
        {
            Assert.Equal("old package", File.ReadAllText(plan.Target));
            Assert.False(File.Exists(plan.Backup));
        }
    }

    private async Task<JsonDocument> RunScript(string script)
    {
        string scriptPath = Path.Combine(_root, "publication.ps1");
        await File.WriteAllTextAsync(scriptPath, "& {\n" + script + "\n} | ConvertTo-Json -Compress", new UTF8Encoding(true));
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy"); start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File"); start.ArgumentList.Add(scriptPath);
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

    private static string Quote(string path) => "'" + path.Replace("'", "''") + "'";

    private PackagePublicationPlan PrepareFile()
    {
        string source = Path.Combine(_root, "source.bin");
        string target = Path.Combine(_root, "renamed.bin");
        File.WriteAllText(source, "good");
        File.WriteAllText(target, "old package");
        return PackagePublication.Prepare(source, false, target, localTarget: true);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
