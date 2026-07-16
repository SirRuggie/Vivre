using Vivre.Core.Net;
using Xunit;

namespace Vivre.Core.Tests.Net;

public class HostPingerTests
{
    private readonly HostPinger _pinger = new();

    [Fact]
    public async Task Loopback_is_online()
    {
        PingResult result = await _pinger.PingAsync("127.0.0.1", 2000);

        Assert.True(result.IsOnline);
        Assert.Null(result.Error);
        Assert.NotNull(result.RoundtripMs);
    }

    [Fact]
    public async Task Unresolvable_host_is_offline_and_does_not_throw()
    {
        PingResult result = await _pinger.PingAsync("unknown-host.invalid", 2000);

        Assert.False(result.IsOnline);
        Assert.NotNull(result.Error);
        Assert.Equal(PingErrorKind.NameResolution, result.ErrorKind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_host_is_offline(string? host)
    {
        PingResult result = await _pinger.PingAsync(host!, 2000);

        Assert.False(result.IsOnline);
    }

    [Fact]
    public async Task Already_cancelled_token_throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _pinger.PingAsync("127.0.0.1", 2000, cts.Token));
    }
}
