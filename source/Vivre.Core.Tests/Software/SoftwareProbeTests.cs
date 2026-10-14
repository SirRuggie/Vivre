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
        var host = new FakeHost { Result = Row(("Found", false), ("Name", null), ("Version", null), ("SearchComplete", true)) };

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
        var local = new FakeHost { Result = Row(("Found", false), ("SearchComplete", true)) };
        await new SoftwareProbe(local).CheckAsync("localhost", "X", null, null, CancellationToken.None);
        Assert.True(local.LocalCalled);
        Assert.False(local.RemoteCalled);

        var remote = new FakeHost { Result = Row(("Found", false), ("SearchComplete", true)) };
        await new SoftwareProbe(remote).CheckAsync("NYC-FP1", "X", null, null, CancellationToken.None);
        Assert.True(remote.RemoteCalled);
        Assert.False(remote.LocalCalled);
    }

    [Fact]
    public async Task Script_searches_uninstall_keys_embeds_query_safely_and_emits_an_object()
    {
        var host = new FakeHost { Result = Row(("Found", false), ("SearchComplete", true)) };
        // A name with an apostrophe must not break out of the single-quoted literal.
        await new SoftwareProbe(host).CheckAsync("NYC-FP1", "Bob's Agent", null, null, CancellationToken.None);

        Assert.Contains("Uninstall", host.Script);
        Assert.Contains("DisplayName", host.Script);
        Assert.Contains("Publisher", host.Script);   // matches on publisher too (brand name finds it)
        Assert.Contains("Bob''s Agent", host.Script);   // single quote doubled inside the literal
        // Read via PSObject properties, never ConvertTo-Json (a JSON string has no properties).
        Assert.DoesNotContain("ConvertTo-Json", host.Script);
    }

    [Fact]
    public async Task Missing_completion_marker_never_becomes_not_installed()
    {
        var host = new FakeHost { Result = Row(("Found", false)) };

        SoftwareProbeException ex = await Assert.ThrowsAsync<SoftwareProbeException>(() =>
            new SoftwareProbe(host).CheckAsync("NYC-FP1", "CrowdStrike", null, null, CancellationToken.None));

        Assert.Contains("did not report completion", ex.Message);
    }

    [Fact]
    public async Task Proven_match_retains_query_and_service_failures()
    {
        var host = new FakeHost
        {
            Result = Row(
                ("Found", true),
                ("Name", "CrowdStrike Sensor"),
                ("Version", "7.18"),
                ("SearchComplete", false),
                ("QueryWarning", "32-bit uninstall path: access denied"),
                ("ServiceError", "Service query failed: provider unavailable"))
        };

        SoftwareCheckResult result = await new SoftwareProbe(host)
            .CheckAsync("NYC-FP1", "CrowdStrike", "CSFalconService", null, CancellationToken.None);

        Assert.True(result.Found);
        Assert.Equal("32-bit uninstall path: access denied", result.QueryWarning);
        Assert.Null(result.ServiceState);
        Assert.Equal("Service query failed: provider unavailable", result.ServiceError);
    }

    [Fact]
    public async Task Incomplete_search_without_match_throws_meaningful_reason()
    {
        var host = new FakeHost
        {
            Result = Row(
                ("Found", false),
                ("SearchComplete", false),
                ("QueryWarning", "Uninstall path could not be read: access denied"))
        };

        SoftwareProbeException ex = await Assert.ThrowsAsync<SoftwareProbeException>(() =>
            new SoftwareProbe(host).CheckAsync("NYC-FP1", "CrowdStrike", null, null, CancellationToken.None));

        Assert.Contains("access denied", ex.Message);
    }

    [Fact]
    public async Task Complete_marker_cannot_override_error_stream_for_absence()
    {
        var host = new FakeHost
        {
            Result = new PSExecutionResult(
                [ObjectRow(("Found", false), ("SearchComplete", true))],
                ["registry provider failed"], [], HadErrors: true)
        };

        SoftwareProbeException ex = await Assert.ThrowsAsync<SoftwareProbeException>(() =>
            new SoftwareProbe(host).CheckAsync("NYC-FP1", "CrowdStrike", null, null, CancellationToken.None));

        Assert.Contains("registry provider failed", ex.Message);
    }

    [Fact]
    public async Task Proven_match_preserves_error_stream_as_warning()
    {
        var host = new FakeHost
        {
            Result = new PSExecutionResult(
                [ObjectRow(("Found", true), ("Name", "CrowdStrike Sensor"), ("SearchComplete", true))],
                ["secondary provider failed"], [], HadErrors: true)
        };

        SoftwareCheckResult result = await new SoftwareProbe(host)
            .CheckAsync("NYC-FP1", "CrowdStrike", null, null, CancellationToken.None);

        Assert.True(result.Found);
        Assert.Contains("secondary provider failed", result.QueryWarning);
    }

    [Fact]
    public void Generated_script_complete_empty_stub_search_reports_trustworthy_absence()
    {
        PSObject row = RunGeneratedScript(
            "function Get-ChildItem { param($Path, $ErrorAction) @() }");

        Assert.False((bool)row.Properties["Found"].Value);
        Assert.True((bool)row.Properties["SearchComplete"].Value);
        Assert.Null(row.Properties["QueryWarning"].Value);
    }

    [Fact]
    public void Generated_script_both_missing_roots_cannot_prove_absence()
    {
        PSObject row = RunGeneratedScript(
            "function Get-ChildItem { param($Path, $ErrorAction) throw [System.Management.Automation.ItemNotFoundException]::new('missing') }");

        Assert.False((bool)row.Properties["Found"].Value);
        Assert.False((bool)row.Properties["SearchComplete"].Value);
    }

    [Fact]
    public void Generated_script_preserves_match_after_unrelated_hive_failure()
    {
        PSObject row = RunGeneratedScript(
            """
            function Get-ChildItem {
                param($Path, $ErrorAction)
                if ($Path -notlike '*WOW6432Node*') { throw [UnauthorizedAccessException]::new('access denied') }
                [PSCustomObject]@{ PSPath = 'stub:\product'; PSChildName = 'product' }
            }
            function Get-ItemProperty {
                param($LiteralPath, $ErrorAction)
                [PSCustomObject]@{ DisplayName = 'CrowdStrike Sensor'; DisplayVersion = '7.18'; Publisher = 'CrowdStrike' }
            }
            """);

        Assert.True((bool)row.Properties["Found"].Value);
        Assert.Equal("CrowdStrike Sensor", row.Properties["Name"].Value);
        Assert.False((bool)row.Properties["SearchComplete"].Value);
        Assert.Contains("access denied", row.Properties["QueryWarning"].Value?.ToString());
    }

    [Fact]
    public void Generated_script_distinguishes_service_failure_from_not_found()
    {
        PSObject row = RunGeneratedScript(
            """
            function Get-ChildItem { param($Path, $ErrorAction) @() }
            function Get-Service { param($ErrorAction) throw [InvalidOperationException]::new('provider unavailable') }
            """,
            "'FalconSvc'");

        Assert.Null(row.Properties["ServiceState"].Value);
        Assert.Contains("provider unavailable", row.Properties["ServiceError"].Value?.ToString());
    }

    private static PSObject RunGeneratedScript(string prelude, string serviceLiteral = "''")
    {
        using var ps = System.Management.Automation.PowerShell.Create();
        ps.AddScript(prelude + Environment.NewLine + SoftwareProbe.BuildScript("'CrowdStrike'", serviceLiteral));
        var output = ps.Invoke();
        Assert.Empty(ps.Streams.Error);
        return Assert.Single(output);
    }

    private static PSExecutionResult Row(params (string Name, object? Value)[] properties)
        => new([ObjectRow(properties)], [], [], HadErrors: false);

    private static PSObject ObjectRow(params (string Name, object? Value)[] properties)
    {
        var o = new PSObject();
        foreach ((string name, object? value) in properties)
        {
            o.Properties.Add(new PSNoteProperty(name, value));
        }

        return o;
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
