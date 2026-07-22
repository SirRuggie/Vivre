namespace Vivre.Core.Updates;

/// <summary>
/// Pure text helpers for the per-host "Reboot message" column (a field separate from the Windows-update
/// message — see <see cref="UpdateMessageText"/> for that one).
/// </summary>
public static class RebootMessageText
{
    /// <summary>
    /// True when the reboot-message column holds a transient PAST-EVENT notice that should be cleared once the
    /// row begins a new operation (scan / install / uninstall): "Reboot complete — back online …",
    /// "Back online …", or "Forced reboot sent …", plus the Reboot-Wave pre-reboot terminals ("… isn't
    /// reboot-ready — …", "… not rebooted …", "… nothing staged to commit …"). These have no other clearer, so
    /// they otherwise linger in the column across unrelated later operations — looking like a fresh reboot on a
    /// row that has moved on (the stale-reboot-message bug).
    /// </summary>
    /// <remarks>
    /// The CURRENT-STATE notices — "Offline since …" and "WinRM temporarily unavailable …" — are deliberately
    /// EXCLUDED: each already has its own condition-based clearer (cleared when the box pings online / WinRM
    /// recovers), so they must survive until that condition resolves. Prefix-matching mirrors those existing
    /// clearers' style; the set-site wording lives in <c>WorkspaceViewModel</c> and the wave state machine
    /// (<c>RebootWave</c>) — a different project — the strings carry interpolated time/duration so they can't be
    /// shared constants, so a regression test locks these against silent drift.
    /// <para>
    /// The pre-reboot wave terminals are matched by CONTAINS, not StartsWith: those messages are HOST-PREFIXED
    /// ("APVSQL1: servicing still running after 20 min — not rebooted; …"), so a StartsWith on the stable phrase
    /// can never fire. The fragments are the stable, host-independent middles: "isn't reboot-ready —" (the old
    /// hard-fail wording), "not rebooted" (the servicing-still-running, couldn't-confirm, and
    /// "nothing to commit; not rebooted" settle terminals — note the em-dash is intentionally NOT included so the
    /// "; not rebooted" variant is also caught), and "nothing staged to commit" (the definitively-nothing-staged
    /// "no reboot needed" terminal, which carries no "not rebooted").
    /// </para>
    /// </remarks>
    public static bool IsTransientRebootNotice(string? message) =>
        !string.IsNullOrEmpty(message)
        && (message.StartsWith("Reboot complete", StringComparison.Ordinal)
            || message.StartsWith("Back online", StringComparison.Ordinal)
            || message.StartsWith("Forced reboot sent", StringComparison.Ordinal)
            // Host-prefixed pre-reboot wave terminals — Contains (see remarks).
            || message.Contains("isn't reboot-ready —", StringComparison.Ordinal)
            || message.Contains("not rebooted", StringComparison.Ordinal)
            || message.Contains("nothing staged to commit", StringComparison.Ordinal));
}
