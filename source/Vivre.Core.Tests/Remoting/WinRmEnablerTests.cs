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

    [Fact]
    public void Null_return_value_is_a_failure_not_success()
    {
        // Convert.ToUInt32(null) coerces to 0 — the old inline check read a never-populated
        // result code as a successful enable. Null must fail closed.
        var ex = Assert.Throws<WinRmEnableException>(
            () => WinRmEnabler.InterpretCreateReturn("SOME-HOST", null));

        Assert.Contains("no result code", ex.Message);
    }

    [Fact]
    public void Zero_return_value_is_success()
    {
        Assert.Equal(0u, WinRmEnabler.InterpretCreateReturn("SOME-HOST", 0u));
    }

    [Theory]
    [InlineData(2u, "access denied")]
    [InlineData(3u, "insufficient privilege")]
    [InlineData(8u, "unknown failure")]
    [InlineData(9u, "path not found")]
    [InlineData(21u, "invalid parameter")]
    public void Nonzero_return_value_throws_with_description(uint code, string description)
    {
        var ex = Assert.Throws<WinRmEnableException>(
            () => WinRmEnabler.InterpretCreateReturn("SOME-HOST", code));

        Assert.Contains(description, ex.Message);
    }

    [Fact]
    public void Non_uint_boxed_zero_still_reads_as_success()
    {
        // Convert handles other numeric boxes; only NULL throws the friendly message.
        Assert.Equal(0u, WinRmEnabler.InterpretCreateReturn("SOME-HOST", (ushort)0));
    }
}
