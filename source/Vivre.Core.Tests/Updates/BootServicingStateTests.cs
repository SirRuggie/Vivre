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

    [Theory]
    [InlineData("cbsRebootPending")]
    [InlineData("wuauRebootRequired")]
    public void Approved_immediate_install_may_ignore_only_reboot_pending_flags(string signal)
    {
        (bool busy, _) = EvaluateFor("Install", allow: true, signal);
        Assert.False(busy);
    }

    [Theory]
    [InlineData("cbsRebootInProgress")]
    [InlineData("pendingXmlExists")]
    [InlineData("cbsPackagesPending")]
    public void Approved_immediate_install_never_overrides_active_or_staged_servicing(string signal)
    {
        (bool busy, string reason) = EvaluateFor("Install", allow: true, signal);
        Assert.True(busy);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void Hard_block_wins_when_plain_pending_flags_are_also_present()
    {
        (bool busy, string reason) = BootServicingState.EvaluateForOperation(
            "Install", allowPendingRebootInstall: true,
            cbsRebootInProgress: false, pendingXmlExists: true, cbsPackagesPending: false,
            cbsRebootPending: true, wuauRebootRequired: true);

        Assert.True(busy);
        Assert.Contains("pending.xml", reason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Install")]
    [InlineData("Uninstall")]
    [InlineData("AddPackage")]
    [InlineData("Cleanup")]
    [InlineData(null)]
    public void Default_denial_blocks_reboot_pending_in_every_mutating_mode(string? mode)
    {
        (bool busy, _) = EvaluateFor(mode, allow: false, "cbsRebootPending");
        Assert.True(busy);
    }

    [Theory]
    [InlineData("Uninstall")]
    [InlineData("AddPackage")]
    [InlineData("Cleanup")]
    [InlineData("Scan")]
    [InlineData(null)]
    public void Approval_is_ignored_outside_normal_install(string? mode)
    {
        (bool busy, _) = EvaluateFor(mode, allow: true, "wuauRebootRequired");
        Assert.True(busy);
    }

    private static (bool Busy, string Reason) EvaluateFor(string? mode, bool allow, string signal) =>
        BootServicingState.EvaluateForOperation(
            mode!, allow,
            cbsRebootInProgress: signal == "cbsRebootInProgress",
            pendingXmlExists: signal == "pendingXmlExists",
            cbsPackagesPending: signal == "cbsPackagesPending",
            cbsRebootPending: signal == "cbsRebootPending",
            wuauRebootRequired: signal == "wuauRebootRequired");
}
