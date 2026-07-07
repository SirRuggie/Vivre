using System.Collections.Generic;
using System.Linq;
using Vivre.Core.Models;

namespace Vivre.Core.Updates;

/// <summary>
/// Pure target picker for the 2016 "Clean up" (DISM <c>/StartComponentCleanup</c>) panel action.
/// <para>
/// Unlike Stage / Verify — which act only on boxes flagged for staged patching
/// (<see cref="StagePreconditions.IsStageTarget"/>) — component cleanup is <b>selection-driven</b> and
/// deliberately ignores staged-state: DISM <c>/StartComponentCleanup</c> is self-contained (it needs
/// nothing a Stage sets up), it reclaims WinSxS/superseded-update space that speeds up normal Windows
/// Update on ANY 2016 box, and it never reboots. So Clean follows the same selection convention as the
/// rest of the toolbar: the selected 2016 boxes, or every 2016 box in the tab when nothing is selected.
/// </para>
/// Non-2016 rows are always excluded — Clean is a 2016-lane operation.
/// </summary>
public static class ComponentCleanupTargets
{
    /// <summary>The 2016 boxes to component-clean: the selected 2016 boxes when any row is selected,
    /// otherwise every 2016 box in <paramref name="all"/>. Staged-state is intentionally ignored; non-2016
    /// rows are excluded either way. (Mirrors the install toolbar's "selection, else all" scoping, then
    /// filters to the 2016 lane — a selection containing no 2016 box yields an empty set, i.e. nothing to clean.)</summary>
    public static IReadOnlyList<Computer> Select(IReadOnlyList<Computer> selected, IReadOnlyList<Computer> all)
    {
        IEnumerable<Computer> source = selected.Count > 0 ? selected : all;
        return source.Where(c => LcuRouting.Is2016(c.OsBuild)).ToList();
    }
}
