using System.Net.Sockets;
using Vivre.Core.Net;
using Xunit;

namespace Vivre.Core.Tests.Net;

public class PingErrorClassificationTests
{
    // The "name can't be resolved" family — a SocketException with one of these codes is a calm offline,
    // not a scary error. HostNotFound (11001) was confirmed by a live fleet probe 2026-07-16.
    [Theory]
    [InlineData(SocketError.HostNotFound)]  // 11001
    [InlineData(SocketError.NoData)]         // 11004
    [InlineData(SocketError.TryAgain)]       // 11002
    [InlineData(SocketError.NoRecovery)]     // 11003
    public void Name_resolution_socket_codes_classify_as_NameResolution(SocketError code) =>
        Assert.Equal(PingErrorKind.NameResolution, PingErrorClassification.Classify(new SocketException((int)code)));

    // Every other socket code is a real reachability failure (timeout, refused, unreachable, denied) —
    // never suppressed.
    [Theory]
    [InlineData(SocketError.TimedOut)]
    [InlineData(SocketError.ConnectionRefused)]
    [InlineData(SocketError.NetworkUnreachable)]
    [InlineData(SocketError.AccessDenied)]
    public void Other_socket_codes_classify_as_Other(SocketError code) =>
        Assert.Equal(PingErrorKind.Other, PingErrorClassification.Classify(new SocketException((int)code)));

    [Fact]
    public void Null_inner_classifies_as_Other() =>
        Assert.Equal(PingErrorKind.Other, PingErrorClassification.Classify(null));

    [Fact]
    public void Non_socket_exception_classifies_as_Other() =>
        Assert.Equal(PingErrorKind.Other, PingErrorClassification.Classify(new InvalidOperationException()));

    // Locks the never-suppress default: an offline result built without a kind is Other, so it can never
    // accidentally read as a calm name-resolution offline.
    [Fact]
    public void Offline_factory_defaults_ErrorKind_to_Other() =>
        Assert.Equal(PingErrorKind.Other, PingResult.Offline("x").ErrorKind);
}
