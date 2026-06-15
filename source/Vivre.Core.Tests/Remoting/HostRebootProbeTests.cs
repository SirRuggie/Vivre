using System.Management.Automation;
using Vivre.Core.PowerShell;
using Vivre.Core.Remoting;
using Xunit;

namespace Vivre.Core.Tests.Remoting;

public class HostRebootProbeTests
{
    [Fact]
    public async Task IsRebootPendingAsync_returns_true_when_script_reports_true()
    {
        var probe = new HostRebootProbe(new FakeHost(MakeResult(rebootPending: true)));

        bool? r = await probe.IsRebootPendingAsync("NYC-SRV1");

        Assert.True(r);
    }

    [Fact]
    public async Task IsRebootPendingAsync_returns_false_when_script_reports_false()
    {
        var probe = new HostRebootProbe(new FakeHost(MakeResult(rebootPending: false)));

        bool? r = await probe.IsRebootPendingAsync("NYC-SRV1");

        Assert.False(r);
    }

    [Fact]
    public async Task IsRebootPendingAsync_returns_null_when_output_is_empty()
    {
        var probe = new HostRebootProbe(new FakeHost(new PSExecutionResult([], [], [], HadErrors: false)));

        bool? r = await probe.IsRebootPendingAsync("NYC-SRV1");

        Assert.Null(r);
    }

    [Fact]
    public async Task IsRebootPendingAsync_returns_null_when_output_lacks_property()
    {
        var bare = new PSObject();
        var probe = new HostRebootProbe(new FakeHost(new PSExecutionResult([bare], [], [], HadErrors: false)));

        bool? r = await probe.IsRebootPendingAsync("NYC-SRV1");

        Assert.Null(r);
    }

    private static PSExecutionResult MakeResult(bool rebootPending)
    {
        var o = new PSObject();
        o.Properties.Add(new PSNoteProperty("RebootPending", rebootPending));
        return new PSExecutionResult([o], [], [], HadErrors: false);
    }

    private sealed class FakeHost : IPowerShellHost
    {
        private readonly PSExecutionResult _result;

        public FakeHost(PSExecutionResult result) => _result = result;

        public Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken cancellationToken = default) =>
            Task.FromResult(_result);

        public Task<PSExecutionResult> RunRemoteAsync(
            string host,
            string script,
            PSCredential? credential = null,
            int port = 5985,
            bool useSsl = false,
            CancellationToken cancellationToken = default,
            bool background = false) =>
            Task.FromResult(_result);

        public Task<PSExecutionResult> RunRemoteStreamingAsync(
            string host,
            string script,
            Action<PSObject> onOutput,
            PSCredential? credential = null,
            int port = 5985,
            bool useSsl = false,
            CancellationToken cancellationToken = default,
            bool background = false) =>
            throw new NotSupportedException("HostRebootProbe tests don't exercise the streaming path.");
    }
}
