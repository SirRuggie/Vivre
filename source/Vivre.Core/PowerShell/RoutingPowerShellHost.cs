using System.Management.Automation;

namespace Vivre.Core.PowerShell;

/// <summary>
/// An <see cref="IPowerShellHost"/> decorator that owns the per-host transport decision. It wraps the
/// real <see cref="PSRunspaceHost"/> and, for remote calls, tries WinRM first; when a host rejects
/// Kerberos with <see cref="KerberosWrongPrincipalException"/> (0x80090322) it records the host as
/// <see cref="HostTransport.SmbDcom"/> in the shared <see cref="HostTransportCache"/>, so the doomed
/// WinRM connect is never attempted again for that host this session (the ~20s open timeout is paid
/// at most once). Healthy hosts are recorded <see cref="HostTransport.WinRm"/> and stay on the fast
/// one-round-trip primary.
/// <para>
/// SCOPE: this decorator only CACHES the per-host verdict and RETHROWS the typed exception — it does NOT
/// re-run the call over another transport. On a Kerberos rejection it records the host as SmbDcom so the
/// next call fast-fails (<c>CachedKerberosRejection</c>) instead of re-paying the ~20s WinRM open timeout,
/// then rethrows. Re-routing is the CALLER's job: the ops that HAVE a fallback catch the typed exception
/// and reroute (the WUA scan/install lane → the SMB agent; <c>VitalsProbe</c> → DCOM). Callers WITHOUT a
/// fallback — ConfigMgr client actions, the reboot probe, the software/custom-column probes, and the Run
/// Script feature — simply fail; they gate that failure with a plain, actionable message via
/// <see cref="RemoteFailureClassifier.IsWinRmUnavailable"/> rather than leaking raw SSPI text. The
/// multi-call WUA install/uninstall orchestration is not wrapped here at all — its transport selection
/// lives in the lane itself (<c>WuaUpdateLane</c>).
/// </para>
/// Local runs pass straight through — there is no transport choice for a local runspace.
/// </summary>
public sealed class RoutingPowerShellHost : IPowerShellHost
{
    private readonly IPowerShellHost _inner;
    private readonly HostTransportCache _cache;

    public RoutingPowerShellHost(IPowerShellHost inner, HostTransportCache cache)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken cancellationToken = default) =>
        _inner.RunLocalAsync(script, cancellationToken);

    // Forward the parameterized overload too — the SMB-copy deploy path runs a local script with a
    // bound PSCredential through this seam, so swallowing it into the interface default (which throws
    // NotSupportedException) would silently break that path.
    public Task<PSExecutionResult> RunLocalAsync(
        string script,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default) =>
        _inner.RunLocalAsync(script, arguments, cancellationToken);

    public async Task<PSExecutionResult> RunRemoteAsync(
        string host,
        string script,
        PSCredential? credential = null,
        int port = 5985,
        bool useSsl = false,
        CancellationToken cancellationToken = default)
    {
        if (_cache.Get(host) == HostTransport.SmbDcom)
        {
            throw CachedKerberosRejection(host);
        }

        try
        {
            PSExecutionResult result = await _inner
                .RunRemoteAsync(host, script, credential, port, useSsl, cancellationToken)
                .ConfigureAwait(false);
            _cache.MarkWinRm(host);
            return result;
        }
        catch (KerberosWrongPrincipalException)
        {
            _cache.MarkKerberosRejected(host);
            throw;
        }
    }

    public async Task<PSExecutionResult> RunRemoteStreamingAsync(
        string host,
        string script,
        Action<PSObject> onOutput,
        PSCredential? credential = null,
        int port = 5985,
        bool useSsl = false,
        CancellationToken cancellationToken = default)
    {
        if (_cache.Get(host) == HostTransport.SmbDcom)
        {
            throw CachedKerberosRejection(host);
        }

        try
        {
            PSExecutionResult result = await _inner
                .RunRemoteStreamingAsync(host, script, onOutput, credential, port, useSsl, cancellationToken)
                .ConfigureAwait(false);
            _cache.MarkWinRm(host);
            return result;
        }
        catch (KerberosWrongPrincipalException)
        {
            _cache.MarkKerberosRejected(host);
            throw;
        }
    }

    private static KerberosWrongPrincipalException CachedKerberosRejection(string host) =>
        new(host, new InvalidOperationException(
            "WinRM is disabled for this host this session — it rejected Kerberos with 0x80090322 earlier."));
}
