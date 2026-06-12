using System.Net.Sockets;

namespace Vivre.Core.Updates;

/// <inheritdoc cref="IReachabilityProbe"/>
/// <remarks>
/// Probes reachability with a TCP connect rather than ICMP ping because SMB (port 445) is
/// reliably open on these managed boxes while ICMP echo may be blocked by the network or host
/// firewall. A successful TCP handshake is a strong "it's up and accessible" signal — the reboot
/// wave uses it to detect when a box has gone offline (reboot started) and when it has come back
/// (commit done, then Verify reads the UBR).
/// </remarks>
public sealed class TcpReachabilityProbe : IReachabilityProbe
{
    private readonly int _port;
    private readonly int _timeoutMs;

    /// <param name="port">TCP port to connect to. Defaults to 445 (SMB) — known-open on managed
    /// Windows boxes, unlike ICMP which firewalls frequently block.</param>
    /// <param name="timeoutMs">Connect timeout in milliseconds. Defaults to 3000 ms — long enough
    /// to survive a loaded host, short enough to keep the wave's poll loop responsive.</param>
    public TcpReachabilityProbe(int port = 445, int timeoutMs = 3000)
    {
        _port = port;
        _timeoutMs = timeoutMs;
    }

    /// <summary>Returns <see langword="true"/> when a TCP connection to <paramref name="host"/>
    /// on the configured port succeeds within the configured timeout, <see langword="false"/> on
    /// any failure (refused, timed out, DNS failure, cancelled). Never throws.</summary>
    public async Task<bool> IsReachableAsync(string host, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        try
        {
            using var client = new TcpClient();

            // Apply both the explicit timeout and the caller's CancellationToken. WaitAsync races
            // them against the connect; whichever fires first wins.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeoutMs);

            await client.ConnectAsync(host, _port, timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The timeout CTS fired, not the caller's token — treat as a normal "not reachable".
            return false;
        }
        catch
        {
            // Connection refused, DNS failure, host offline, or any other network error.
            // Reachability is binary: if we can't connect, the box isn't reachable.
            return false;
        }
    }
}
