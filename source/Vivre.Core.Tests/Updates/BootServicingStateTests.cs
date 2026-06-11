using Vivre.UpdateAgent;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Tests the agent's pure boot-busy decision (linked source — the agent itself is net48 and can't
/// be referenced from this net10 project). The I/O that gathers the signals lives in the agent's
/// BootBusyGuard and isn't unit-tested; the go/no-go logic is what matters here.
/// </summary>
public class BootServicingStateTests
{
    [Fact]
    public void No_signals_is_not_busy()
    {
        (bool busy, string reason) = BootServicingState.Evaluate(
            cbsRebootInProgress: false,
            pendingXmlExists: false,
            cbsPackagesPending: false,
            cbsRebootPending: false,
            wuauRebootRequired: false);

        Assert.False(busy);
        Assert.Equal(string.Empty, reason);
    }

    [Theory]
    [InlineData("cbsRebootInProgress")]
    [InlineData("pendingXmlExists")]
    [InlineData("cbsPackagesPending")]
    [InlineData("cbsRebootPending")]
    [InlineData("wuauRebootRequired")]
    public void Any_single_signal_short_circuits_to_busy(string signal)
    {
        (bool busy, string reason) = BootServicingState.Evaluate(
            cbsRebootInProgress: signal == "cbsRebootInProgress",
            pendingXmlExists: signal == "pendingXmlExists",
            cbsPackagesPending: signal == "cbsPackagesPending",
            cbsRebootPending: signal == "cbsRebootPending",
            wuauRebootRequired: signal == "wuauRebootRequired");

        Assert.True(busy);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void Servicing_in_progress_is_reported_ahead_of_a_plain_reboot_pending()
    {
        // When both a servicing op and a reboot-pending flag are set, the reason names the
        // in-progress servicing (the more urgent "do not touch the stack" signal).
        (bool busy, string reason) = BootServicingState.Evaluate(
            cbsRebootInProgress: true,
            pendingXmlExists: false,
            cbsPackagesPending: false,
            cbsRebootPending: true,
            wuauRebootRequired: false);

        Assert.True(busy);
        Assert.Contains("in progress", reason, System.StringComparison.OrdinalIgnoreCase);
    }
}
