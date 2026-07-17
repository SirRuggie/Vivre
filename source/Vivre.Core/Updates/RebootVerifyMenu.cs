using Vivre.Core.Models;

namespace Vivre.Core.Updates;

/// <summary>
/// Pure visibility predicate for the grid's right-click <b>"Reboot &amp; verify…"</b> item, extracted so it
/// is unit-testable without the UI. Offered for a reboot-pending box, and for an Error box with a confirmed
/// pending reboot (a failed install that needs its reboot — see the method doc).
/// </summary>
public static class RebootVerifyMenu
{
    /// <summary>
    /// Whether "Reboot &amp; verify…" should be OFFERED for <paramref name="computer"/>: only in Patching
    /// mode (<paramref name="isUpdateMode"/>), and only when a reboot genuinely makes sense — keyed on the
    /// SAME <see cref="PatchState.RebootPending"/> signal that drives the grid's "Reboot pending" pill, so
    /// the menu item and the pill always agree, PLUS the failed-install case below.
    ///
    /// <para>Because <see cref="PatchState.RebootPending"/> is derived from <c>RebootRequired</c> (which a
    /// re-scan does NOT reset — only an actual reboot clears it), this shows for a box that is reboot-pending
    /// from ANY source: an in-session install, an app reopen, a re-scan, BatchPatch, or a manual patch. A
    /// reopened/re-scanned box whose <c>UpdatePhase</c> is back to "Available" still reads
    /// <see cref="PatchState.RebootPending"/> while a reboot is pending, so it still gets the item.</para>
    ///
    /// <para><b>Error-but-reboot-required (the AZRVISIONBT-SQ1 dead-end):</b> a FAILED install forces the
    /// red Error pill, and Error beats reboot-pending in <c>DerivePatchState</c> — so the box never reads
    /// <see cref="PatchState.RebootPending"/> even with the reboot dot lit. That hid this item at exactly
    /// the moment the operator needed it (Reboot &amp; verify's DCOM trigger is the one reboot path that
    /// works on Kerberos-broken boxes; the Force-reboot fallback rides WinRM and dead-ends there). So the
    /// item is ALSO offered when the box shows <see cref="PatchState.Error"/> with a CONFIRMED pending
    /// reboot (<c>RebootRequired == true</c> strictly — an unknown/null reboot state does not qualify).
    /// <see cref="PatchState.Error"/> includes the Unreachable ("Can't reach WU") phase, deliberately:
    /// a WU-reach failure with a confirmed pending reboot is the same needs-the-reboot situation.
    /// Working states (Scanning/Downloading/Installing/Uninstalling and the Staging/Cleaning chips) can
    /// never satisfy either arm — their display states ignore the pending flag — so a mid-operation box
    /// still never offers a reboot.</para>
    ///
    /// <para>Visibility only — clicking still routes through the existing operator-confirmed Reboot &amp;
    /// verify flow (graceful reboot → confirm dialog → wave). This predicate adds no reboot path.</para>
    /// </summary>
    public static bool ShouldOfferRebootVerify(Computer computer, bool isUpdateMode)
    {
        ArgumentNullException.ThrowIfNull(computer);
        return isUpdateMode
            && (computer.PatchState == PatchState.RebootPending
                || (computer.PatchState == PatchState.Error && computer.RebootRequired == true));
    }
}
