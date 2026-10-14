using System.Management.Automation;

namespace Vivre.Core.PowerShell;

/// <summary>
/// An <see cref="IPowerShellHost"/> decorator that owns each connection's transport decision. It wraps the
/// real <see cref="PSRunspaceHost"/> and, for remote calls, tries WinRM first; when a host rejects
/// Kerberos with <see cref="KerberosWrongPrincipalException"/> (0x80090322) it records the host as
/// <see cref="HostTransport.SmbDcom"/> in the shared <see cref="HostTransportCache"/>. Subsequent
/// ordinary calls suppress further attempts for that endpoint and credential. An explicit verified
/// retry can restore WinRM; changed credentials receive a fresh decision. Healthy connections use the fast
/// one-round-trip primary.
/// <para>
/// SCOPE: ordinary calls cache the connection verdict and rethrow the typed exception — they do NOT
/// re-run the call over another transport. On a Kerberos rejection it records the host as SmbDcom so the
/// next call fast-fails (<c>CachedKerberosRejection</c>) instead of re-paying the ~20s WinRM open timeout,
/// then rethrows. Re-routing is the CALLER's job: the ops that HAVE a fallback catch the typed exception
/// and reroute (the WUA scan/install lane → the SMB agent; <c>VitalsProbe</c> → DCOM; <c>SoftwareProbe</c>
/// → DCOM). Callers WITHOUT a fallback — ConfigMgr client actions, the reboot probe, the custom-column
/// probe, and the Run Script feature — simply fail; they gate that failure with a plain, actionable message via
/// <see cref="RemoteFailureClassifier.IsWinRmUnavailable"/> rather than leaking raw SSPI text. The
/// multi-call WUA install/uninstall orchestration is not wrapped here at all — its transport selection
/// lives in the lane itself (<c>WuaUpdateLane</c>).
/// </para>
/// Local runs pass straight through — there is no transport choice for a local runspace.
/// </summary>
public sealed class RoutingPowerShellHost : IPowerShellHost, IWinRmRecovery
{
    private const string RecoverySentinel = "VIVRE_WINRM_RECOVERY_7F4B2C19";
    private readonly IPowerShellHost _inner;
    private readonly HostTransportCache _cache;
    private readonly TimeSpan _retryTimeout;

    public RoutingPowerShellHost(IPowerShellHost inner, HostTransportCache cache, TimeSpan? retryTimeout = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _retryTimeout = retryTimeout ?? TimeSpan.FromSeconds(25);
        if (_retryTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryTimeout));
        }
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
        CancellationToken cancellationToken = default,
        bool background = false)
    {
        HostTransportCache.ConnectionKey key = _cache.KeyFor(host, credential, port, useSsl);
        HostTransportCache.NormalAttempt normal = _cache.BeginNormal(key);
        if (normal.Transport == HostTransport.SmbDcom)
        {
            throw CachedKerberosRejection(host);
        }

        long attempt = normal.Generation;
        try
        {
            PSExecutionResult result = await _inner
                .RunRemoteAsync(host, script, credential, port, useSsl, cancellationToken, background)
                .ConfigureAwait(false);
            _cache.TryCompleteNormal(key, attempt, HostTransport.WinRm);
            return result;
        }
        catch (KerberosWrongPrincipalException)
        {
            _cache.TryCompleteNormal(key, attempt, HostTransport.SmbDcom);
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
        CancellationToken cancellationToken = default,
        bool background = false)
    {
        HostTransportCache.ConnectionKey key = _cache.KeyFor(host, credential, port, useSsl);
        HostTransportCache.NormalAttempt normal = _cache.BeginNormal(key);
        if (normal.Transport == HostTransport.SmbDcom)
        {
            throw CachedKerberosRejection(host);
        }

        long attempt = normal.Generation;
        try
        {
            PSExecutionResult result = await _inner
                .RunRemoteStreamingAsync(host, script, onOutput, credential, port, useSsl, cancellationToken, background)
                .ConfigureAwait(false);
            _cache.TryCompleteNormal(key, attempt, HostTransport.WinRm);
            return result;
        }
        catch (KerberosWrongPrincipalException)
        {
            _cache.TryCompleteNormal(key, attempt, HostTransport.SmbDcom);
            throw;
        }
    }

    public async Task<WinRmRetryResult> RetryWinRmAsync(
        string host,
        PSCredential? credential = null,
        int port = 5985,
        bool useSsl = false,
        CancellationToken cancellationToken = default)
    {
        HostTransportCache.ConnectionKey key = _cache.KeyFor(host, credential, port, useSsl);
        if (cancellationToken.IsCancellationRequested)
        {
            return new WinRmRetryResult(WinRmRetryOutcome.Cancelled, "WinRM retry was cancelled.");
        }

        long attempt = _cache.BeginRecovery(key);
        using var innerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken innerToken = innerCancellation.Token;
        Task<PSExecutionResult> check = Task.Run(() =>
        {
            // Cancellation can win while this work waits for a pool thread. Avoid touching the
            // disposed CTS or dispatching a connection after its caller has stopped waiting.
            innerToken.ThrowIfCancellationRequested();
            return _inner.RunRemoteAsync(
                host, $"'{RecoverySentinel}'", credential, port, useSsl,
                innerToken, background: false);
        }, CancellationToken.None);
        _ = check.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            PSExecutionResult result = await check.WaitAsync(_retryTimeout, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                innerCancellation.Cancel();
                return new WinRmRetryResult(WinRmRetryOutcome.Cancelled, "WinRM retry was cancelled.");
            }
            bool verified = !result.HadErrors
                && result.Errors.Count == 0
                && result.Output.Count == 1
                && string.Equals(result.Output[0]?.ToString(), RecoverySentinel, StringComparison.Ordinal);
            if (!verified)
            {
                return new WinRmRetryResult(WinRmRetryOutcome.Failed,
                    result.Errors.Count > 0
                        ? $"WinRM verification failed: {result.Errors[0]}"
                        : result.HadErrors
                            ? "WinRM verification reported an error without diagnostic details."
                            : "WinRM connected but the harmless verification sentinel was missing.");
            }

            if (!_cache.TryCompleteRecovery(key, attempt, HostTransport.WinRm))
            {
                return new WinRmRetryResult(WinRmRetryOutcome.Failed,
                    "A newer WinRM check completed first; this older result was ignored.");
            }

            return new WinRmRetryResult(WinRmRetryOutcome.Succeeded, "WinRM connection verified.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            innerCancellation.Cancel();
            return new WinRmRetryResult(WinRmRetryOutcome.Cancelled, "WinRM retry was cancelled.");
        }
        catch (TimeoutException)
        {
            innerCancellation.Cancel();
            return new WinRmRetryResult(WinRmRetryOutcome.TimedOut,
                $"WinRM retry did not finish within {_retryTimeout.TotalSeconds:0.#} seconds.");
        }
        catch (Exception ex)
        {
            return FailedRetry(key, attempt, ex);
        }
    }

    private WinRmRetryResult FailedRetry(
        HostTransportCache.ConnectionKey key, long attempt, Exception ex)
    {
        if (ex is KerberosWrongPrincipalException)
        {
            _cache.TryCompleteRecovery(key, attempt, HostTransport.SmbDcom);
        }

        return new WinRmRetryResult(WinRmRetryOutcome.Failed, ex.Message);
    }

    private static KerberosWrongPrincipalException CachedKerberosRejection(string host) =>
        new(host, new InvalidOperationException(
            "WinRM is suppressed for this connection because it rejected Kerberos earlier. Use Retry WinRM to verify it again."));
}
