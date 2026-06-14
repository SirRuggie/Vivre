using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// The one classifier that decides which boxes are "2016" — it makes the panel self-populate and
/// "Install all" auto-route, so an unread box must come back unclassified (never guessed into the lane).
/// </summary>
public class LcuRoutingTests
{
    [Theory]
    [InlineData("Windows Server 2016 Standard — 10.0.14393", 14393)]
    [InlineData("Windows Server 2019 Datacenter — 10.0.17763", 17763)]
    [InlineData("Windows Server 2022 — 10.0.20348", 20348)]
    [InlineData("Microsoft Windows 11 Enterprise — 10.0.26100", 26100)]
    public void ParseBuild_pulls_the_build_from_the_os_string(string os, int expected) =>
        Assert.Equal(expected, LcuRouting.ParseBuild(os));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Some OS with no version")]
    public void ParseBuild_is_null_when_there_is_no_recognisable_build(string? os) =>
        Assert.Null(LcuRouting.ParseBuild(os));

    [Theory]
    [InlineData(14393, true)]   // Server 2016 → the LCU lane
    [InlineData(17763, false)]  // 2019
    [InlineData(20348, false)]  // 2022
    [InlineData(null, false)]   // unread → deliberately NOT 2016 (fail-safe)
    public void Is2016_is_true_only_for_a_confirmed_14393_build(int? build, bool expected) =>
        Assert.Equal(expected, LcuRouting.Is2016(build));

    [Theory]
    [InlineData(14393, RebootVerifyLane.Lcu2016)]   // Server 2016 → UBR-confirmed lane
    [InlineData(17763, RebootVerifyLane.Wua)]        // Server 2019 → WUA lane
    [InlineData(20348, RebootVerifyLane.Wua)]        // Server 2022 → WUA lane
    [InlineData(0,     RebootVerifyLane.Wua)]        // zero build → WUA (fail-safe)
    [InlineData(null,  RebootVerifyLane.Wua)]        // unread → WUA (fail-safe)
    public void RebootVerifyLaneFor_routes_14393_to_Lcu2016_everything_else_to_Wua(int? build, RebootVerifyLane expected) =>
        Assert.Equal(expected, LcuRouting.RebootVerifyLaneFor(build));
}
