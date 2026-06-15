using Vivre.Core.Models;

namespace Vivre.Core.Updates;

/// <summary>
/// Pure visibility predicate for the grid's right-click <b>"Reboot &amp; verify…"</b> item, extracted so it
/// is unit-testable without the UI.
/// </summary>
public static class RebootVerifyMenu
{
    /// <summary>
    /// Whether "Reboot &amp; verify…" should be OFFERED for <paramref name="computer"/>: only in Patching
    /// mode (<paramref name="isUpdateMode"/>), and only when the box is genuinely reboot-pending — keyed on
    /// the SAME <see cref="PatchState.RebootPending"/> signal that drives the grid's "Reboot pending" pill,
    /// so the menu item and the pill always agree.
    ///
    /// <para>Because <see cref="PatchState.RebootPending"/> is derived from <c>RebootRequired</c> (which a
    /// re-scan does NOT reset — only an actual reboot clears it), this shows for a box that is reboot-pending
    /// from ANY source: an in-session install, an app reopen, a re-scan, BatchPatch, or a manual patch. A
    /// reopened/re-scanned box whose <c>UpdatePhase</c> is back to "Available" still reads
    /// <see cref="PatchState.RebootPending"/> while a reboot is pending, so it still gets the item.</para>
    ///
    /// <para>Visibility only — clicking still routes through the existing operator-confirmed Reboot &amp;
    /// verify flow (graceful reboot → confirm dialog → wave). This predicate adds no reboot path.</para>
    /// </summary>
    public static bool ShouldOfferRebootVerify(Computer computer, bool isUpdateMode)
    {
        ArgumentNullException.ThrowIfNull(computer);
        return isUpdateMode && computer.PatchState == PatchState.RebootPending;
    }
}
