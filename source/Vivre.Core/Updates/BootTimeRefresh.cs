using Vivre.Core.Models;

namespace Vivre.Core.Updates;

/// <summary>
/// Re-reads one row's <see cref="Computer.LastBootTime"/> (the grid's "Last reboot" column) from the
/// target and writes the answer — including a BLANK when the read fails. The background monitor calls
/// this at the single moment a box is seen to transition offline → online, which is the one point that
/// covers BOTH the Reboot &amp; Verify wave and a one-off Force reboot and is not gated to the Patching tab.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists:</b> <see cref="Computer.LastBootTime"/> is only ever written by Check All / Check
/// Vitals. No reboot path rewrote it, so after a box rebooted and came back the cell kept showing the
/// PREVIOUS boot — the one thing an operator watching a reboot looks at.
/// </para>
/// <para>
/// <b>A failed or unreadable read BLANKS the cell.</b> This is deliberately the OPPOSITE of the vitals
/// path, which preserves a previously-known boot time when a partial read can't see it. That guard is
/// right there and wrong here: this refresh runs ONLY when we have just watched the box go away and come
/// back, so the value we are holding is known-suspect. A stale "Last reboot" on a box that demonstrably
/// just rebooted is a lie the operator will act on; an empty cell is honest and self-corrects on the next
/// successful read (the next Check All / Check Vitals, or the next transition). Do NOT copy the vitals
/// null-guard into this method.
/// </para>
/// <para>
/// <b>Thread affinity is load-bearing and NOTHING asserts it.</b> The caller (the WPF monitor loop) runs
/// on the UI SynchronizationContext and relies on never losing it; the DEBUG off-thread tripwire in
/// <see cref="Computer"/> covers only the live-filtered properties (<c>RebootRequired</c> /
/// <c>UpdatePhase</c>), so an off-thread <see cref="Computer.LastBootTime"/> write would fail SILENTLY.
/// The await below therefore must NOT use <c>ConfigureAwait(false)</c> — despite the rest of Vivre.Core
/// doing so — because the write that follows it is data-bound state. A regression test pumps a real
/// <see cref="SynchronizationContext"/> and asserts the write lands back on it.
/// </para>
/// <para>
/// <b>Cancellation</b> (the monitor being stopped) propagates and leaves the cell untouched: a stopped
/// monitor learned nothing, so it must not blank anything. Every other failure is absorbed —
/// <see cref="IBootTimeReader"/>'s contract is that failure returns <c>null</c>, so a THROW is a contract
/// violation worth surfacing through <paramref name="onUnexpectedError"/> rather than swallowing, but it
/// must not abort the monitor pass.
/// </para>
/// </remarks>
public static class BootTimeRefresh
{
    /// <summary>
    /// Reads <paramref name="computer"/>'s boot time through <paramref name="reader"/> and stores it in
    /// <see cref="Computer.LastBootTime"/>, storing <c>null</c> (a blank cell) when the read fails.
    /// </summary>
    /// <param name="reader">The boot-time reader — ambient-DCOM in production, so it also works on the
    /// Kerberos-broken boxes a WinRM probe can never reach.</param>
    /// <param name="computer">The row to refresh; its <see cref="Computer.Name"/> is the target host.</param>
    /// <param name="cancellationToken">The monitor's token. Cancellation propagates and writes nothing.</param>
    /// <param name="onUnexpectedError">Optional sink for a reader that THREW (a contract violation — the
    /// documented failure mode is a null result). The cell is still blanked.</param>
    /// <param name="onUnreadable">Optional sink fired whenever the cell ends up BLANK — i.e. the reader
    /// returned null (its documented failure mode) or threw. Without this the expected failure is completely
    /// silent: <c>DcomBootTimeReader</c> swallows offline / still-booting / DCOM-not-up / denied into a bare
    /// <c>return null</c>, so nothing anywhere records that the read was attempted and failed. Blanking is
    /// unchanged — this only makes the blank explainable after the fact.</param>
    public static async Task RefreshAsync(
        IBootTimeReader reader,
        Computer computer,
        CancellationToken cancellationToken,
        Action<Exception>? onUnexpectedError = null,
        Action? onUnreadable = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(computer);

        BootTimeReading? reading;
        bool threw = false;
        try
        {
            // NO ConfigureAwait(false) — load-bearing, see the remarks above: the write after this await
            // is data-bound Computer state and must stay on the caller's captured context.
            reading = await reader.ReadAsync(computer.Name, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;   // monitoring stopped mid-read — we learned nothing, so change nothing
        }
        catch (Exception ex)
        {
            // Not swallowed: handed to the caller to log. The cell still blanks — an unreadable box must
            // never leave a stale timestamp standing on a machine we just watched reboot.
            onUnexpectedError?.Invoke(ex);
            reading = null;
            threw = true;
        }

        // Unconditional on purpose. null reading -> null cell. See the remarks: no vitals-style null-guard.
        computer.LastBootTime = reading?.LastBootUpTime;

        // Fired AFTER the write so it can never pre-empt the blanking it describes, and ONLY when the reader
        // returned null without throwing — the throw path already emitted its own line via onUnexpectedError,
        // and one failure must not produce two log lines. A silent blank is what made the earlier incidents
        // undiagnosable; a double-logged one would just be noise.
        if (reading is null && !threw)
        {
            onUnreadable?.Invoke();
        }
    }
}
