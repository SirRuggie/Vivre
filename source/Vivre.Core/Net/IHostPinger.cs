namespace Vivre.Core.Net;

/// <summary>
/// Pings a host to determine reachability. Abstracted so the grid's ping sweep
/// can be unit-tested without real network I/O, and so a richer probe (TCP, WSMan)
/// can replace ICMP later without touching the view model.
/// </summary>
public interface IHostPinger
{
    /// <summary>
    /// Sends a single echo request. Network/resolution failures are returned as an
    /// offline <see cref="PingResult"/> (not thrown); only cancellation throws
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<PingResult> PingAsync(string host, int timeoutMs, CancellationToken cancellationToken = default);
}
