using System.Management.Automation;
using Vivre.Core.PowerShell;
using Xunit;

namespace Vivre.Core.Tests.PowerShell;

public class RoutingPowerShellHostTests
{
    // Fake inner host: counts remote calls and rejects Kerberos for a named set of hosts.
    private sealed class FakeInnerHost(params string[] kerberosHosts) : IPowerShellHost
    {
        private readonly HashSet<string> _kerberosHosts = new(kerberosHosts, StringComparer.OrdinalIgnoreCase);

        public int RemoteCalls { get; private set; }

        public int ParameterizedLocalCalls { get; private set; }

        public IReadOnlyDictionary<string, object?>? LastArguments { get; private set; }

        public Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken ct = default) =>
            Task.FromResult(new PSExecutionResult([], [], [], false));

        public Task<PSExecutionResult> RunLocalAsync(
            string script, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct = default)
        {
            ParameterizedLocalCalls++;
            LastArguments = arguments;
            return Task.FromResult(new PSExecutionResult([], [], [], false));
        }

        public Task<PSExecutionResult> RunRemoteAsync(
            string host, string script, PSCredential? credential = null, int port = 5985, bool useSsl = false,
            CancellationToken ct = default, bool background = false)
        {
            RemoteCalls++;
            if (_kerberosHosts.Contains(host))
            {
                throw new KerberosWrongPrincipalException(host, new Exception("errorcode 0x80090322"));
            }

            return Task.FromResult(new PSExecutionResult([], [], [], false));
        }

        public Task<PSExecutionResult> RunRemoteStreamingAsync(
            string host, string script, Action<PSObject> onOutput, PSCredential? credential = null, int port = 5985,
            bool useSsl = false, CancellationToken ct = default, bool background = false) =>
            RunRemoteAsync(host, script, credential, port, useSsl, ct, background);
    }

    [Fact]
    public async Task Healthy_host_uses_winrm_and_is_cached_winrm()
    {
        var cache = new HostTransportCache();
        var inner = new FakeInnerHost();
        var routing = new RoutingPowerShellHost(inner, cache);

        await routing.RunRemoteAsync("GOODBOX", "hostname");

        Assert.Equal(HostTransport.WinRm, cache.Get("GOODBOX"));
        Assert.False(cache.IsKerberosDegraded("GOODBOX"));
        Assert.Equal(1, inner.RemoteCalls);
    }

    [Fact]
    public async Task Healthy_host_keeps_using_winrm_on_subsequent_calls()
    {
        var cache = new HostTransportCache();
        var inner = new FakeInnerHost();
        var routing = new RoutingPowerShellHost(inner, cache);

        await routing.RunRemoteAsync("GOODBOX", "hostname");
        await routing.RunRemoteAsync("GOODBOX", "hostname");

        // The fast WinRM primary is used every time for a healthy host — no fast-fail short-circuit.
        Assert.Equal(2, inner.RemoteCalls);
        Assert.Equal(HostTransport.WinRm, cache.Get("GOODBOX"));
    }

    [Fact]
    public async Task Kerberos_rejection_flips_cache_to_smbdcom_and_sets_degraded()
    {
        var cache = new HostTransportCache();
        var inner = new FakeInnerHost("BADBOX");
        var routing = new RoutingPowerShellHost(inner, cache);

        await Assert.ThrowsAsync<KerberosWrongPrincipalException>(
            () => routing.RunRemoteAsync("BADBOX", "hostname"));

        Assert.Equal(HostTransport.SmbDcom, cache.Get("BADBOX"));
        Assert.True(cache.IsKerberosDegraded("BADBOX"));
    }

    [Fact]
    public async Task Cached_smbdcom_host_does_not_call_winrm_again()
    {
        var cache = new HostTransportCache();
        var inner = new FakeInnerHost("BADBOX");
        var routing = new RoutingPowerShellHost(inner, cache);

        // First call reaches inner WinRM (which rejects) and flips the cache.
        await Assert.ThrowsAsync<KerberosWrongPrincipalException>(
            () => routing.RunRemoteAsync("BADBOX", "hostname"));
        Assert.Equal(1, inner.RemoteCalls);

        // Subsequent remote + streaming calls must fail fast WITHOUT touching inner WinRM again.
        await Assert.ThrowsAsync<KerberosWrongPrincipalException>(
            () => routing.RunRemoteAsync("BADBOX", "hostname"));
        await Assert.ThrowsAsync<KerberosWrongPrincipalException>(
            () => routing.RunRemoteStreamingAsync("BADBOX", "hostname", _ => { }));

        Assert.Equal(1, inner.RemoteCalls); // still 1 — the doomed WinRM connect is never repeated
    }

    [Fact]
    public async Task Host_keys_are_case_insensitive()
    {
        var cache = new HostTransportCache();
        var inner = new FakeInnerHost("BadBox");
        var routing = new RoutingPowerShellHost(inner, cache);

        await Assert.ThrowsAsync<KerberosWrongPrincipalException>(
            () => routing.RunRemoteAsync("BadBox", "hostname"));
        // A differently-cased reference to the same host is also fast-failed.
        await Assert.ThrowsAsync<KerberosWrongPrincipalException>(
            () => routing.RunRemoteAsync("BADBOX", "hostname"));

        Assert.Equal(1, inner.RemoteCalls);
    }

    [Fact]
    public async Task Local_runs_pass_through()
    {
        var cache = new HostTransportCache();
        var inner = new FakeInnerHost();
        var routing = new RoutingPowerShellHost(inner, cache);

        PSExecutionResult result = await routing.RunLocalAsync("1 + 1");

        Assert.False(result.HadErrors);
        Assert.Equal(0, inner.RemoteCalls); // local path never touches the remote transport logic
    }

    [Fact]
    public async Task Parameterized_local_run_forwards_to_inner()
    {
        // The SMB-copy deploy path runs a local script with a bound PSCredential through this overload;
        // the decorator must forward it, NOT fall through to the interface default (which throws).
        var cache = new HostTransportCache();
        var inner = new FakeInnerHost();
        var routing = new RoutingPowerShellHost(inner, cache);
        var args = new Dictionary<string, object?> { ["Cred"] = "x" };

        await routing.RunLocalAsync("script", args);

        Assert.Equal(1, inner.ParameterizedLocalCalls);
        Assert.Same(args, inner.LastArguments);
    }

    [Fact]
    public void MarkWinRm_does_not_clobber_a_kerberos_rejection()
    {
        // TOCTOU fix: a late-arriving WinRM success must NOT downgrade a host already flipped to
        // SmbDcom (which would re-pay the doomed connect). The Kerberos rejection always wins.
        var cache = new HostTransportCache();

        cache.MarkKerberosRejected("H");
        cache.MarkWinRm("H"); // late success

        Assert.Equal(HostTransport.SmbDcom, cache.Get("H"));
        Assert.True(cache.IsKerberosDegraded("H"));
    }

    [Fact]
    public void Degraded_flag_tracks_transport_exactly()
    {
        // Single source of truth: degraded == routed SmbDcom, so they can never disagree.
        var cache = new HostTransportCache();

        Assert.False(cache.IsKerberosDegraded("H")); // Unknown
        cache.MarkWinRm("H");
        Assert.False(cache.IsKerberosDegraded("H")); // WinRm
        cache.MarkKerberosRejected("H");
        Assert.True(cache.IsKerberosDegraded("H"));  // SmbDcom
    }
}
