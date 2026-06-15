using Vivre.Core.Models;
using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// The visibility predicate for the right-click "Reboot &amp; verify…" item. It must show for ANY
/// reboot-pending box (keyed on PatchState.RebootPending, the same signal as the grid pill), Patching-mode
/// only — including a box whose reboot-pending survived an app reopen / re-scan (UpdatePhase back to
/// "Available" while RebootRequired stays true) — and must never appear in Health mode.
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
}
