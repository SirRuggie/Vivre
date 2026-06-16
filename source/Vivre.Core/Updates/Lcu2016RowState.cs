using System;

namespace Vivre.Core.Updates;

/// <summary>
/// Pure, unit-tested decisions for how the Server 2016 staged-patching lane maps a terminal/in-flight
/// agent status onto a grid row — extracted from the view-model so the load-bearing distinctions
/// (above all <b>Deferred ≠ Staged</b>) are testable in Vivre.Core.Tests without the WPF view-model
/// (which Vivre.Core.Tests can't reference — the same constraint <see cref="Net.ReachabilityConfirmation"/>
/// solved). The view-model mechanically applies the output of these helpers.
/// </summary>
public static class Lcu2016RowState
{
    /// <summary>The point past which a still-running cleanup is FLAGGED (display only) as overlong — long
    /// after any real cleanup finishes, so the flag is reassurance the watch is alive, never a kill. The
    /// 3-hour per-host timeout no longer applies to cleanup (the watch runs unbounded; the agent's terminal
    /// Error + the lane's silence-watchdog still catch a genuinely dead agent), so this is a pure display
    /// overlay with no teardown.</summary>
    public static readonly TimeSpan CleanupCeiling = TimeSpan.FromHours(8);

    /// <summary>How the view-model should set a row from a terminal STAGE status. The view-model applies
    /// these mechanically: <c>StagedThisSession = Staged</c>, force <c>RebootRequired</c> true only when
    /// <c>RebootRequired</c> is true (never clear a true from elsewhere), <c>UpdatePhase = Phase</c>,
    /// <c>UpdateMessage = Message</c>.</summary>
    /// <param name="Staged">True ONLY for a real stage (PendingReboot). A Deferred refusal is NEVER staged.</param>
    /// <param name="RebootRequired">True when the box is reboot-pending (a real stage, or a deferral).</param>
    /// <param name="Phase">The <see cref="PatchPhase"/> string to write onto the row.</param>
    /// <param name="Message">The glanceable row message.</param>
    public readonly record struct StageRowOutcome(bool Staged, bool RebootRequired, string Phase, string Message);

    /// <summary>
    /// Map a terminal STAGE phase to a row outcome. The CRITICAL invariant: <see cref="PatchPhase.Deferred"/>
    /// (a servicing-busy refusal) yields <c>Staged == false</c> with a "reboot first" message — it must NEVER
    /// read as the amber "Staged — run Reboot Wave" state, because the box was not touched.
    /// </summary>
    public static StageRowOutcome MapStageTerminal(PatchPhase phase, string kb, string statusMessage) => phase switch
    {
        // A real stage: the CU was added and the box now needs a reboot to commit it.
        PatchPhase.PendingReboot => new(Staged: true, RebootRequired: true, "PendingReboot", $"Staged {kb} · run Reboot Wave to commit"),
        // A servicing-busy refusal: nothing was staged. Reboot-pending (so amber), but explicitly NOT staged
        // and NOT the Reboot-Wave message — the operator must reboot to clear the pending state, then re-stage.
        PatchPhase.Deferred => new(Staged: false, RebootRequired: true, "Deferred", "Couldn't stage — reboot to clear the pending state first"),
        // Already at the target UBR — nothing to do.
        PatchPhase.Done => new(Staged: false, RebootRequired: false, "Done", $"Already current ({kb})"),
        // Error and anything else: surface the agent's message as a failure.
        _ => new(Staged: false, RebootRequired: false, "Error", statusMessage),
    };

    /// <summary>
    /// Map a terminal CLEANUP phase to a glanceable (Phase, Message) pair so the operator reads the three
    /// distinct end-states at a glance: <b>Cleaned — ready to Stage</b> (green), <b>Cleaned — reboot-pending</b>
    /// (amber; reboot before Stage), or <b>Deferred</b> (the servicing-busy refusal — reboot first). RebootRequired
    /// for the 3010 case still comes from the generic ApplyStatus rebootPending mapping; this only sets the labels.
    /// </summary>
    public static (string Phase, string Message) MapCleanupTerminal(PatchPhase phase, string statusMessage) => phase switch
    {
        PatchPhase.Done => ("Cleaned", "Cleaned — ready to Stage"),
        PatchPhase.PendingReboot => ("Cleaned", "Cleaned — reboot-pending (reboot before Stage)"),
        PatchPhase.Deferred => ("Deferred", "Couldn't clean up — reboot to clear the pending state first"),
        _ => ("Error", statusMessage),
    };

    /// <summary>Whether a still-running cleanup has passed the display ceiling. Display-only — it NEVER
    /// cancels the watch, sets Error, or tears down; it only appends a "still going, check the box" flag.</summary>
    public static bool IsPastCleanupCeiling(TimeSpan elapsed, TimeSpan ceiling) => elapsed >= ceiling;

    /// <summary>
    /// Build the live "Cleaning — {elapsed}" readout shown while a cleanup runs, so the row never looks
    /// frozen even when DISM's percent stalls. Composes the host-side elapsed (the real liveness), the
    /// agent's last-known percent (decoration, when known), the agent's "looks stalled (may still be
    /// working)" hint, and — past the ceiling — a "still going, check the box" flag.
    /// </summary>
    public static string BuildCleanupProgressLabel(TimeSpan elapsed, int? percent, bool stalled, bool pastCeiling)
    {
        string label = $"Cleaning — {FormatElapsed(elapsed)}";
        if (percent is int p)
        {
            label += $" · {p}%";
        }

        if (stalled)
        {
            label += " · looks stalled (may still be working)";
        }

        if (pastCeiling)
        {
            label += " — still going, check the box";
        }

        return label;
    }

    /// <summary>Compact elapsed: seconds under a minute, whole minutes under an hour, else "Hh Mm".</summary>
    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalMinutes < 1)
        {
            return $"{(int)elapsed.TotalSeconds}s";
        }

        if (elapsed.TotalHours < 1)
        {
            return $"{(int)elapsed.TotalMinutes}m";
        }

        return $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
    }
}
