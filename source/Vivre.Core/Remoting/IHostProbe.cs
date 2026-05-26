using Vivre.Core.Credentials;

namespace Vivre.Core.Remoting;

/// <summary>
/// Authenticated reachability probe: confirms a host can be reached AND that the given
/// credential can authenticate to it, via a lightweight WMI/DCOM query. This is the
/// credential-aware complement to ICMP (<see cref="Net.IHostPinger"/>) — many servers block
/// ping but answer WMI, and (unlike ICMP) WMI requires valid admin credentials, which is why
/// reachability "only works under admin credentials" in locked-down environments.
/// </summary>
public interface IHostProbe
{
    /// <summary>
    /// Reachable when a minimal WMI query against <paramref name="host"/> succeeds using
    /// <paramref name="credential"/> (or the current Windows login when null). Unreachable or
    /// authentication-failed hosts return a non-reachable result carrying the reason (the
    /// meaningful outcome, not an error to throw); only cancellation throws.
    /// </summary>
    Task<ProbeResult> CanReachAsync(string host, ConnectionCredential? credential = null, CancellationToken cancellationToken = default);
}
