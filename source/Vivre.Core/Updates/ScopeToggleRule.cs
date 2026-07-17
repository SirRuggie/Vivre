namespace Vivre.Core.Updates;

/// <summary>
/// Pure predicate for the Applicable/Installed scope-toggle: whether a row's update message must
/// be PRESERVED rather than swapped to the target scope's cached scan message.
///
/// A terminal status — whether a failure (<see cref="PatchState.Error"/>, which includes
/// Unreachable) or a success (<see cref="PatchState.Done"/> / <see cref="PatchState.RebootPending"/>,
/// which include the 2016 Cleaned/Deferred terminals) or the post-reboot <see cref="PatchState.Unverified"/>
/// couldn't-confirm/rescan terminal — carries operation detail (e.g. "Installed 3
/// updates", "Can't reach WU"), NOT a scope-scoped scan result. The target scope's cached message is
/// often null for such rows (they were never successfully scanned in that scope), so swapping would
/// silently blank the detail. An in-flight row (<paramref name="isPatching"/>) similarly carries live
/// progress detail.
///
/// Only a non-terminal scanned state (e.g. <see cref="PatchState.Available"/>) has a meaningful
/// scope-scoped cached message on both sides and should swap on toggle.
///
/// The per-scope COUNTS (<c>UpdatesAvailable</c>) still track for every row regardless — this rule
/// applies to the MESSAGE only.
/// </summary>
public static class ScopeToggleRule
{
    /// <summary>
    /// Returns <see langword="true"/> when the scope-toggle must PRESERVE the row's current update
    /// message (i.e. must NOT replace it with the target scope's cached scan message).
    /// </summary>
    /// <param name="state">The row's current <see cref="PatchState"/>.</param>
    /// <param name="isPatching">Whether the row is mid-operation (download / install / uninstall / scan).</param>
    public static bool PreservesMessageOnScopeToggle(PatchState state, bool isPatching)
        => isPatching || state is PatchState.Error or PatchState.Done or PatchState.RebootPending or PatchState.Unverified;
}
