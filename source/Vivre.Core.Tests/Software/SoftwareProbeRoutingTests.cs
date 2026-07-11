using System.Management.Automation;
using Vivre.Core.PowerShell;
using Vivre.Core.Software;
using Xunit;

namespace Vivre.Core.Tests.Software;

public class SoftwareProbeRoutingTests
{
    private static KerberosWrongPrincipalException Kerberos() =>
        new("NYC-FP1", new InvalidOperationException("SEC_E_WRONG_PRINCIPAL/0x80090322"));

    [Fact]
    public async Task Kerberos_rejection_falls_back_to_dcom_and_surfaces_its_result()
    {
        var host = new ThrowingHost(Kerberos());
        var reader = new FakeReader { Result = new SoftwareCheckResult(true, "CrowdStrike Windows Sensor", "7.18.0", "Running") };

        SoftwareCheckResult r = await new SoftwareProbe(host, reader)
            .CheckAsync("NYC-FP1", "CrowdStrike", "CSFalconService", null, CancellationToken.None);

        Assert.True(reader.Called);
        Assert.True(r.Found);
        Assert.Equal("CrowdStrike Windows Sensor", r.Name);
        Assert.Equal("7.18.0", r.Version);
        Assert.Equal("Running", r.ServiceState);
    }

    [Fact]
    public async Task Kerberos_then_dcom_failure_throws_naming_both_transports_never_found_false()
    {
        var host = new ThrowingHost(Kerberos());
        var reader = new ThrowingReader(new SoftwareProbeException("StdRegProv GetStringValue returned 5"));

        SoftwareProbeException ex = await Assert.ThrowsAsync<SoftwareProbeException>(() =>
            new SoftwareProbe(host, reader).CheckAsync("NYC-FP1", "CrowdStrike", null, null, CancellationToken.None));

        // The message names BOTH transports so the operator sees the full picture — never a silent Found=false.
        Assert.Contains("Kerberos", ex.Message);
        Assert.Contains("DCOM", ex.Message);
    }

    [Fact]
    public async Task Kerberos_then_dcom_cancellation_propagates_unwrapped()
    {
        var host = new ThrowingHost(Kerberos());
        var reader = new ThrowingReader(new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SoftwareProbe(host, reader).CheckAsync("NYC-FP1", "CrowdStrike", null, null, CancellationToken.None));
    }

    [Fact]
    public async Task Kerberos_with_no_reader_propagates_unchanged()
    {
        var host = new ThrowingHost(Kerberos());

        await Assert.ThrowsAsync<KerberosWrongPrincipalException>(() =>
            new SoftwareProbe(host).CheckAsync("NYC-FP1", "CrowdStrike", null, null, CancellationToken.None));
    }

    [Fact]
    public async Task Healthy_winrm_never_calls_the_reader()
    {
        var host = new ThrowingHost(null) { Result = Row(("Found", true), ("Name", "Google Chrome"), ("Version", "120.0")) };
        var reader = new FakeReader { Result = new SoftwareCheckResult(false, null, null, null) };

        SoftwareCheckResult r = await new SoftwareProbe(host, reader)
            .CheckAsync("NYC-FP1", "Chrome", null, null, CancellationToken.None);

        Assert.False(reader.Called);
        Assert.True(r.Found);
        Assert.Equal("Google Chrome", r.Name);
    }

    [Fact]
    public async Task Non_kerberos_session_loss_does_not_reroute()
    {
        var host = new ThrowingHost(new RemoteSessionLostException("NYC-FP1", new InvalidOperationException("dropped")));
        var reader = new FakeReader { Result = new SoftwareCheckResult(true, "X", null, null) };

        await Assert.ThrowsAsync<RemoteSessionLostException>(() =>
            new SoftwareProbe(host, reader).CheckAsync("NYC-FP1", "X", null, null, CancellationToken.None));
        Assert.False(reader.Called);
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

    // A host that throws a configured exception from the remote call (to exercise the reroute seam), or
    // returns Result when constructed with no exception (the healthy path).
    private sealed class ThrowingHost : IPowerShellHost
    {
        private readonly Exception? _remoteError;

        public ThrowingHost(Exception? remoteError) => _remoteError = remoteError;

        public PSExecutionResult Result { get; init; } = new([], [], [], HadErrors: false);

        public Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result);

        public Task<PSExecutionResult> RunRemoteAsync(
            string host, string script, PSCredential? credential = null, int port = 5985, bool useSsl = false,
            CancellationToken cancellationToken = default, bool background = false) =>
            _remoteError is not null ? Task.FromException<PSExecutionResult>(_remoteError) : Task.FromResult(Result);

        public Task<PSExecutionResult> RunRemoteStreamingAsync(
            string host, string script, Action<PSObject> onOutput, PSCredential? credential = null, int port = 5985,
            bool useSsl = false, CancellationToken cancellationToken = default, bool background = false) =>
            throw new NotSupportedException();
    }

    private sealed class FakeReader : IDcomSoftwareReader
    {
        public bool Called { get; private set; }

        public SoftwareCheckResult Result { get; init; } = new(false, null, null, null);

        public Task<SoftwareCheckResult> CheckAsync(string host, string query, string? serviceName, CancellationToken ct)
        {
            Called = true;
            return Task.FromResult(Result);
        }
    }

    private sealed class ThrowingReader : IDcomSoftwareReader
    {
        private readonly Exception _error;

        public ThrowingReader(Exception error) => _error = error;

        public Task<SoftwareCheckResult> CheckAsync(string host, string query, string? serviceName, CancellationToken ct) =>
            Task.FromException<SoftwareCheckResult>(_error);
    }
}
