using System.Management.Automation;
using Vivre.Core.PowerShell;

namespace Vivre.Core.Updates;

/// <summary>
/// Pure (no I/O) outcome map for one scheduled-reboot REGISTRATION attempt — the register-side
/// mirror of <see cref="ScheduledTaskCancelOutcome"/>. The load-bearing rule is the ASYMMETRY:
/// an UNCONFIRMED registration (the invoke timed out mid-request, so Register-ScheduledTask may or
/// may not have landed) must be treated as SCHEDULED, never as failed. Being wrong is not
/// symmetric — "not scheduled" when it IS means an unexpected production reboot (dangerous);
/// "scheduled" when it is NOT means a reboot that never comes (annoying, safe). So an unconfirmed
/// attempt lights the Scheduled marker with an honest "couldn't confirm — verify on the box", and
/// only a CLEAN failure report from the box (HadErrors: the script ran and said the registration
/// failed) leaves the row dark.
/// </summary>
public sealed record ScheduleRegistrationOutcome(bool TreatAsScheduled, string Status, string? Detail)
{
    /// <summary>Classifies one registration attempt. <paramref name="unconfirmed"/> wins over
    /// everything: when the request timed out there IS no trustworthy result to read.</summary>
    public static ScheduleRegistrationOutcome Classify(bool hadErrors, IReadOnlyList<string> errors, bool unconfirmed)
    {
        if (unconfirmed)
        {
            return new(
                TreatAsScheduled: true,
                Status: "Scheduled — couldn't confirm",
                Detail: "Vivre lost the box mid-request (no answer, or the session dropped); the task may " +
                        "still have registered. Treat the reboot as scheduled and verify on the box " +
                        "(Task Scheduler ▸ Vivre_Reboot).");
        }

        if (hadErrors)
        {
            return new(
                TreatAsScheduled: false,
                Status: "Schedule failed",
                Detail: errors.Count > 0 ? errors[0] : "Register-ScheduledTask failed");
        }

        return new(TreatAsScheduled: true, Status: "Scheduled", Detail: null);
    }

    /// <summary>
    /// True when a THROWN failure leaves the registration UNCONFIRMED — the box was (or may have
    /// been) reached and the command may already have run when the failure hit, so the row must be
    /// treated as scheduled (see the asymmetry above). The rule: a row goes DARK only when we can
    /// PROVE the command never ran, or the box itself said the registration failed.
    /// <list type="bullet">
    /// <item><see cref="RemoteSessionLostException"/> with <c>AtConnect == false</c> — the canonical
    /// case: the session dropped MID-RUN, and the type's own contract says work may already be in
    /// flight on the box. <c>AtConnect == true</c> means the runspace never opened — nothing ran.</item>
    /// <item><see cref="KerberosWrongPrincipalException"/> / <see cref="RemoteShellInitException"/> —
    /// connect/shell-setup failures; the command never started.</item>
    /// <item><see cref="RuntimeException"/> (incl. <c>RemoteException</c>) — a TERMINATING in-script
    /// error: the script ran and FAILED, so the registration genuinely didn't happen — dark is the
    /// truth (PSRunspaceHost deliberately never mislabels these as lost connections).</item>
    /// <item>Anything untyped — we cannot prove the command never ran; the safe direction is
    /// scheduled-and-verify. (Cancellation/timeout is also unconfirmed; the caller's catch filters
    /// route it before this is consulted.)</item>
    /// </list>
    /// </summary>
    public static bool IsUnconfirmedFailure(Exception ex) => ex switch
    {
        RemoteSessionLostException e => !e.AtConnect,
        KerberosWrongPrincipalException => false,
        RemoteShellInitException => false,
        RuntimeException => false, // covers RemoteException too (it derives from RuntimeException)
        OperationCanceledException => true,
        _ => true,
    };
}
