using System.Management.Automation;
using Vivre.Core.PowerShell;
using Vivre.Core.Sccm;
using Xunit;

namespace Vivre.Core.Tests.Sccm;

public class ConfigMgrClientTests
{
    [Fact]
    public async Task Parses_a_healthy_client()
    {
        var host = new FakeHost(ResultFrom(HealthObject(version: "5.00.9132.1000", site: "PS1")));
        var client = new ConfigMgrClient(host);

        SccmClientInfo info = await client.GetClientHealthAsync("localhost");

        Assert.Equal("5.00.9132.1000", info.ClientVersion);
        Assert.Equal("PS1", info.SiteCode);
        Assert.True(info.IsHealthy);
        Assert.False(info.RebootRequired);
    }

    [Fact]
    public async Task Parses_an_unhealthy_client()
    {
        var obj = HealthObject(version: "5.00.9088.1000", site: "PS2", reboot: true, missing: true);
        var client = new ConfigMgrClient(new FakeHost(ResultFrom(obj)));

        SccmClientInfo info = await client.GetClientHealthAsync("localhost");

        Assert.True(info.RebootRequired);
        Assert.True(info.MissingUpdates);
        Assert.False(info.RunningUpdates);
        Assert.False(info.IsHealthy);
    }

    [Fact]
    public async Task Throws_when_no_client_data()
    {
        // No output + an error stream = target isn't a ConfigMgr client.
        var empty = new PSExecutionResult([], ["Get-CimInstance: Invalid namespace ROOT\\ccm"], [], HadErrors: true);
        var client = new ConfigMgrClient(new FakeHost(empty));

        SccmQueryException ex = await Assert.ThrowsAsync<SccmQueryException>(
            () => client.GetClientHealthAsync("localhost"));

        Assert.Contains("ROOT\\ccm", ex.Message);
    }

    [Fact]
    public async Task Local_host_runs_locally()
    {
        var host = new FakeHost(ResultFrom(HealthObject()));
        var client = new ConfigMgrClient(host);

        await client.GetClientHealthAsync("localhost");

        Assert.True(host.LocalCalled);
        Assert.False(host.RemoteCalled);
    }

    [Fact]
    public async Task Remote_host_runs_over_winrm()
    {
        var host = new FakeHost(ResultFrom(HealthObject()));
        var client = new ConfigMgrClient(host);

        await client.GetClientHealthAsync("NYC-FP1");

        Assert.True(host.RemoteCalled);
        Assert.False(host.LocalCalled);
    }

    [Fact]
    public async Task TriggerSchedule_returns_completion_message_and_sends_the_guid()
    {
        var action = new ScheduleAction("Machine Policy", "{00000000-0000-0000-0000-000000000021}", "Machine policy requested");
        var message = new PSObject("Machine policy requested");
        var host = new FakeHost(new PSExecutionResult([message], [], [], HadErrors: false));
        var client = new ConfigMgrClient(host);

        string result = await client.TriggerScheduleAsync("localhost", action);

        Assert.Equal("Machine policy requested", result);
        Assert.Contains(action.ScheduleId, host.LastScript);
        Assert.Contains("TriggerSchedule", host.LastScript);
    }

    [Fact]
    public async Task TriggerSchedule_throws_on_error()
    {
        var action = ClientActions.All[0];
        var denied = new PSExecutionResult([], ["TriggerSchedule: Access denied"], [], HadErrors: true);
        var client = new ConfigMgrClient(new FakeHost(denied));

        SccmQueryException ex = await Assert.ThrowsAsync<SccmQueryException>(
            () => client.TriggerScheduleAsync("localhost", action));

        Assert.Contains(action.Label, ex.Message);
        Assert.Contains("Access denied", ex.Message);
    }

    private static PSExecutionResult ResultFrom(PSObject row) =>
        new([row], [], [], HadErrors: false);

    private static PSObject HealthObject(
        string version = "5.00.9132.1000",
        string? site = "PS1",
        bool reboot = false,
        bool missing = false,
        bool running = false,
        bool user = false)
    {
        var o = new PSObject();
        o.Properties.Add(new PSNoteProperty("ClientVersion", version));
        o.Properties.Add(new PSNoteProperty("SiteCode", site));
        o.Properties.Add(new PSNoteProperty("RebootRequired", reboot));
        o.Properties.Add(new PSNoteProperty("MissingUpdates", missing));
        o.Properties.Add(new PSNoteProperty("RunningUpdates", running));
        o.Properties.Add(new PSNoteProperty("UserLoggedOn", user));
        return o;
    }

    private sealed class FakeHost : IPowerShellHost
    {
        private readonly PSExecutionResult _result;

        public FakeHost(PSExecutionResult result) => _result = result;

        public bool LocalCalled { get; private set; }

        public bool RemoteCalled { get; private set; }

        public string LastScript { get; private set; } = string.Empty;

        public Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken cancellationToken = default)
        {
            LocalCalled = true;
            LastScript = script;
            return Task.FromResult(_result);
        }

        public Task<PSExecutionResult> RunRemoteAsync(
            string host,
            string script,
            PSCredential? credential = null,
            int port = 5985,
            bool useSsl = false,
            CancellationToken cancellationToken = default,
            bool background = false)
        {
            RemoteCalled = true;
            LastScript = script;
            return Task.FromResult(_result);
        }

        public Task<PSExecutionResult> RunRemoteStreamingAsync(
            string host,
            string script,
            Action<PSObject> onOutput,
            PSCredential? credential = null,
            int port = 5985,
            bool useSsl = false,
            CancellationToken cancellationToken = default,
            bool background = false) =>
            throw new NotSupportedException("ConfigMgr tests don't exercise the streaming path.");
    }
}
