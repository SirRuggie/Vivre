using Vivre.Core.Net;
using Xunit;

namespace Vivre.Core.Tests.Net;

public class ReachabilityGatingTests
{
    // The load-bearing case-1-vs-case-2 rule: only a box that was online AND genuinely managed tracks
    // its return ("waiting for it to come back…"). Every other combination reads a calm "Offline".
    [Theory]
    [InlineData(true, true, true)]     // was online AND managed → track the return (case 2, reboot-wave)
    [InlineData(true, false, false)]   // was online but only ever pinged (BMC) → calm "Offline" (the fix)
    [InlineData(null, true, false)]    // never seen this session → calm "Offline"
    [InlineData(null, false, false)]   // never seen, never managed → calm "Offline"
    [InlineData(false, true, false)]   // already offline → no transition to track
    [InlineData(false, false, false)]  // already offline, never managed → calm "Offline"
    public void ShouldTrackOfflineReturn_only_when_was_online_and_managed(bool? previous, bool managed, bool expected) =>
        Assert.Equal(expected, ReachabilityGating.ShouldTrackOfflineReturn(previous, managed));

    [Theory]
    [InlineData(false, true)]   // confirmed offline → short-circuit the scan to a calm "Offline"
    [InlineData(true, false)]   // online (possibly ping-only) → run the scan; preserve the real WinRM/SMB error
    [InlineData(null, false)]   // never probed → do NOT short-circuit; caller must probe first
    public void ScanShouldShortCircuitOffline_only_when_confirmed_offline(bool? isOnline, bool expected) =>
        Assert.Equal(expected, ReachabilityGating.ScanShouldShortCircuitOffline(isOnline));

    [Theory]
    [InlineData(false, false, true)]   // ping down AND ambient DCOM down → skip the doomed probes, mark Offline
    [InlineData(false, true, false)]   // ping down but ambient DCOM UP (Kerberos-broken, DCOM-readable) → do NOT skip
    [InlineData(true, false, false)]   // ping up → do NOT skip
    [InlineData(true, true, false)]    // both up → do NOT skip
    public void ShouldSkipAsOffline_only_when_both_ping_and_ambient_dcom_fail(bool ping, bool dcom, bool expected) =>
        Assert.Equal(expected, ReachabilityGating.ShouldSkipAsOffline(ping, dcom));
}
