using Vivre.Core.Updates;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Unit tests for <see cref="TcpReachabilityProbe"/>. The DCOM readiness probe
/// (<see cref="DcomRebootReadinessProbe"/>) requires a live Windows box with DCOM accessible and
/// is not unit-tested here; it is validated by integration / manual testing against a real host.
/// </summary>
public class TcpReachabilityProbeTests
{
    /// <summary>A high-numbered local port that should not have anything listening under normal
    /// conditions. If this assumption ever breaks (some local service grabs 59999) the test would
    /// falsely pass — in practice this has never been an issue on Windows developer machines.</summary>
    private const int ClosedPort = 59999;

    /// <summary>Short connect timeout used in tests so the suite stays fast. 300 ms is long enough
    /// to get a TCP RST from a local closed port and short enough that a genuine timeout only
    /// adds 300 ms if something unexpected prevents a fast refusal.</summary>
    private const int FastTimeoutMs = 300;

    [Fact]
    public async Task Closed_local_port_returns_false_within_timeout()
    {
        var probe = new TcpReachabilityProbe(port: ClosedPort, timeoutMs: FastTimeoutMs);

        bool result = await probe.IsReachableAsync("127.0.0.1", CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task Cancelled_token_returns_false_or_throws_OCE_and_does_not_hang()
    {
        var probe = new TcpReachabilityProbe(port: ClosedPort, timeoutMs: FastTimeoutMs);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Per the spec either false or OperationCanceledException is acceptable — what matters
        // is that it completes promptly and does not hang.
        try
        {
            bool result = await probe.IsReachableAsync("127.0.0.1", cts.Token);
            Assert.False(result);
        }
        catch (OperationCanceledException)
        {
            // Also acceptable.
        }
    }
}
