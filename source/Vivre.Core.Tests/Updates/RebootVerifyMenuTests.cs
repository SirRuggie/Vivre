using Vivre.Core.Models;
using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// The visibility predicate for the right-click "Reboot &amp; verify…" item. It must show for ANY
/// reboot-pending box (keyed on PatchState.RebootPending, the same signal as the grid pill), Patching-mode
/// only — including a box whose reboot-pending survived an app reopen / re-scan (UpdatePhase back to
/// "Available" while RebootRequired stays true) — AND for an Error box with a confirmed pending reboot
/// (a failed install that needs its reboot; Error's pill precedence otherwise hides the item at exactly
/// the moment it's needed). It must never appear in Health mode, never on a mid-operation box, and never
/// on an Error box whose reboot state is false or unknown.
/// </summary>
public class RebootVerifyMenuTests
{
    // A box that finished installing this session and is reboot-pending (UpdatePhase still "PendingReboot").
    private static Computer InSessionRebootPending() =>
        new("HOST") { UpdatePhase = "PendingReboot", RebootRequired = true };

    // A box whose reboot-pending survived a reopen + re-scan: the scan reset UpdatePhase to "Available", but
    // RebootRequired is still true → PatchState derives to RebootPending (the pill shows "Reboot pending").
    private static Computer ReopenedRescannedRebootPending() =>
        new("HOST") { UpdatePhase = "Available", RebootRequired = true };

    private static Computer NotRebootPending() =>
        new("HOST") { UpdatePhase = "Available", RebootRequired = false };

    // The AZRVISIONBT-SQ1 incident shape: a failed install forces the Error pill (Error beats
    // reboot-pending in DerivePatchState) while the reboot the box needs is confirmed pending.
    private static Computer FailedInstallRebootRequired() =>
        new("HOST") { UpdatePhase = "Error", RebootRequired = true };

    private static Computer FailedInstallNoReboot() =>
        new("HOST") { UpdatePhase = "Error", RebootRequired = false };

    private static Computer FailedInstallRebootUnknown() =>
        new("HOST") { UpdatePhase = "Error", RebootRequired = null };

    [Fact]
    public void Reboot_pending_box_in_Patching_is_offered_even_when_not_installed_this_session()
    {
        // The reopened/re-scanned case (Madison-VFP): PatchState.RebootPending via RebootRequired, even
        // though UpdatePhase is "Available". This is the bug — it must be offered.
        Computer box = ReopenedRescannedRebootPending();
        Assert.Equal(PatchState.RebootPending, box.PatchState); // sanity: derives to RebootPending (drives the pill)
        Assert.True(RebootVerifyMenu.ShouldOfferRebootVerify(box, isUpdateMode: true));
    }

    [Fact]
    public void Reboot_pending_after_an_in_session_install_still_offered_no_regression()
    {
        Assert.True(RebootVerifyMenu.ShouldOfferRebootVerify(InSessionRebootPending(), isUpdateMode: true));
    }

    [Fact]
    public void Not_reboot_pending_box_in_Patching_is_not_offered()
    {
        Assert.False(RebootVerifyMenu.ShouldOfferRebootVerify(NotRebootPending(), isUpdateMode: true));
    }

    [Fact]
    public void Health_mode_never_offers_it_even_when_reboot_pending()
    {
        Assert.False(RebootVerifyMenu.ShouldOfferRebootVerify(ReopenedRescannedRebootPending(), isUpdateMode: false));
        Assert.False(RebootVerifyMenu.ShouldOfferRebootVerify(InSessionRebootPending(), isUpdateMode: false));
    }

    [Fact]
    public void Health_mode_does_not_offer_a_non_pending_box_either()
    {
        Assert.False(RebootVerifyMenu.ShouldOfferRebootVerify(NotRebootPending(), isUpdateMode: false));
    }

    [Fact]
    public void Error_box_with_confirmed_pending_reboot_is_offered_the_incident_fix()
    {
        Computer box = FailedInstallRebootRequired();
        Assert.Equal(PatchState.Error, box.PatchState); // sanity: Error pill (Error beats reboot-pending)
        Assert.True(RebootVerifyMenu.ShouldOfferRebootVerify(box, isUpdateMode: true));
    }

    [Fact]
    public void Error_box_with_no_pending_reboot_is_not_offered()
    {
        Assert.False(RebootVerifyMenu.ShouldOfferRebootVerify(FailedInstallNoReboot(), isUpdateMode: true));
    }

    [Fact]
    public void Error_box_with_UNKNOWN_reboot_state_is_not_offered()
    {
        // null = the probe couldn't answer — an unconfirmed reboot never surfaces a reboot button.
        Assert.False(RebootVerifyMenu.ShouldOfferRebootVerify(FailedInstallRebootUnknown(), isUpdateMode: true));
    }

    [Fact]
    public void Unreachable_box_with_confirmed_pending_reboot_is_offered()
    {
        // Unreachable ("Can't reach WU") reduces to the Error display-state; with a confirmed pending
        // reboot it is the same needs-the-reboot situation — deliberately included.
        var box = new Computer("HOST") { UpdatePhase = "Unreachable", RebootRequired = true };
        Assert.Equal(PatchState.Error, box.PatchState); // sanity: Unreachable reduces to Error
        Assert.True(RebootVerifyMenu.ShouldOfferRebootVerify(box, isUpdateMode: true));
    }

    [Fact]
    public void Mid_install_box_is_never_offered_even_with_reboot_required()
    {
        // THE TRAP: a working row must never grow a reboot button. Installing's display state ignores
        // the pending flag, so neither predicate arm can match.
        var box = new Computer("HOST") { UpdatePhase = "Installing", RebootRequired = true };
        Assert.Equal(PatchState.Installing, box.PatchState); // sanity: stays the working state
        Assert.False(RebootVerifyMenu.ShouldOfferRebootVerify(box, isUpdateMode: true));
    }

    [Fact]
    public void Health_mode_never_offers_the_error_reboot_case_either()
    {
        Assert.False(RebootVerifyMenu.ShouldOfferRebootVerify(FailedInstallRebootRequired(), isUpdateMode: false));
    }
}
