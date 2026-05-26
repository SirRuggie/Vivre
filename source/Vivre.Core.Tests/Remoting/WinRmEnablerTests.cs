using Vivre.Core.Remoting;
using Xunit;

namespace Vivre.Core.Tests.Remoting;

public class WinRmEnablerTests
{
    // The DCOM call needs a real reachable target, so only argument validation is
    // unit-testable here; the live enable is verified manually against a real machine.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_host_throws(string? host)
    {
        var enabler = new WinRmEnabler();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => enabler.EnableAsync(host!));
    }

    [Fact]
    public async Task Cancelled_token_throws()
    {
        var enabler = new WinRmEnabler();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => enabler.EnableAsync("SOME-HOST", cancellationToken: cts.Token));
    }
}
