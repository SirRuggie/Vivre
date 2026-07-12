namespace Vivre.Core.Rdp;

/// <summary>What Vivre does with a session tab when its RDP connection ends.</summary>
public enum RdpDisconnectAction
{
    /// <summary>Deliberate sign-out — the session is gone by the user's own choice. Close the tab
    /// silently (like mstsc closing its window on logoff): no message, no error, no Reconnect.</summary>
    CloseTab,

    /// <summary>The session ended in a known non-error way (idle timeout, replaced connection, an
    /// admin disconnect that leaves the session ALIVE on the server, an API-driven end). Keep the
    /// tab with a plain, non-alarming message and the Reconnect button. The RDP control's
    /// <c>GetErrorDescription</c> must NOT be called for this outcome — calling it for a non-error
    /// disconnect is what produced the bogus "An internal error has occurred." on sign-out.</summary>
    KeepReconnectPlain,

    /// <summary>A genuine failure. Keep the tab, show the control's real reason
    /// (<c>GetErrorDescription</c> — called for THIS outcome only), and offer Reconnect.</summary>
    KeepReconnectError,
}

/// <summary>
/// Pure (no WPF, no OCX, no I/O) classifier that maps an RDP disconnect to the action Vivre takes.
/// <para>
/// <b>Keep-by-default, close-by-exception.</b> Losing a session the operator could have reconnected
/// to is worse than a spurious message, so the CLOSE list is a single-code allowlist and every
/// unknown or unmapped code KEEPS the tab. There is deliberately no "drop list" or "error list" to
/// maintain — untested codes are safe by construction.
/// </para>
/// <para>
/// <b>The enum-mismatch history (measured 2026-07-12, APVHOP):</b> the old inline check compared the
/// OCX's <c>ExtendedDisconnectReason</c> property against constants 2/4/6 whose comment labels came
/// from a different table. In the enum actually being read (<c>ExtendedDisconnectReasonCode</c>),
/// 2 = APIInitiatedLogoff, 4 = <b>ServerLogonTimeout</b> and 6 = <b>OutOfMemory</b> — so a real
/// sign-out (12 = LogoffByUser, measured identically for Start ▸ Sign out AND the `logoff` command)
/// showed a bogus error with a Reconnect button, while two genuine failures would have closed the
/// tab silently. Collapsing the two enums IS the bug — this classifier keys the close decision on
/// <paramref name="extendedDisconnectReason"/> alone.
/// </para>
/// </summary>
public static class RdpDisconnectClassifier
{
    /// <summary>ExtendedDisconnectReasonCode 12 (exDiscReasonLogoffByUser) — the ONLY code that
    /// closes the tab. Both deliberate sign-out paths (Start ▸ Sign out, `logoff`) measured 12.</summary>
    private const int LogoffByUser = 12;

    /// <summary>
    /// Classifies one disconnect. All inputs are read at the moment the control raises
    /// <c>OnDisconnected</c>.
    /// </summary>
    /// <param name="extendedDisconnectReason">The OCX's <c>ExtendedDisconnectReason</c> property as
    /// an int (<c>ExtendedDisconnectReasonCode</c>); pass a negative value when it couldn't be read —
    /// unreadable classifies as a kept error, never a close.</param>
    /// <param name="disconnectReason">The <c>OnDisconnected</c> event's <c>discReason</c> — a
    /// DIFFERENT enum from <paramref name="extendedDisconnectReason"/>; used only to distinguish
    /// plain from error when the extended reason carries no info, NEVER for the close decision.</param>
    /// <param name="connected">Whether login ever completed on this session. A disconnect before
    /// login (e.g. cancelling at the login screen can log off a partial session with code 12) keeps
    /// the tab so the operator can retry.</param>
    /// <param name="autoReconnectAttempts">Auto-reconnect attempts seen since the session was last
    /// live. A logoff code arriving mid-recovery is muddied context — keep the tab (a spurious open
    /// tab is cheap; a wrongly closed session is not).</param>
    public static RdpDisconnectAction Classify(
        int extendedDisconnectReason, int disconnectReason, bool connected, int autoReconnectAttempts)
    {
        if (extendedDisconnectReason == LogoffByUser && connected && autoReconnectAttempts == 0)
        {
            return RdpDisconnectAction.CloseTab;
        }

        // Known non-error session endings (ExtendedDisconnectReasonCode). NOTE 11
        // (RpcInitiatedDisconnectByUser, an admin `tsdiscon`) leaves the session ALIVE on the
        // server — Reconnect goes straight back into it; it must never close the tab.
        bool benignEnding = extendedDisconnectReason
            is 1   // exDiscReasonAPIInitiatedDisconnect
            or 2   // exDiscReasonAPIInitiatedLogoff
            or 3   // exDiscReasonServerIdleTimeout
            or 5   // exDiscReasonReplacedByOtherConnection
            or 11  // exDiscReasonRpcInitiatedDisconnectByUser — session still alive on the server
            or LogoffByUser; // a logoff that didn't qualify to close (pre-login / mid-auto-reconnect) is still not an error

        // No extended info at all: fall back to the control-level reason, where 1/2/3
        // (local / remote-by-user / server) are the documented non-error disconnects.
        bool benignNoInfo = extendedDisconnectReason == 0 && disconnectReason is 1 or 2 or 3;

        return benignEnding || benignNoInfo
            ? RdpDisconnectAction.KeepReconnectPlain
            : RdpDisconnectAction.KeepReconnectError;
    }

    /// <summary>
    /// The status-bar message for a classified disconnect. Total function: null for
    /// <see cref="RdpDisconnectAction.CloseTab"/> (the tab closes silently — there is nothing to
    /// show), the plain non-alarming line for a clean drop, and <paramref name="describeError"/>'s
    /// output for a genuine error. <paramref name="describeError"/> (the view wraps the OCX's
    /// <c>GetErrorDescription</c> in it) is invoked for the ERROR outcome ONLY — that contract is
    /// pinned by unit test, because calling it on a non-error disconnect is the original bug.
    /// </summary>
    public static string? Message(RdpDisconnectAction action, Func<string> describeError) => action switch
    {
        RdpDisconnectAction.CloseTab => null,
        RdpDisconnectAction.KeepReconnectPlain => "Disconnected — click Reconnect.",
        _ => describeError(),
    };
}
