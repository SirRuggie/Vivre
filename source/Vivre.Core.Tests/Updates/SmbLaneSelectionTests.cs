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

    [Fact]
    public async Task Scan_falls_back_to_the_smb_lane_on_a_generic_session_loss()
    {
        // WinRM is down for a NON-Kerberos reason (RemoteSessionLostException). Scan is read-only, so it
        // must fall back to the SMB agent on ANY session loss — even a mid-run drop (AtConnect == false).
        var smb = new RecordingSmbLane();
        var lane = new WuaUpdateLane(new SessionLostHost(atConnect: false), agentBytesProvider: () => StubAgent, smbLane: smb);

        HostPatchStatus result = await lane.ScanAsync("WINRM-DOWN", new PatchOptions(), credential: null, CancellationToken.None);

        Assert.Equal("Scan", smb.LastCall);
        Assert.Equal(PatchPhase.Done, result.Phase);
        Assert.Equal("ran on the SMB lane", result.Message);
    }

    [Fact]
    public async Task Install_falls_back_to_the_smb_lane_on_a_connect_time_session_loss()
    {
        // The runspace never opened (AtConnect == true), so nothing was dropped/registered/started on the
        // box — it is safe to retry the install over the SMB agent.
        var smb = new RecordingSmbLane();
        var lane = new WuaUpdateLane(new SessionLostHost(atConnect: true), agentBytesProvider: () => StubAgent, smbLane: smb);

        HostPatchStatus result = await lane.InstallAsync(
            "WINRM-DOWN", new PatchOptions(), credential: null, new SyncProgress([]), CancellationToken.None);

        Assert.Equal("Install", smb.LastCall);
        Assert.Equal("ran on the SMB lane", result.Message);
    }

    [Fact]
    public async Task Install_does_NOT_fall_back_on_a_mid_run_session_loss()
    {
        // A drop AFTER the runspace opened (AtConnect == false) might leave an install already running on
        // the box — re-running over the SMB agent could double-apply, so it must NOT fall back.
        var smb = new RecordingSmbLane();
        var lane = new WuaUpdateLane(new SessionLostHost(atConnect: false), agentBytesProvider: () => StubAgent, smbLane: smb);

        HostPatchStatus result = await lane.InstallAsync(
            "WINRM-DROPPED", new PatchOptions(), credential: null, new SyncProgress([]), CancellationToken.None);

        Assert.Null(smb.LastCall);
        Assert.Equal(PatchPhase.Error, result.Phase);
        Assert.Contains("Lost connection", result.Message);
    }

    [Fact]
    public async Task A_scheduled_install_does_NOT_fall_back_even_at_connect_time()
    {
        // The SMB agent lane can't schedule (it runs immediately), so a scheduled install must never
        // silently fall back and lose the schedule — even on an otherwise-safe connect-time failure.
        var smb = new RecordingSmbLane();
        var lane = new WuaUpdateLane(new SessionLostHost(atConnect: true), agentBytesProvider: () => StubAgent, smbLane: smb);
        var options = new PatchOptions { RunBehavior = RunBehavior.ScheduleAt };

        HostPatchStatus result = await lane.InstallAsync(
            "WINRM-DOWN", options, credential: null, new SyncProgress([]), CancellationToken.None);

        Assert.Null(smb.LastCall);
        Assert.Equal(PatchPhase.Error, result.Phase);
        Assert.Contains("scheduling isn't available", result.Message);
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

        public Task<HostPatchStatus> InstallFullPackageAsync(string host, string sourcePackagePath, PatchOptions options, IProgress<HostPatchStatus> progress, CancellationToken cancellationToken)
        {
            LastCall = "InstallFullPackage";
            return Task.FromResult(SmbSentinel);
        }

        public Task<HostPatchStatus> RunComponentCleanupAsync(string host, PatchOptions options, IProgress<HostPatchStatus> progress, CancellationToken cancellationToken)
        {
            LastCall = "Cleanup";
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
            CancellationToken cancellationToken = default, bool background = false) =>
            throw new KerberosWrongPrincipalException(host, new InvalidOperationException("0x80090322"));

        public Task<PSExecutionResult> RunRemoteStreamingAsync(
            string host, string script, Action<PSObject> onOutput, PSCredential? credential = null, int port = 5985,
            bool useSsl = false, CancellationToken cancellationToken = default, bool background = false) =>
            throw new KerberosWrongPrincipalException(host, new InvalidOperationException("0x80090322"));
    }

    /// <summary>A WinRM host whose scan returns no rows and no errors (an "Up to date" box).</summary>
    private sealed class HealthyScanHost : IPowerShellHost
    {
        public Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PSExecutionResult([], [], [], HadErrors: false));

        public Task<PSExecutionResult> RunRemoteAsync(
            string host, string script, PSCredential? credential = null, int port = 5985, bool useSsl = false,
            CancellationToken cancellationToken = default, bool background = false) =>
            Task.FromResult(new PSExecutionResult([], [], [], HadErrors: false));

        public Task<PSExecutionResult> RunRemoteStreamingAsync(
            string host, string script, Action<PSObject> onOutput, PSCredential? credential = null, int port = 5985,
            bool useSsl = false, CancellationToken cancellationToken = default, bool background = false) =>
            Task.FromResult(new PSExecutionResult([], [], [], HadErrors: false));
    }

    /// <summary>A WinRM host that fails every remote call with the generic (NON-Kerberos)
    /// <see cref="RemoteSessionLostException"/> — "WinRM is down / the session dropped". <paramref name="atConnect"/>
    /// distinguishes a connect-time failure (the runspace never opened — nothing ran on the box) from a
    /// mid-run drop, exactly as <see cref="PSRunspaceHost"/>'s connect- vs execute-phase catches do.</summary>
    private sealed class SessionLostHost(bool atConnect) : IPowerShellHost
    {
        public Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PSExecutionResult([], [], [], HadErrors: false));

        public Task<PSExecutionResult> RunRemoteAsync(
            string host, string script, PSCredential? credential = null, int port = 5985, bool useSsl = false,
            CancellationToken cancellationToken = default, bool background = false) =>
            throw new RemoteSessionLostException(host, new Exception("the remote session ended"), atConnect);

        public Task<PSExecutionResult> RunRemoteStreamingAsync(
            string host, string script, Action<PSObject> onOutput, PSCredential? credential = null, int port = 5985,
            bool useSsl = false, CancellationToken cancellationToken = default, bool background = false) =>
            throw new RemoteSessionLostException(host, new Exception("the remote session ended"), atConnect);
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
