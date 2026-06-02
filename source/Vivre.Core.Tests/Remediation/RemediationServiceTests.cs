using System.Management.Automation;
using Vivre.Core.PowerShell;
using Vivre.Core.Remediation;
using Xunit;

namespace Vivre.Core.Tests.Remediation;

public class RemediationServiceTests
{
    [Fact]
    public async Task StartService_parses_ok_result()
    {
        var host = new FakeHost(ResultFrom(Obj(("ok", true), ("message", "Started 'Print Spooler' (now Running)"))));
        var svc = new RemediationService(host);

        RemediationResult r = await svc.StartServiceAsync("localhost", "Print Spooler");

        Assert.True(r.Ok);
        Assert.Contains("Print Spooler", r.Message);
    }

    [Fact]
    public async Task StartService_parses_failure()
    {
        var host = new FakeHost(ResultFrom(Obj(("ok", false), ("message", "Access is denied"))));
        var svc = new RemediationService(host);

        RemediationResult r = await svc.StartServiceAsync("NYC-FP1", "Some Service");

        Assert.False(r.Ok);
        Assert.Equal("Access is denied", r.Message);
    }

    [Fact]
    public async Task StartService_no_output_is_failure_with_error_detail()
    {
        var host = new FakeHost(new PSExecutionResult([], ["Connecting to remote server failed"], [], HadErrors: true));
        var svc = new RemediationService(host);

        RemediationResult r = await svc.StartServiceAsync("NYC-FP1", "Some Service");

        Assert.False(r.Ok);
        Assert.Contains("Connecting to remote server failed", r.Message);
    }

    [Fact]
    public async Task StartService_escapes_single_quotes_to_block_injection()
    {
        var host = new FakeHost(ResultFrom(Obj(("ok", true), ("message", "ok"))));
        var svc = new RemediationService(host);

        await svc.StartServiceAsync("localhost", "Bob's Service");

        // The display name must reach the script single-quote-escaped (doubled), inside a literal.
        Assert.Contains("'Bob''s Service'", host.LastScript);
    }

    [Fact]
    public async Task FreeDiskSpace_parses_reclaimed_and_percent()
    {
        var host = new FakeHost(ResultFrom(Obj(
            ("ok", true), ("reclaimed", 1572864L), ("newFreePercent", 44.1), ("message", "Cleared TEMP"))));
        var svc = new RemediationService(host);

        DiskCleanupResult r = await svc.FreeDiskSpaceAsync("localhost");

        Assert.True(r.Ok);
        Assert.Equal(1572864L, r.ReclaimedBytes);
        Assert.Equal(44.1, r.NewFreePercent);
    }

    [Fact]
    public async Task TopProcesses_parses_rows_in_order()
    {
        var host = new FakeHost(new PSExecutionResult(
            [
                Obj(("name", "chrome"), ("id", 1234), ("wsMb", 850.5), ("cpu", 120.0)),
                Obj(("name", "sqlservr"), ("id", 42), ("wsMb", 512.0), ("cpu", 9999.0)),
            ],
            [], [], HadErrors: false));
        var svc = new RemediationService(host);

        IReadOnlyList<ProcessInfo> procs = await svc.GetTopProcessesAsync("NYC-FP1");

        Assert.Equal(2, procs.Count);
        Assert.Equal("chrome", procs[0].Name);
        Assert.Equal(1234, procs[0].Id);
        Assert.Equal(850.5, procs[0].WorkingSetMb);
        Assert.Equal(120.0, procs[0].CpuSeconds);
        Assert.Equal("sqlservr", procs[1].Name);
    }

    [Fact]
    public async Task EndProcess_embeds_pid_and_parses_result()
    {
        var host = new FakeHost(ResultFrom(Obj(("ok", true), ("message", "Ended chrome (PID 1234)"))));
        var svc = new RemediationService(host);

        RemediationResult r = await svc.EndProcessAsync("localhost", 1234);

        Assert.True(r.Ok);
        Assert.Contains("Get-Process -Id 1234", host.LastScript);
    }

    [Fact]
    public async Task Local_host_runs_locally_remote_over_winrm()
    {
        var local = new FakeHost(ResultFrom(Obj(("ok", true), ("message", "ok"))));
        await new RemediationService(local).StartServiceAsync("localhost", "X");
        Assert.True(local.LocalCalled);
        Assert.False(local.RemoteCalled);

        var remote = new FakeHost(ResultFrom(Obj(("ok", true), ("message", "ok"))));
        await new RemediationService(remote).StartServiceAsync("NYC-FP1", "X");
        Assert.True(remote.RemoteCalled);
        Assert.False(remote.LocalCalled);
    }

    private static PSExecutionResult ResultFrom(PSObject row) => new([row], [], [], HadErrors: false);

    private static PSObject Obj(params (string Name, object? Value)[] properties)
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
            string host, string script, PSCredential? credential = null, int port = 5985, bool useSsl = false,
            CancellationToken cancellationToken = default)
        {
            RemoteCalled = true;
            LastScript = script;
            return Task.FromResult(_result);
        }

        public Task<PSExecutionResult> RunRemoteStreamingAsync(
            string host, string script, Action<PSObject> onOutput, PSCredential? credential = null, int port = 5985,
            bool useSsl = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Remediation tests don't exercise the streaming path.");
    }
}
