using System.IO;
using System.Management.Automation;
using System.Security;
using System.Diagnostics;
using System.Text;
using Vivre.Core.Deploy;
using Vivre.Core.PowerShell;
using Xunit;

namespace Vivre.Core.Tests.Deploy;

/// <summary>
/// Covers the stage flow: copy a package onto a machine without running it, preferring the SMB admin
/// share and falling back to WinRM. The fake host routes the SMB copy (a parameterized local run) and
/// the WinRM chunk/finalize calls separately, so a test can model "SMB blocked → WinRM succeeds",
/// "both fail", etc. A local target is a pure file-system copy and is exercised with real temp files.
/// </summary>
public class DeploymentServiceTests : IDisposable
{
    private readonly string _registryRoot = Path.Combine(Path.GetTempPath(), $"VivreProtectedPathsTests_{Guid.NewGuid():N}");
    private readonly string _sourceMsi;
    private const string TargetFile = @"C:\Windows\Temp\VivrePackages\App.msi";
    private const string TargetFileParent = @"C:\Windows\Temp\VivrePackages";

    public DeploymentServiceTests()
    {
        Directory.CreateDirectory(_registryRoot);
        _sourceMsi = Path.Combine(_registryRoot, "App.msi");
        File.WriteAllText(_sourceMsi, "dummy installer bytes");
    }

    [Fact]
    public async Task Smb_success_reports_the_target_and_never_touches_winrm()
    {
        var host = new FakeHost { SmbResult = Row(("ok", true)) };

        StageResult r = await Service(host)
            .StageAsync("NYC-FP1", _sourceMsi, false, TargetFile, null, CancellationToken.None);

        Assert.True(r.Ok);
        Assert.Equal(TargetFile, r.Path);
        Assert.Contains("Staged and verified", r.Message);
        Assert.True(host.SmbCalled);
        Assert.False(host.RemoteCalled);   // SMB worked — no fallback
    }

    [Fact]
    public async Task Smb_copy_runs_locally_maps_the_share_and_passes_the_credential_as_a_parameter()
    {
        var ss = new SecureString();
        ss.AppendChar('x');
        ss.MakeReadOnly();
        var cred = new PSCredential(@"DOM\admin", ss);

        var host = new FakeHost { SmbResult = Row(("ok", true)) };
        await Service(host).StageAsync("NYC-FP1", _sourceMsi, false, TargetFile, cred, CancellationToken.None);

        // The copy runs on THIS machine (reaching the target over SMB), maps the admin share, copies.
        Assert.Contains("New-PSDrive", host.SmbScript);
        Assert.Contains("Copy-Item", host.SmbScript);
        // The credential is passed as a bound parameter, never interpolated into the script text.
        Assert.DoesNotContain("DOM\\admin", host.SmbScript);
        Assert.Same(cred, host.SmbArgs!["Cred"]);
        // It targets the admin-share UNC for the destination.
        Assert.Contains(@"\\NYC-FP1\C$\Windows\Temp\VivrePackages\App.msi", host.SmbScript);
        // The result must be a raw object (read via PSObject properties), NOT ConvertTo-Json — a JSON
        // string has no 'ok'/'message' properties, so piping through it makes every result misread.
        Assert.DoesNotContain("ConvertTo-Json", host.SmbScript);
    }

