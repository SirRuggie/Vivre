using System.Management.Automation;
using Vivre.Core.Deploy;
using Vivre.Core.Operations;
using Vivre.Core.PowerShell;
using Vivre.Core.Remediation;
using Vivre.Core.Updates;

namespace Vivre.Core.Tests.Operations;

public sealed class CleanupIntegrationTests
{
    [Fact]
    public async Task Cleanup_claim_blocks_all_conflicting_service_entries_before_transport()
    {
        var gate = new HostOperationCoordinator();
        using var cleanup = gate.TryBeginCleanup("SERVER01");
        var transport = new NeverRunHost();
        var patch = new PatchService(transport, operations: gate);
        var options = new PatchOptions();
        var progress = new Progress<HostPatchStatus>();
        var wave = new RebootWaveOptions(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
        HostPatchStatus[] results =
        [
            await patch.ScanAsync("server01", options, null),
            await patch.ScanAsync("server01", new PatchOptions { Scope = UpdateScope.Installed }, null),
            await patch.InstallAsync("server01", options, null, progress),
            await patch.UninstallAsync("server01", options, null, progress),
            await patch.StageLcuAsync("server01", @"C:\packages", new LcuTarget("KB1234567"), options, progress),
            await patch.ComponentCleanupLcuAsync("server01", options, progress),
            await patch.RebootWaveLcuAsync("server01", 1, wave, progress),
            await patch.RebootWaveWuaAsync("server01", wave, progress),
        ];
        Assert.All(results, r => Assert.Contains("Free up disk space", r.Message));
        var registry = new ProtectedStagingPathRegistry(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "paths.json"));
        var stage = await new DeploymentService(transport, gate, registry)
            .StageAsync("server01", @"C:\source.msi", false, @"C:\target.msi", null, default);
        Assert.False(stage.Ok);
        Assert.Contains("Free up disk space", stage.Message);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Two_detail_services_share_ownership_until_actual_cleanup_finishes_despite_cancellation()
    {
        var gate = new HostOperationCoordinator();
        var inner = new HeldCleanup();
        var registry = new ProtectedStagingPathRegistry(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "paths.json"));
        var first = new CoordinatedRemediationService(inner, gate, registry);
        var second = new CoordinatedRemediationService(inner, gate, registry);
        using var cancellation = new CancellationTokenSource();
        Task<DiskCleanupResult> running = first.FreeDiskSpaceAsync("server01", cancellationToken: cancellation.Token);
        cancellation.Cancel();
        Assert.False(running.IsCompleted);
        Assert.False(inner.Token.CanBeCanceled);
        Assert.False((await second.FreeDiskSpaceAsync("SERVER01")).Ok);
        using (var patch = gate.TryBeginOperation("server01", "Install")) Assert.False(patch.Acquired);
        using (var other = gate.TryBeginOperation("server02", "Install")) Assert.True(other.Acquired);
        inner.Finish.SetResult(new(DiskCleanupOutcome.Completed, 0, 50, "Complete"));
        Assert.True((await running).Ok);
        using var released = gate.TryBeginOperation("server01", "Install");
        Assert.True(released.Acquired);
        Assert.Equal(1, inner.Calls);
    }

    [Theory]
    [InlineData(DiskCleanupOutcome.PartiallyCompleted)]
    [InlineData(DiskCleanupOutcome.Failed)]
    public async Task Failed_recovery_blocks_next_operation_even_with_measurements(DiskCleanupOutcome outcome)
    {
        var gate = new HostOperationCoordinator();
        var inner = new HeldCleanup();
        var registry = new ProtectedStagingPathRegistry(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "paths.json"));
        var service = new CoordinatedRemediationService(inner, gate, registry);
        inner.Finish.SetResult(new(outcome, 1024, 50, "Windows Update service recovery failed") { SafeToReleaseHost = false });
        Assert.Equal(outcome, (await service.FreeDiskSpaceAsync("server01")).Outcome);
        using var patch = gate.TryBeginOperation("server01", "Install");
        using var cleanup = gate.TryBeginCleanup("server01");
        Assert.False(patch.Acquired);
        Assert.False(cleanup.Acquired);
    }

    [Fact]
    public async Task Active_patch_service_blocks_details_cleanup_before_cleanup_can_execute()
    {
        var gate = new HostOperationCoordinator();
        var transport = new HeldScanHost();
        var patch = new PatchService(transport, operations: gate);
        Task<HostPatchStatus> scanning = patch.ScanAsync("server01", new PatchOptions(), null);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var inner = new HeldCleanup();
        var registry = new ProtectedStagingPathRegistry(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "paths.json"));
        var cleanup = new CoordinatedRemediationService(inner, gate, registry);
        DiskCleanupResult result = await cleanup.FreeDiskSpaceAsync("SERVER01");
        Assert.False(result.Ok);
        Assert.Contains("Update scan", result.Message);
        Assert.Equal(0, inner.Calls);
        transport.Finish.SetResult(new PSExecutionResult([], [], [], false));
        await scanning;
    }

    private sealed class HeldScanHost : IPowerShellHost
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<PSExecutionResult> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PSExecutionResult> RunRemoteAsync(string host, string script, PSCredential? credential = null, int port = 5985, bool useSsl = false, CancellationToken cancellationToken = default, bool background = false)
        { Entered.TrySetResult(); return Finish.Task; }
        public Task<PSExecutionResult> RunRemoteStreamingAsync(string host, string script, Action<PSObject> onOutput, PSCredential? credential = null, int port = 5985, bool useSsl = false, CancellationToken cancellationToken = default, bool background = false) => throw new NotSupportedException();
    }

    private sealed class HeldCleanup : IRemediationService
    {
        public TaskCompletionSource<DiskCleanupResult> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public int Calls { get; private set; }
        public Task<DiskCleanupResult> FreeDiskSpaceAsync(string host, PSCredential? credential = null, CancellationToken cancellationToken = default, IReadOnlyCollection<string>? protectedPaths = null)
        { Calls++; Token = cancellationToken; return Finish.Task; }
        public Task<RemediationResult> StartServiceAsync(string host, string serviceDisplayName, PSCredential? credential = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProcessInfo>> GetTopProcessesAsync(string host, PSCredential? credential = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RemediationResult> EndProcessAsync(string host, int processId, PSCredential? credential = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NeverRunHost : IPowerShellHost
    {
        public int Calls { get; private set; }
        private Task<PSExecutionResult> Unexpected() { Calls++; throw new InvalidOperationException("Conflict guard allowed transport execution"); }
        public Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken cancellationToken = default) => Unexpected();
        public Task<PSExecutionResult> RunRemoteAsync(string host, string script, PSCredential? credential = null, int port = 5985, bool useSsl = false, CancellationToken cancellationToken = default, bool background = false) => Unexpected();
        public Task<PSExecutionResult> RunRemoteStreamingAsync(string host, string script, Action<PSObject> onOutput, PSCredential? credential = null, int port = 5985, bool useSsl = false, CancellationToken cancellationToken = default, bool background = false) => Unexpected();
    }
}
