using Vivre.Core.Models;

namespace Vivre.Core.Updates;

/// <summary>
/// The ceiling on the post-reboot verify arc, and the HONEST row state a cut-short arc lands on.
/// <para>
/// <b>Why this exists.</b> The monitor awaits the verify arc INLINE inside its per-row work, and that work
/// runs under <c>Task.WhenAll</c> for the whole tab — so an arc that never returns freezes every row on
/// that tab, with no log line and no UI tell. Field-reproduced 2026-07-28: two force-rebooted rows, ~2m52s
/// of total activity-log silence across roughly 8 monitor passes, seven rows stuck with green Online pills
/// while two boxes were provably down. The arc had been handed the RAW monitor token, so the 120 s deadline
/// its caller armed covered nothing (cancelling a linked source never cancels its parent).
/// </para>
/// <para>
/// <b>Why the arc gets its OWN ceiling rather than inheriting the probe's 120 s.</b> That 120 s was sized
/// for ONE WinRM reboot-pending probe. The arc is a different, much larger operation: up to
/// <c>PostRebootRescanAttempts</c> WUA rescans, each now capped at <c>ScanAttemptTimeoutSeconds</c> (300 s),
/// plus the retry delays between them, plus its own 120 s-capped probe at the end — a bounded worst case
/// near 18 minutes. Inheriting 120 s would cut legitimate arcs short as a matter of routine; leaving it
/// unbounded is what caused the incident. <see cref="Ceiling"/> is therefore set explicitly to one full
/// scan attempt's worth of time: long enough for a slow-but-succeeding rescan and the settle-retry pattern
/// the arc was written for, short enough that the pathological case costs one attempt instead of three.
/// </para>
/// <para>
/// <b>Honest-state rule.</b> A cut-short arc has PROVEN NOTHING. It must never leave a row looking verified,
/// rebooted, or up to date — so <see cref="MarkUnverified"/> writes the same neutral Unverified state the
/// arc's own "couldn't rescan" path already uses, and deliberately does NOT touch
/// <see cref="Computer.RebootRequired"/> or <see cref="Computer.StagedThisSession"/>: a timeout is not
/// evidence about either.
/// </para>
/// <para>
/// UI-free by design so <c>Vivre.Core.Tests</c> (net10.0) can cover it — the caller lives in the
/// net10.0-windows Desktop project and is unreachable from the test project.
/// </para>
/// </summary>
public static class VerifyArcTimeout
{
    /// <summary>Wall-clock ceiling for the whole verify arc. See the remarks for the sizing argument.</summary>
    public static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether an <see cref="OperationCanceledException"/> out of the arc is OUR deadline rather than the
    /// operator stopping monitoring. Only the former may be swallowed — a real Stop must keep propagating,
    /// exactly as the arc's existing probe-timeout filter does.
    /// </summary>
    public static bool IsArcDeadline(bool arcCancelled, bool monitorCancelled) => arcCancelled && !monitorCancelled;

    /// <summary>The row message for a cut-short arc — reuses the arc's existing couldn't-rescan wording.</summary>
    public const string RowMessage = "Couldn't rescan after reboot — re-check";

    /// <summary>The activity line for a cut-short arc. The caller supplies the tab tag as the log origin.</summary>
    public static string ActivityLine(string host) =>
        $"{host}: post-reboot verify didn't finish within {Ceiling.TotalMinutes:N0} min — left Unverified, re-check.";

    /// <summary>
    /// Lands <paramref name="computer"/> in the honest post-timeout state. Never green, never "rebooted",
    /// never "up to date". Call on the monitor's UI context: <see cref="Computer.UpdatePhase"/> is a
    /// live-filter input.
    /// </summary>
    public static void MarkUnverified(Computer computer)
    {
        ArgumentNullException.ThrowIfNull(computer);

        computer.UpdateMessage = RowMessage;
        computer.UpdatePhase = PatchPhase.Unverified.ToString();
        // NOT probe-only: the rescan itself didn't finish, so a later clean probe must not self-heal this
        // row to green (MonitorSelfHeal keys off exactly this flag).
        computer.UnverifiedRebootProbeOnly = false;

        // Deliberately NOT written: RebootRequired and StagedThisSession. A timeout is evidence of nothing,
        // and clearing either would be the false-success this whole arc exists to prevent.
    }
}
