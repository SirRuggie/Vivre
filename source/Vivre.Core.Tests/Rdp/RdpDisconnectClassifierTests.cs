using Vivre.Core.Rdp;
using Xunit;

namespace Vivre.Core.Tests.Rdp;

/// <summary>
/// Tests for <see cref="RdpDisconnectClassifier"/> — the keep-by-default / close-by-exception
/// contract. The close allowlist is a single code (ExtendedDisconnectReasonCode 12, LogoffByUser,
/// measured identically for Start ▸ Sign out and the `logoff` command); everything else keeps the
/// tab. Codes 4 and 6 are pinned HARD as keeps because the pre-fix code closed the tab silently on
/// them (the inverse bug), and unknown codes are pinned HARD as keeps (the fail-safe contract).
/// </summary>
public class RdpDisconnectClassifierTests
{
    // ── The close allowlist: exactly one code ────────────────────────────────

    [Theory]
    [InlineData(2)]  // discReason measured on both sign-out paths
    [InlineData(0)]
    [InlineData(3)]
    public void Signout_code12_connected_closes_regardless_of_discReason(int discReason)
    {
        RdpDisconnectAction action = RdpDisconnectClassifier.Classify(
            extendedDisconnectReason: 12, disconnectReason: discReason, connected: true, autoReconnectAttempts: 0);

        Assert.Equal(RdpDisconnectAction.CloseTab, action);
    }

    // ── The trap: 11 (admin tsdiscon) leaves the session ALIVE — never close ─

    [Fact]
    public void AdminDisconnect_code11_keeps_plain()
    {
        RdpDisconnectAction action = RdpDisconnectClassifier.Classify(
            extendedDisconnectReason: 11, disconnectReason: 2, connected: true, autoReconnectAttempts: 0);

        Assert.Equal(RdpDisconnectAction.KeepReconnectPlain, action);
    }

    // ── The inverse-bug regression guard: 4 and 6 were silent tab-closes ─────

    [Theory]
    [InlineData(4)]  // exDiscReasonServerLogonTimeout — NOT "server-initiated logoff"
    [InlineData(6)]  // exDiscReasonOutOfMemory — NOT "logoff-by-user"
    public void ServerLogonTimeout_and_OutOfMemory_keep_with_the_real_error(int extReason)
    {
        RdpDisconnectAction action = RdpDisconnectClassifier.Classify(
            extendedDisconnectReason: extReason, disconnectReason: 2, connected: true, autoReconnectAttempts: 0);

        Assert.NotEqual(RdpDisconnectAction.CloseTab, action);
        Assert.Equal(RdpDisconnectAction.KeepReconnectError, action);
    }

    // ── The fail-safe contract: unknown/unmapped codes NEVER close ───────────

    [Theory]
    [InlineData(7)]           // ServerDeniedConnection
    [InlineData(9)]           // ServerInsufficientPrivileges
    [InlineData(256)]         // license range
    [InlineData(4360)]        // protocol range
    [InlineData(int.MaxValue)]
    [InlineData(-1)]          // the view's "couldn't read the property" sentinel
    public void Unknown_or_unreadable_codes_keep_the_tab(int extReason)
    {
        RdpDisconnectAction action = RdpDisconnectClassifier.Classify(
            extendedDisconnectReason: extReason, disconnectReason: 2, connected: true, autoReconnectAttempts: 0);

        Assert.NotEqual(RdpDisconnectAction.CloseTab, action);
    }

    // ── The compound guards on the one close code ────────────────────────────

    [Fact]
    public void Signout_before_login_completed_does_not_close()
    {
        RdpDisconnectAction action = RdpDisconnectClassifier.Classify(
            extendedDisconnectReason: 12, disconnectReason: 2, connected: false, autoReconnectAttempts: 0);

        Assert.NotEqual(RdpDisconnectAction.CloseTab, action);
    }

    [Fact]
    public void Signout_mid_autoReconnect_does_not_close()
    {
        RdpDisconnectAction action = RdpDisconnectClassifier.Classify(
            extendedDisconnectReason: 12, disconnectReason: 2, connected: true, autoReconnectAttempts: 3);

        Assert.Equal(RdpDisconnectAction.KeepReconnectPlain, action);
    }

    // ── Plain vs error when the extended reason carries no info ──────────────

    [Theory]
    [InlineData(1)] // local, not an error
    [InlineData(2)] // remote by user
    [InlineData(3)] // by server
    public void NoInfo_with_benign_control_reason_keeps_plain(int discReason)
    {
        RdpDisconnectAction action = RdpDisconnectClassifier.Classify(
            extendedDisconnectReason: 0, disconnectReason: discReason, connected: true, autoReconnectAttempts: 0);

        Assert.Equal(RdpDisconnectAction.KeepReconnectPlain, action);
    }

    [Theory]
    [InlineData(516)]  // socket/timeout class
    [InlineData(2308)] // connection lost
    [InlineData(2825)] // the NLA/auth class the view appends its hint for
    public void NoInfo_with_error_control_reason_keeps_with_the_real_error(int discReason)
    {
        RdpDisconnectAction action = RdpDisconnectClassifier.Classify(
            extendedDisconnectReason: 0, disconnectReason: discReason, connected: true, autoReconnectAttempts: 0);

        Assert.Equal(RdpDisconnectAction.KeepReconnectError, action);
    }

    [Fact]
    public void IdleTimeout_keeps_plain()
    {
        RdpDisconnectAction action = RdpDisconnectClassifier.Classify(
            extendedDisconnectReason: 3, disconnectReason: 3, connected: true, autoReconnectAttempts: 0);

        Assert.Equal(RdpDisconnectAction.KeepReconnectPlain, action);
    }

    // ── Message: GetErrorDescription is reached for the ERROR outcome ONLY ───

    [Fact]
    public void Message_for_plain_keep_never_invokes_the_error_describer()
    {
        bool invoked = false;

        string? message = RdpDisconnectClassifier.Message(
            RdpDisconnectAction.KeepReconnectPlain, () => { invoked = true; return "boom"; });

        Assert.False(invoked);
        Assert.Equal("Disconnected — click Reconnect.", message);
    }

    [Fact]
    public void Message_for_close_is_null_and_never_invokes_the_error_describer()
    {
        bool invoked = false;

        string? message = RdpDisconnectClassifier.Message(
            RdpDisconnectAction.CloseTab, () => { invoked = true; return "boom"; });

        Assert.False(invoked);
        Assert.Null(message);
    }

    [Fact]
    public void Message_for_error_returns_the_describer_output()
    {
        string? message = RdpDisconnectClassifier.Message(
            RdpDisconnectAction.KeepReconnectError, () => "The real reason.");

        Assert.Equal("The real reason.", message);
    }
}
