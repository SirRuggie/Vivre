using System.Management.Automation;
using Vivre.Core.PowerShell;
using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Locks the reboot-adjacent ASYMMETRY rule for scheduled-reboot registration: an UNCONFIRMED
/// attempt (the invoke timed out — the task may or may not have registered) must be treated as
/// SCHEDULED, never as failed. A dark row over an armed <c>Vivre_Reboot</c> task is an unexpected
/// production reboot; a lit row over a task that never registered is merely a reboot that never
/// comes. Only a clean failure report from the box leaves the row dark.
/// </summary>
public class ScheduleRegistrationOutcomeTests
{
    [Fact]
    public void Clean_success_is_scheduled_with_no_detail()
    {
        var outcome = ScheduleRegistrationOutcome.Classify(hadErrors: false, [], unconfirmed: false);

        Assert.True(outcome.TreatAsScheduled);
        Assert.Equal("Scheduled", outcome.Status);
        Assert.Null(outcome.Detail);
    }

    [Fact]
    public void Unconfirmed_is_treated_as_scheduled_with_a_verify_instruction()
    {
        // THE invariant: timed out ≠ not scheduled. The chip lights and the operator is told to verify.
        var outcome = ScheduleRegistrationOutcome.Classify(hadErrors: false, [], unconfirmed: true);

        Assert.True(outcome.TreatAsScheduled);
        Assert.Equal("Scheduled — couldn't confirm", outcome.Status);
        Assert.Contains("verify on the box", outcome.Detail);
    }

    [Fact]
    public void Unconfirmed_wins_over_a_stale_error_shape()
    {
        // A timed-out request has no trustworthy result to read — unconfirmed must dominate even if
        // caller state carries error-ish leftovers.
        var outcome = ScheduleRegistrationOutcome.Classify(
            hadErrors: true, ["Access is denied"], unconfirmed: true);

        Assert.True(outcome.TreatAsScheduled);
    }

    [Fact]
    public void Reported_failure_stays_dark_with_the_first_error()
    {
        // The box answered and said the registration failed — a dark row is the truth here.
        var outcome = ScheduleRegistrationOutcome.Classify(
            hadErrors: true, ["Access is denied", "second"], unconfirmed: false);

        Assert.False(outcome.TreatAsScheduled);
        Assert.Equal("Schedule failed", outcome.Status);
        Assert.Equal("Access is denied", outcome.Detail);
    }

    [Fact]
    public void Reported_failure_with_no_error_text_uses_the_fallback_detail()
    {
        var outcome = ScheduleRegistrationOutcome.Classify(hadErrors: true, [], unconfirmed: false);

        Assert.False(outcome.TreatAsScheduled);
        Assert.Equal("Register-ScheduledTask failed", outcome.Detail);
    }

    // ── IsUnconfirmedFailure: the thrown-failure bucket map ──────────────────
    // Dark only when we can PROVE the command never ran or the box said it failed.

    [Fact]
    public void Mid_invoke_session_drop_is_unconfirmed()
    {
        // THE door the timeout fix left open: AtConnect == false means the session died MID-RUN —
        // the type's own contract says work may already be in flight on the box.
        var ex = new RemoteSessionLostException("HOST1", new InvalidOperationException("drop"), atConnect: false);

        Assert.True(ScheduleRegistrationOutcome.IsUnconfirmedFailure(ex));
    }

    [Fact]
    public void Connect_phase_session_loss_is_a_definite_failure()
    {
        // AtConnect == true: the runspace never opened — nothing ran on the box; dark is the truth.
        var ex = new RemoteSessionLostException("HOST1", new InvalidOperationException("refused"), atConnect: true);

        Assert.False(ScheduleRegistrationOutcome.IsUnconfirmedFailure(ex));
    }

    [Fact]
    public void Kerberos_rejection_is_a_definite_failure()
    {
        var ex = new KerberosWrongPrincipalException("HOST1", new InvalidOperationException("0x80090322"));

        Assert.False(ScheduleRegistrationOutcome.IsUnconfirmedFailure(ex));
    }

    [Fact]
    public void Shell_init_failure_is_a_definite_failure()
    {
        var ex = new RemoteShellInitException("HOST1", new InvalidOperationException("MaxShellsPerUser"));

        Assert.False(ScheduleRegistrationOutcome.IsUnconfirmedFailure(ex));
    }

    [Fact]
    public void In_script_terminating_error_is_a_definite_failure()
    {
        // A RuntimeException/RemoteException means the script RAN and FAILED — the registration
        // genuinely didn't happen, so a dark row is the truth, not a hidden armed task.
        var ex = new RuntimeException("Register-ScheduledTask : Access is denied");

        Assert.False(ScheduleRegistrationOutcome.IsUnconfirmedFailure(ex));
    }

    [Fact]
    public void Unknown_exception_defaults_to_unconfirmed()
    {
        // The fail-safe direction: if we can't prove the command never ran, the chip lights.
        Assert.True(ScheduleRegistrationOutcome.IsUnconfirmedFailure(new InvalidOperationException("weird")));
    }
}
