using System.Management.Automation;
using Vivre.Core.PowerShell;
using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// The lane selection in <see cref="WuaUpdateLane"/>: a host that rejects WinRM/Kerberos
/// (<see cref="KerberosWrongPrincipalException"/>) must transparently route Scan / Install / Uninstall to
/// the SMB + SCM lane, and a host that answers WinRM must NOT. A fake WinRM host raises the typed
/// exception (or returns success), and a recording <see cref="ISmbAgentLane"/> proves where the call went.
/// </summary>
public class SmbLaneSelectionTests
{
    private static readonly byte[] StubAgent = [1, 2, 3];
    private static readonly HostPatchStatus SmbSentinel = new(PatchPhase.Done, "ran on the SMB lane");

    [Fact]
    public async Task Scan_routes_to_the_smb_lane_on_kerberos_rejection()
    {
        var smb = new RecordingSmbLane();
        var lane = new WuaUpdateLane(new KerberosRejectingHost(), agentBytesProvider: () => StubAgent, smbLane: smb);

        HostPatchStatus result = await lane.ScanAsync("VISION-BOX", new PatchOptions(), credential: null, CancellationToken.None);

        Assert.Equal("Scan", smb.LastCall);
        Assert.Equal(PatchPhase.Done, result.Phase);
        Assert.Equal("ran on the SMB lane", result.Message);
    }

    [Fact]
    public async Task Install_routes_to_the_smb_lane_on_kerberos_rejection()
    {
        var smb = new RecordingSmbLane();
        var lane = new WuaUpdateLane(new KerberosRejectingHost(), agentBytesProvider: () => StubAgent, smbLane: smb);

        HostPatchStatus result = await lane.InstallAsync(
            "VISION-BOX", new PatchOptions(), credential: null, new SyncProgress([]), CancellationToken.None);

        Assert.Equal("Install", smb.LastCall);
        Assert.Equal("ran on the SMB lane", result.Message);
    }

    [Fact]
    public async Task Uninstall_routes_to_the_smb_lane_on_kerberos_rejection()
    {
        var smb = new RecordingSmbLane();
        var lane = new WuaUpdateLane(new KerberosRejectingHost(), agentBytesProvider: () => StubAgent, smbLane: smb);

        HostPatchStatus result = await lane.UninstallAsync(
            "VISION-BOX", new PatchOptions(), credential: null, new SyncProgress([]), CancellationToken.None);

        Assert.Equal("Uninstall", smb.LastCall);
        Assert.Equal("ran on the SMB lane", result.Message);
    }

    [Fact]
    public async Task A_healthy_winrm_scan_never_touches_the_smb_lane()
    {
        // The scan script returns "Up to date" (no rows, no errors) over WinRM — the SMB lane must stay idle.
        var smb = new RecordingSmbLane();
        var winrm = new HealthyScanHost();
        var lane = new WuaUpdateLane(winrm, agentBytesProvider: () => StubAgent, smbLane: smb);

        HostPatchStatus result = await lane.ScanAsync("HEALTHY-BOX", new PatchOptions(), credential: null, CancellationToken.None);

        Assert.Null(smb.LastCall);
        Assert.Equal(PatchPhase.Available, result.Phase);
    }

    private sealed class RecordingSmbLane : ISmbAgentLane
    {
        public string? LastCall { get; private set; }

        public Task<HostPatchStatus> ScanAsync(string host, PatchOptions options, CancellationToken cancellationToken)
        {
            LastCall = "Scan";
            return Task.FromResult(SmbSentinel);
        }

        public Task<HostPatchStatus> InstallAsync(string host, PatchOptions options, IProgress<HostPatchStatus> progress, CancellationToken cancellationToken)
        {
            LastCall = "Install";
            return Task.FromResult(SmbSentinel);
        }

        public Task<HostPatchStatus> UninstallAsync(string host, PatchOptions options, IProgress<HostPatchStatus> progress, CancellationToken cancellationToken)
        {
            LastCall = "Uninstall";
            return Task.FromResult(SmbSentinel);
        }
    }

    /// <summary>A WinRM host that rejects Kerberos on every remote call — both the scan's RunRemoteAsync
    /// and the install/uninstall RunRemoteStreamingAsync — exactly as RoutingPowerShellHost does for a
    /// 0x80090322 host.</summary>
    private sealed class KerberosRejectingHost : IPowerShellHost
    {
        public Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PSExecutionResult([], [], [], HadErrors: false));

        public Task<PSExecutionResult> RunRemoteAsync(
            string host, string script, PSCredential? credential = null, int port = 5985, bool useSsl = false,
            CancellationToken cancellationToken = default) =>
            throw new KerberosWrongPrincipalException(host, new InvalidOperationException("0x80090322"));

        public Task<PSExecutionResult> RunRemoteStreamingAsync(
            string host, string script, Action<PSObject> onOutput, PSCredential? credential = null, int port = 5985,
            bool useSsl = false, CancellationToken cancellationToken = default) =>
            throw new KerberosWrongPrincipalException(host, new InvalidOperationException("0x80090322"));
    }

    /// <summary>A WinRM host whose scan returns no rows and no errors (an "Up to date" box).</summary>
    private sealed class HealthyScanHost : IPowerShellHost
    {
        public Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PSExecutionResult([], [], [], HadErrors: false));

        public Task<PSExecutionResult> RunRemoteAsync(
            string host, string script, PSCredential? credential = null, int port = 5985, bool useSsl = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PSExecutionResult([], [], [], HadErrors: false));

        public Task<PSExecutionResult> RunRemoteStreamingAsync(
            string host, string script, Action<PSObject> onOutput, PSCredential? credential = null, int port = 5985,
            bool useSsl = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PSExecutionResult([], [], [], HadErrors: false));
    }

    private sealed class SyncProgress(List<HostPatchStatus> sink) : IProgress<HostPatchStatus>
    {
        public void Report(HostPatchStatus value)
        {
            lock (sink)
            {
                sink.Add(value);
            }
        }
    }
}
