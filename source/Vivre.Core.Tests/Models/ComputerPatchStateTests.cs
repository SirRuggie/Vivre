using Vivre.Core.Models;
using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Models;

/// <summary>
/// The grid's Status chip / message color / fleet counts all read <see cref="Computer.PatchState"/>,
/// so its derivation from UpdatePhase + RebootRequired must be exactly right — especially the two
/// cases that previously made the UI lie after a reboot.
/// </summary>
public class ComputerPatchStateTests
{
    [Theory]
    [InlineData(null, null, PatchState.Idle)]
    [InlineData("Idle", null, PatchState.Idle)]
    [InlineData("Scanning", null, PatchState.Scanning)]
    // Staging (2016 DISM add-package) and Cleaning (DISM /StartComponentCleanup) get their own chip LABELS
    // but reduce to the Scanning display-state so the working/colour/Stop/tally logic treats them like a scan.
    [InlineData("Staging", null, PatchState.Scanning)]
    [InlineData("Cleaning", null, PatchState.Scanning)]
    // Cleaned: cleanup finished — maps to Done (green), same as Done phase.
    [InlineData("Cleaned", null, PatchState.Done)]
    [InlineData("Available", null, PatchState.Available)]
    [InlineData("Downloading", null, PatchState.Downloading)]
    [InlineData("Installing", null, PatchState.Installing)]
    [InlineData("Uninstalling", null, PatchState.Uninstalling)]
    [InlineData("PendingReboot", true, PatchState.RebootPending)]
    [InlineData("Rebooting", true, PatchState.RebootPending)]
    [InlineData("Done", false, PatchState.Done)]
    // Couldn't confirm/rescan after a reboot: neutral Unverified when no reboot is known pending...
    [InlineData("Unverified", null, PatchState.Unverified)]
    [InlineData("Unverified", false, PatchState.Unverified)]
    // ...but a later probe that finds a genuine pending reboot upgrades it to amber (Done's pending-guard).
    [InlineData("Unverified", true, PatchState.RebootPending)]
    [InlineData("Error", null, PatchState.Error)]
    // ERROR > REBOOT-PENDING: a failed install/uninstall (forced to the Error phase when FailedCount>0 by the
    // agent's Summarize / the controller's ApplyStatus) must read red Error even when a reboot is ALSO
    // pending — the failure is the headline; the reboot DOT stays lit separately. Locks the precedence the
    // false-green fix depends on (Error must beat amber reboot-pending).
    [InlineData("Error", true, PatchState.Error)]
    // Unreachable (transient WU retries exhausted) reduces to the red Error display-state — never green —
    // so a box that couldn't be scanned is counted/coloured as a failure and NEVER reads "up to date".
    [InlineData("Unreachable", null, PatchState.Error)]
    // Deferred (servicing-busy refusal: the agent didn't stage/clean because a reboot is already pending)
    // means the box IS reboot-pending — amber RebootPending, never green, regardless of the RebootRequired
    // flag's exact value (a deferral always implies a pending reboot).
    [InlineData("Deferred", true, PatchState.RebootPending)]
    [InlineData("Deferred", null, PatchState.RebootPending)]
    public void Derives_expected_state(string? phase, bool? rebootRequired, PatchState expected)
    {
        var c = new Computer("HOST") { UpdatePhase = phase, RebootRequired = rebootRequired };
        Assert.Equal(expected, c.PatchState);
    }

    [Fact]
    public void PendingReboot_phase_with_reboot_cleared_reads_Done_back_online()
    {
        // After the reboot completes, the probe clears RebootRequired but UpdatePhase may still
        // say PendingReboot — the chip must go green, not stay amber.
        var c = new Computer("HOST") { UpdatePhase = "PendingReboot", RebootRequired = false };
        Assert.Equal(PatchState.Done, c.PatchState);
    }

    [Fact]
    public void Done_phase_with_reboot_outstanding_reads_RebootPending()
    {
        var c = new Computer("HOST") { UpdatePhase = "Done", RebootRequired = true };
        Assert.Equal(PatchState.RebootPending, c.PatchState);
    }

    [Fact]
    public void An_unscanned_row_with_a_pending_reboot_shows_RebootPending()
    {
        var c = new Computer("HOST") { RebootRequired = true };
        Assert.Equal(PatchState.RebootPending, c.PatchState);
    }

    [Fact]
    public void PatchState_raises_change_when_either_input_changes()
    {
        var c = new Computer("HOST");
        int hits = 0;
        c.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Computer.PatchState)) hits++; };

        c.UpdatePhase = "Installing";
        c.RebootRequired = true;

        Assert.True(hits >= 2);
    }

    [Fact]
    public void A_fresh_unscanned_row_is_Idle_not_Unverified()
    {
        // THE TRAP: RebootRequired == null is ALSO every fresh, not-yet-probed row. The Unverified state
        // must key off the reboot-wave OUTCOME (UpdatePhase), never off null reboot-state — otherwise every
        // unscanned row would go non-green. A fresh row (no UpdatePhase) must stay Idle.
        var c = new Computer("HOST");
        Assert.Equal(PatchState.Idle, c.PatchState);
        Assert.NotEqual(PatchState.Unverified, c.PatchState);
    }
}
