using System.Management.Automation;
using Vivre.Core.PowerShell;
using Vivre.Core.Software;
using Xunit;

namespace Vivre.Core.Tests.Software;

public class SoftwareProbeTests
{
    [Fact]
    public async Task Found_product_returns_name_and_version()
    {
        var host = new FakeHost { Result = Row(("Found", true), ("Name", "CrowdStrike Windows Sensor"), ("Version", "7.18.0")) };

        SoftwareCheckResult r = await new SoftwareProbe(host).CheckAsync("NYC-FP1", "CrowdStrike", null, null, CancellationToken.None);

        Assert.True(r.Found);
        Assert.Equal("CrowdStrike Windows Sensor", r.Name);
        Assert.Equal("7.18.0", r.Version);
        Assert.Null(r.ServiceState);
    }

    [Fact]
    public async Task Not_found_returns_found_false()
    {
        var host = new FakeHost { Result = Row(("Found", false), ("Name", null), ("Version", null)) };

        SoftwareCheckResult r = await new SoftwareProbe(host).CheckAsync("NYC-FP1", "CrowdStrike", null, null, CancellationToken.None);

        Assert.False(r.Found);
        Assert.Null(r.Name);
    }

    [Fact]
    public async Task Service_state_is_reported_when_a_service_is_requested()
    {
        var host = new FakeHost { Result = Row(("Found", true), ("Name", "CrowdStrike Windows Sensor"), ("Version", "7.18.0"), ("ServiceState", "Running")) };

        SoftwareCheckResult r = await new SoftwareProbe(host).CheckAsync("NYC-FP1", "CrowdStrike", "CSFalconService", null, CancellationToken.None);

        Assert.True(r.Found);
        Assert.Equal("Running", r.ServiceState);
        // The requested service name is embedded in the script (which actually queries services).
        Assert.Contains("Get-Service", host.Script);
        Assert.Contains("CSFalconService", host.Script);
    }

    [Fact]
    public async Task No_output_throws()
    {
        var host = new FakeHost { Result = new PSExecutionResult([], ["Connecting to remote server failed"], [], HadErrors: true) };

        SoftwareProbeException ex = await Assert.ThrowsAsync<SoftwareProbeException>(() =>
            new SoftwareProbe(host).CheckAsync("NYC-FP1", "CrowdStrike", null, null, CancellationToken.None));
        Assert.Contains("Connecting to remote server failed", ex.Message);
    }

    [Fact]
    public async Task Local_host_runs_locally_remote_over_winrm()
    {
        var local = new FakeHost { Result = Row(("Found", false)) };
        await new SoftwareProbe(local).CheckAsync("localhost", "X", null, null, CancellationToken.None);
        Assert.True(local.LocalCalled);
        Assert.False(local.RemoteCalled);

        var remote = new FakeHost { Result = Row(("Found", false)) };
        await new SoftwareProbe(remote).CheckAsync("NYC-FP1", "X", null, null, CancellationToken.None);
        Assert.True(remote.RemoteCalled);
        Assert.False(remote.LocalCalled);
    }

    [Fact]
    public async Task Script_searches_uninstall_keys_embeds_query_safely_and_emits_an_object()
    {
        var host = new FakeHost { Result = Row(("Found", false)) };
        // A name with an apostrophe must not break out of the single-quoted literal.
        await new SoftwareProbe(host).CheckAsync("NYC-FP1", "Bob's Agent", null, null, CancellationToken.None);

        Assert.Contains("Uninstall", host.Script);
        Assert.Contains("DisplayName", host.Script);
        Assert.Contains("Publisher", host.Script);   // matches on publisher too (brand name finds it)
        Assert.Contains("Bob''s Agent", host.Script);   // single quote doubled inside the literal
        // Read via PSObject properties, never ConvertTo-Json (a JSON string has no properties).
        Assert.DoesNotContain("ConvertTo-Json", host.Script);
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

    private sealed class FakeHost : IPowerShellHost
    {
        public bool LocalCalled { get; private set; }

        public bool RemoteCalled { get; private set; }

        public string Script { get; private set; } = string.Empty;

        public PSExecutionResult Result { get; init; } = new([], [], [], HadErrors: false);

        public Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken cancellationToken = default)
        {
            LocalCalled = true;
            Script = script;
            return Task.FromResult(Result);
        }

        public Task<PSExecutionResult> RunRemoteAsync(
            string host, string script, PSCredential? credential = null, int port = 5985, bool useSsl = false,
            CancellationToken cancellationToken = default, bool background = false)
        {
            RemoteCalled = true;
            Script = script;
            return Task.FromResult(Result);
        }

        public Task<PSExecutionResult> RunRemoteStreamingAsync(
            string host, string script, Action<PSObject> onOutput, PSCredential? credential = null, int port = 5985,
            bool useSsl = false, CancellationToken cancellationToken = default, bool background = false) =>
            throw new NotSupportedException();
    }
}