    [Fact]
    public async Task Smb_blocked_falls_back_to_winrm_and_succeeds()
    {
        string source = NewTempFile();
        try
        {
            var host = new FakeHost
            {
                SmbResult = Row(("ok", false), ("safeToFallback", true), ("message", "The network path was not found.")),
                RemoteResult = Row(("ok", true), ("path", TargetFileParent)),
            };

            StageResult r = await Service(host)
                .StageAsync("NYC-FP1", source, false, TargetFile, null, CancellationToken.None);

            Assert.True(r.Ok);
            Assert.Equal(TargetFile, r.Path);
            Assert.True(host.SmbCalled);
            Assert.True(host.RemoteCalled);   // fell back to WinRM
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public async Task Smb_and_winrm_both_fail_reports_both_causes()
    {
        string source = NewTempFile();
        try
        {
            var host = new FakeHost
            {
                SmbResult = Row(("ok", false), ("safeToFallback", true), ("message", "SMB port blocked")),
                RemoteResult = Row(("ok", false), ("message", "WinRM unhealthy")),
            };

            StageResult r = await Service(host)
                .StageAsync("NYC-FP1", source, false, TargetFile, null, CancellationToken.None);

            Assert.False(r.Ok);
            Assert.Null(r.Path);
            Assert.Contains("WinRM unhealthy", r.Message);
            Assert.Contains("SMB port blocked", r.Message);
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public async Task Winrm_fallback_chunks_the_payload_verifies_hash_and_expands_running_nothing()
    {
        string source = NewTempFile();
        try
        {
            var host = new FakeHost
            {
                SmbResult = Row(("ok", false), ("safeToFallback", true), ("message", "no admin share")),
                RemoteResult = Row(("ok", true), ("path", TargetFileParent)),
            };

            await Service(host).StageAsync("NYC-FP1", source, false, TargetFile, null, CancellationToken.None);

            string all = string.Join("\n----\n", host.RemoteScripts);
            Assert.Contains("FromBase64String", all);     // a chunk command
            Assert.Contains("FileMode]::Append", all);     // appends bytes
            Assert.Contains("Get-FileHash", all);          // finalize verifies
            Assert.Contains("Expand-Archive", all);        // finalize expands
            // A single file expands into the destination's PARENT (the file lands at the exact target).
            Assert.Contains(TargetFileParent, all);
            // Results are raw objects read via PSObject properties — never ConvertTo-Json (a JSON
            // string has no 'ok'/'path' properties, so it would make every result misread as failure).
            Assert.DoesNotContain("ConvertTo-Json", all);
            // Staging NEVER runs the payload — none of the install machinery appears.
            Assert.DoesNotContain("New-ScheduledTask", all);
            Assert.DoesNotContain("S-1-5-18", all);
            Assert.DoesNotContain("cmd.exe", all);
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public async Task Local_folder_replacement_is_exact_and_preserves_unrelated_siblings_and_empty_folders()
    {
        string root = Path.Combine(Path.GetTempPath(), $"VivreStageFolder_{Guid.NewGuid():N}");
        string source = Path.Combine(root, "source");
        string parent = Path.Combine(root, "dest");
        string target = Path.Combine(parent, "package");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "new.bin"), "new bytes");
        File.WriteAllText(Path.Combine(target, "obsolete.bin"), "old bytes");
        File.WriteAllText(Path.Combine(parent, "sibling.txt"), "keep");
        try
        {
            StageResult result = await Service(new FakeHost { ThrowIfCalled = true })
                .StageAsync("localhost", source, true, target, null, CancellationToken.None);
            Assert.True(result.Ok, result.Message);
            Assert.False(File.Exists(Path.Combine(target, "obsolete.bin")));
            Assert.Equal("new bytes", File.ReadAllText(Path.Combine(target, "new.bin")));
            Assert.True(Directory.Exists(Path.Combine(target, "empty")));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(parent, "sibling.txt")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Missing_source_fails_before_transport_and_preserves_destination()
    {
        string root = Path.Combine(Path.GetTempPath(), $"VivreStageMissing_{Guid.NewGuid():N}");
        string target = Path.Combine(root, "package.bin");
        Directory.CreateDirectory(root);
        File.WriteAllText(target, "old");
        try
        {
            StageResult result = await Service(new FakeHost { ThrowIfCalled = true })
                .StageAsync("localhost", Path.Combine(root, "missing.bin"), false, target, null, CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Equal("old", File.ReadAllText(target));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Local_single_file_replaces_exact_target_and_preserves_siblings()
    {
        string root = Path.Combine(Path.GetTempPath(), $"VivreStageFile_{Guid.NewGuid():N}");
        string source = Path.Combine(root, "incoming-name.bin");
        string target = Path.Combine(root, "renamed-target.msi");
        string sibling = Path.Combine(root, "other-package.msi");
        Directory.CreateDirectory(root);
        File.WriteAllText(source, "new"); File.WriteAllText(target, "old"); File.WriteAllText(sibling, "keep");
        try
        {
            StageResult result = await Service(new FakeHost { ThrowIfCalled = true })
                .StageAsync("localhost", source, false, target, null, CancellationToken.None);
            Assert.True(result.Ok, result.Message);
            Assert.Equal("new", File.ReadAllText(target));
            Assert.Equal("keep", File.ReadAllText(sibling));
            Assert.True(File.Exists(source));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Ambiguous_smb_result_does_not_retry_through_winrm()
    {
        var host = new FakeHost { SmbResult = new PSExecutionResult([], [], [], HadErrors: false) };
        StageResult result = await Service(host).StageAsync("NYC-FP1", _sourceMsi, false, TargetFile, null, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("unknown", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(host.RemoteCalled);
    }

    [Fact]
    public async Task Generated_publication_script_executes_under_windows_powershell_51()
    {
        string root = Path.Combine(Path.GetTempPath(), $"VivreStagePs51_{Guid.NewGuid():N}");
        string source = Path.Combine(root, "source");
        string target = Path.Combine(root, "target");
        string scriptPath = Path.Combine(root, "publish.ps1");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "new.txt"), "new-content");
        File.WriteAllText(Path.Combine(target, "obsolete.txt"), "old-content");
        try
        {
            PackagePublicationPlan plan = PackagePublication.Prepare(source, true, target, true);
            string sourceLiteral = "'" + source.Replace("'", "''") + "'";
            string script = PackagePublication.BuildPublishScript(
                sourceLiteral, false, plan.Target, plan.Stage, plan.Backup, true,
                PackagePublication.ManifestBase64(plan.Manifest));
            await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(true));
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            startInfo.Environment.Remove("PSModulePath");
            using var process = Process.Start(startInfo)!;
            string stderr = await process.StandardError.ReadToEndAsync();
            string stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, stderr + stdout);
            Assert.Contains("True", stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("new-content", File.ReadAllText(Path.Combine(target, "new.txt")));
            Assert.False(File.Exists(Path.Combine(target, "obsolete.txt")));
            Assert.True(Directory.Exists(Path.Combine(target, "empty")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Publish_failure_restores_previous_package_and_keeps_unrelated_sibling()
    {
        string root = Path.Combine(Path.GetTempPath(), $"VivreRollback_{Guid.NewGuid():N}");
        string source = Path.Combine(root, "source.bin"); string target = Path.Combine(root, "target.bin");
        string sibling = Path.Combine(root, "sibling.bin"); Directory.CreateDirectory(root);
        File.WriteAllText(source, "new"); File.WriteAllText(target, "old"); File.WriteAllText(sibling, "keep");
        try
        {
            PackagePublicationPlan plan = PackagePublication.Prepare(source, false, target, true);
            StageResult result = PackagePublication.PublishLocal(plan, CancellationToken.None,
                phase => { if (phase == "before-publish") throw new IOException("injected publish failure"); });
            Assert.False(result.Ok); Assert.Equal("old", File.ReadAllText(target)); Assert.Equal("keep", File.ReadAllText(sibling));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Rollback_failure_retains_named_backup_for_recovery()
    {
        string root = Path.Combine(Path.GetTempPath(), $"VivreRollbackFail_{Guid.NewGuid():N}");
        string source = Path.Combine(root, "source.bin"); string target = Path.Combine(root, "target.bin"); Directory.CreateDirectory(root);
        File.WriteAllText(source, "new"); File.WriteAllText(target, "old");
        PackagePublicationPlan plan = PackagePublication.Prepare(source, false, target, true);
        try
        {
            StageResult result = PackagePublication.PublishLocal(plan, CancellationToken.None, phase =>
            { if (phase is "before-publish" or "before-rollback") throw new IOException("injected failure"); });
            Assert.False(result.Ok); Assert.Contains(plan.Backup, result.Message); Assert.Equal("old", File.ReadAllText(plan.Backup));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Local_target_is_copied_on_disk_without_remoting()
    {
        string source = NewTempFile("the payload bytes");
        string destDir = Path.Combine(Path.GetTempPath(), $"VivreStageDest_{Guid.NewGuid():N}");
        string target = Path.Combine(destDir, "App.bin");
        try
        {
            // A throwing host proves the local path never reaches out over the network.
            var host = new FakeHost { ThrowIfCalled = true };

            StageResult r = await Service(host)
                .StageAsync("localhost", source, false, target, null, CancellationToken.None);

            Assert.True(r.Ok);
            Assert.Equal(target, r.Path);
            Assert.True(File.Exists(target));
            Assert.Equal("the payload bytes", await File.ReadAllTextAsync(target));
        }
        finally
        {
            File.Delete(source);
            if (Directory.Exists(destDir))
            {
                Directory.Delete(destDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var host = new FakeHost { SmbResult = Row(("ok", true)) };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Service(host).StageAsync("NYC-FP1", _sourceMsi, false, TargetFile, null, cts.Token));
    }

    // --- helpers -----------------------------------------------------------

    private DeploymentService Service(IPowerShellHost host) => new(
        host,
        protectedPaths: new Vivre.Core.Operations.ProtectedStagingPathRegistry(
            Path.Combine(_registryRoot, $"{Guid.NewGuid():N}.json")));

    public void Dispose()
    {
        if (Directory.Exists(_registryRoot)) Directory.Delete(_registryRoot, recursive: true);
    }

    private static string NewTempFile(string content = "PK fake zip source bytes")
    {
        string p = Path.Combine(Path.GetTempPath(), $"VivreStageTest_{Guid.NewGuid():N}.bin");
        File.WriteAllText(p, content);
        return p;
    }

    private static PSExecutionResult Row(params (string Name, object? Value)[] properties)
    {
        var o = new PSObject();
        foreach ((string name, object? value) in properties)
        {
            o.Properties.Add(new PSNoteProperty(name, value));
        }

        return new PSExecutionResult([o], [], [], HadErrors: false);
    }

    /// <summary>Fake host: the SMB copy arrives via the parameterized local run; the WinRM fallback's
    /// chunk + finalize arrive via the remote run. Records each so tests can assert routing + shape.</summary>
    private sealed class FakeHost : IPowerShellHost
    {
        public bool SmbCalled { get; private set; }

        public bool RemoteCalled { get; private set; }

        public string SmbScript { get; private set; } = string.Empty;

        public IReadOnlyDictionary<string, object?>? SmbArgs { get; private set; }

        public List<string> RemoteScripts { get; } = [];

        /// <summary>Returned by the SMB copy (the parameterized local run).</summary>
        public PSExecutionResult SmbResult { get; init; } = new([], [], [], HadErrors: false);

        /// <summary>Returned by every WinRM call (chunk + finalize).</summary>
        public PSExecutionResult RemoteResult { get; init; } = new([], [], [], HadErrors: false);

        /// <summary>When set, any host call throws — proves the local-copy path stays off the network.</summary>
        public bool ThrowIfCalled { get; init; }

        public Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Stage uses the parameterized local run for SMB, not this overload.");

        public Task<PSExecutionResult> RunLocalAsync(
            string script, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
        {
            if (ThrowIfCalled)
            {
                throw new InvalidOperationException("local stage must not reach out over the network");
            }

            SmbCalled = true;
            SmbScript = script;
            SmbArgs = arguments;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(SmbResult);
        }

        public Task<PSExecutionResult> RunRemoteAsync(
            string host, string script, PSCredential? credential = null, int port = 5985, bool useSsl = false,
            CancellationToken cancellationToken = default, bool background = false)
        {
            if (ThrowIfCalled)
            {
                throw new InvalidOperationException("local stage must not reach out over the network");
            }

            RemoteCalled = true;
            RemoteScripts.Add(script);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(RemoteResult);
        }

        public Task<PSExecutionResult> RunRemoteStreamingAsync(
            string host, string script, Action<PSObject> onOutput, PSCredential? credential = null, int port = 5985,
            bool useSsl = false, CancellationToken cancellationToken = default, bool background = false) =>
            throw new NotSupportedException("Stage tests don't exercise the streaming path.");
    }
}
