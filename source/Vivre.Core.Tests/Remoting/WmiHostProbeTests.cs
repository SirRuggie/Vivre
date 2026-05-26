using Vivre.Core.Remoting;
using Xunit;

namespace Vivre.Core.Tests.Remoting;

public class WmiHostProbeTests
{
    // The DCOM query needs a real reachable target, so only argument/cancellation behaviour is
    // unit-testable here; the live probe is verified manually against a real machine.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_host_is_not_reachable(string? host)
    {
        var probe = new WmiHostProbe();

        // A probe reports reachability rather than throwing on a blank host.
        Assert.False((await probe.CanReachAsync(host!)).Reachable);
    }

    [Fact]
    public async Task Cancelled_token_throws()
    {
        var probe = new WmiHostProbe();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => probe.CanReachAsync("SOME-HOST", cancellationToken: cts.Token));
    }
}
