using System.Diagnostics;
using System.Management.Automation;
using Vivre.Core.Deploy;
using Vivre.Core.Operations;
using Vivre.Core.PowerShell;

namespace Vivre.Core.Tests.Deploy;

public sealed class WinRmStagingLifecycleTests : IDisposable
{
    private const string Host = "NYC-FP1";
    private const string Target = @"C:\ProgramData\Vivre\packages\app.bin";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VivreWinRmLifecycle_" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _journalPath;
    private readonly string _registryPath;

    public WinRmStagingLifecycleTests()
    {
        Directory.CreateDirectory(_root);
        _source = Path.Combine(_root, "source.bin");
        File.WriteAllBytes(_source, Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray());
        _journalPath = Path.Combine(_root, "journal");
        _registryPath = Path.Combine(_root, "protected.json");
    }

    [Fact]
    public async Task Success_runs_all_transfer_phases_and_completes_journal()
    {
        var host = new LifecycleHost(_ => Ack());

        StageResult result = await Service(host).StageAsync(Host, _source, false, Target, null, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(new[] { "initialize", "chunk", "publication", "cleanup" }, host.RemotePhases);
        Assert.Empty(PendingRecords());
    }

    [Fact]
    public async Task Large_manifest_uses_bounded_commands_and_cleanup_retires_only_transfer_paths()
    {
        string folder = Path.Combine(_root, "many-files"); Directory.CreateDirectory(folder);
        for (int i = 0; i < 700; i++) File.WriteAllText(Path.Combine(folder, $"dummy-{i:D5}.txt"), "");
        var host = new LifecycleHost(_ => Ack());
        StageResult result = await Service(host).StageAsync(Host, folder, true, Target, null, default);
        Assert.True(result.Ok, result.Message);
        Assert.Contains(host.RemoteScripts, s => s.Contains("'manifest.json'") && s.Contains("# Vivre transfer chunk"));
        string publication = Assert.Single(host.RemoteScripts, s => s.Contains("# Vivre transfer publication"));
        Assert.True(publication.Length < 32 * 1024);
        Assert.Contains("transferred manifest SHA-256 mismatch", publication);
        Assert.All(host.RemoteScripts.Where(s => s.Contains("# Vivre transfer chunk") && s.Contains("'manifest.json'")), s => Assert.True(s.Length < 100 * 1024));
        var paths = new ProtectedStagingPathRegistry(_registryPath).GetProtectedPaths(Host);
        Assert.Contains(Target, paths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(paths, p => p.Contains(".vivre-transfer-"));
        // A fake success without the explicit cleanup verdict must keep candidate/backup protection.
        Assert.Contains(paths, p => p.Contains(".vivre-backup-"));
    }

    [Fact]
    public async Task Missing_chunk_ack_prevents_publication_and_still_cleans_up()
    {
        var host = new LifecycleHost(phase => phase == "chunk" ? Empty() : Ack());

        StageResult result = await Service(host).StageAsync(Host, _source, false, Target, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("write transfer chunk", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("publication", host.RemotePhases);
        Assert.Equal(new[] { "initialize", "chunk", "cleanup" }, host.RemotePhases);
        Assert.Empty(PendingRecords());
    }

    [Fact]
    public async Task Missing_initialize_ack_with_absent_cleanup_retains_journal_for_manual_verification()
    {
        var host = new LifecycleHost(phase => phase switch
        {
            "initialize" => Empty(),
            "cleanup" => Row(("ok", true), ("absent", true), ("message", "already absent")),
            _ => Ack(),
        });

        StageResult result = await Service(host).StageAsync(Host, _source, false, Target, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("initialize transfer", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("manual", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "initialize", "cleanup" }, host.RemotePhases);
        Assert.Single(PendingRecords());
    }

    [Fact]
    public async Task Cancellation_during_chunk_runs_non_cancelled_cleanup_and_completes_journal()
    {
        using var cancellation = new CancellationTokenSource();
        var host = new LifecycleHost((phase, token) =>
        {
            if (phase == "chunk")
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
            if (phase == "cleanup") Assert.False(token.IsCancellationRequested);
            return Ack();
        });

        OperationCanceledException error = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Service(host).StageAsync(Host, _source, false, Target, null, cancellation.Token));

        Assert.Contains("Staging cancelled", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "initialize", "chunk", "cleanup" }, host.RemotePhases);
        Assert.Empty(PendingRecords());
    }

    [Fact]
    public async Task Caller_cancellation_during_unfinished_initialize_returns_promptly_without_cleanup()
    {
        using var cancellation = new CancellationTokenSource();
        var initializeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateInitialize = new TaskCompletionSource<PSExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new LifecycleHost(_ => Ack())
        {
            AsyncRemote = (phase, token) =>
            {
                if (phase != "initialize") return Task.FromResult(Ack());
                Assert.False(token.IsCancellationRequested);
                initializeStarted.SetResult();
                return lateInitialize.Task;
            },
        };
        Task<StageResult> staging = Service(host).StageAsync(Host, _source, false, Target, null, cancellation.Token);
        await initializeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var elapsed = Stopwatch.StartNew();

        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => staging);

        elapsed.Stop();
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"Cancellation took {elapsed.Elapsed}.");
        Assert.Equal(new[] { "initialize" }, host.RemotePhases);
        Assert.Single(PendingRecords());
        lateInitialize.SetResult(Ack());
        await lateInitialize.Task;
    }

    [Fact]
    public async Task Unreachable_cleanup_remains_durable_and_is_retried_before_next_smb_success()
    {
        var first = new LifecycleHost(phase => phase == "cleanup"
            ? new PSExecutionResult([], ["host unreachable"], [], HadErrors: true)
            : Ack());

        StageResult firstResult = await Service(first).StageAsync(Host, _source, false, Target, null, CancellationToken.None);

        Assert.True(firstResult.Ok, firstResult.Message);
        Assert.Contains("cleanup remains pending", firstResult.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(PendingRecords());

        var second = new LifecycleHost(_ => Ack()) { SmbResult = Row(("ok", true), ("message", "SMB success")) };
        StageResult secondResult = await Service(second).StageAsync(Host, _source, false, Target, null, CancellationToken.None);

        Assert.True(secondResult.Ok, secondResult.Message);
        Assert.Equal(new[] { "remote:cleanup", "local:smb" }, second.Events);
        Assert.Empty(PendingRecords());
    }

    [Fact]
    public async Task Cleanup_transport_ignoring_cancellation_is_bounded_and_remains_pending()
    {
        var lateCleanup = new TaskCompletionSource<PSExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new LifecycleHost(_ => Ack())
        {
            AsyncRemote = (phase, _) => phase == "cleanup"
                ? lateCleanup.Task
                : Task.FromResult(Ack()),
        };
        var elapsed = Stopwatch.StartNew();

        StageResult result = await Service(host).StageAsync(Host, _source, false, Target, null, CancellationToken.None);

        elapsed.Stop();
        Assert.True(result.Ok, result.Message);
        Assert.Contains("cleanup remains pending", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(elapsed.Elapsed, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(15));
        Assert.Single(PendingRecords());
        lateCleanup.SetResult(Ack());
        await lateCleanup.Task;
    }

    [Fact]
    public async Task Active_journal_lease_is_not_retried_or_deleted()
    {
        var journal = new StagingTransferJournal(_journalPath);
        using StagingTransferJournal.Lease active = journal.Create(Host, Target);
        string record = PendingRecords().Single();
        var host = new LifecycleHost(_ => Ack()) { SmbResult = Row(("ok", true)) };

        StageResult result = await Service(host, journal).StageAsync(Host, _source, false, Target, null, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Empty(host.RemotePhases);
        Assert.True(File.Exists(record));
    }

    [Fact]
    public async Task Unknown_publication_response_reports_recovery_locations_and_cleans_transfer()
    {
        var host = new LifecycleHost(phase => phase == "publication" ? Empty() : Ack());

        StageResult result = await Service(host).StageAsync(Host, _source, false, Target, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("Publication outcome is unknown", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("candidate", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recovery backup", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "initialize", "chunk", "publication", "cleanup" }, host.RemotePhases);
        Assert.Empty(PendingRecords());
    }

    private DeploymentService Service(IPowerShellHost host, StagingTransferJournal? journal = null) => new(
        host,
        protectedPaths: new ProtectedStagingPathRegistry(_registryPath),
        transfers: journal ?? new StagingTransferJournal(_journalPath));

    private string[] PendingRecords() => Directory.Exists(_journalPath)
        ? Directory.GetFiles(_journalPath, "*.json")
        : [];

    private static PSExecutionResult Ack() => Row(("ok", true), ("message", "acknowledged"));
    private static PSExecutionResult Empty() => new([], [], [], HadErrors: false);

    private static PSExecutionResult Row(params (string Name, object? Value)[] properties)
    {
        var row = new PSObject();
        foreach ((string name, object? value) in properties) row.Properties.Add(new PSNoteProperty(name, value));
        return new PSExecutionResult([row], [], [], HadErrors: false);
    }

    private sealed class LifecycleHost : IPowerShellHost
    {
        private readonly Func<string, CancellationToken, PSExecutionResult> _remote;

        internal LifecycleHost(Func<string, PSExecutionResult> remote) : this((phase, _) => remote(phase)) { }
        internal LifecycleHost(Func<string, CancellationToken, PSExecutionResult> remote) => _remote = remote;

        internal PSExecutionResult SmbResult { get; init; } = Row(
            ("ok", false), ("safeToFallback", true), ("message", "SMB unavailable"));
        internal Func<string, CancellationToken, Task<PSExecutionResult>>? AsyncRemote { get; init; }
        internal List<string> RemotePhases { get; } = [];
        internal List<string> RemoteScripts { get; } = [];
        internal List<string> Events { get; } = [];

        public Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PSExecutionResult> RunLocalAsync(
            string script, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
        {
            Events.Add("local:smb");
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(SmbResult);
        }

        public Task<PSExecutionResult> RunRemoteAsync(
            string host, string script, PSCredential? credential = null, int port = 5985, bool useSsl = false,
            CancellationToken cancellationToken = default, bool background = false)
        {
            RemoteScripts.Add(script);
            string phase = Phase(script);
            RemotePhases.Add(phase);
            Events.Add("remote:" + phase);
            return AsyncRemote is null
                ? Task.FromResult(_remote(phase, cancellationToken))
                : AsyncRemote(phase, cancellationToken);
        }

        public Task<PSExecutionResult> RunRemoteStreamingAsync(
            string host, string script, Action<PSObject> onOutput, PSCredential? credential = null, int port = 5985,
            bool useSsl = false, CancellationToken cancellationToken = default, bool background = false) =>
            throw new NotSupportedException();

        private static string Phase(string script)
        {
            foreach (string phase in new[] { "initialize", "chunk", "publication", "cleanup" })
                if (script.Contains("# Vivre transfer " + phase, StringComparison.Ordinal)) return phase;
            throw new InvalidOperationException("Unrecognized transfer script.");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
