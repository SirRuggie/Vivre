using System.Collections.Generic;
using System.Linq;
using Vivre.Core.Models;
using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Tests for <see cref="ComponentCleanupTargets.Select"/> — the 2016 "Clean up" panel action is
/// selection-driven and ignores staged-state (unlike Stage / Verify): the selected 2016 boxes, or all
/// 2016 boxes when nothing is selected; non-2016 rows are always excluded.
/// </summary>
public class ComponentCleanupTargetsTests
{
    private const int Server2016 = 14393; // LcuRouting.Server2016Build
    private const int Server2019 = 17763;

    private static Computer Box(string name, int? build, bool staged = false) =>
        new(name) { OsBuild = build, RequiresStagedPatching = staged };

    [Fact]
    public void NothingSelected_targets_all_2016_in_tab()
    {
        var all = new List<Computer> { Box("A", Server2016), Box("B", Server2019), Box("C", Server2016) };

        IReadOnlyList<Computer> result = ComponentCleanupTargets.Select(selected: [], all: all);

        Assert.Equal(["A", "C"], result.Select(c => c.Name));
    }

    [Fact]
    public void NothingSelected_includes_unflagged_2016_boxes()
    {
        // The whole point of the decouple: a NON-staged 2016 box is still cleanable.
        var all = new List<Computer>
        {
            Box("FLAGGED", Server2016, staged: true),
            Box("UNFLAGGED", Server2016, staged: false),
        };

        IReadOnlyList<Computer> result = ComponentCleanupTargets.Select(selected: [], all: all);

        Assert.Equal(["FLAGGED", "UNFLAGGED"], result.Select(c => c.Name));
    }

    [Fact]
    public void SomeSelected_targets_only_the_selected_2016_boxes()
    {
        var selected = new List<Computer> { Box("A", Server2016), Box("C", Server2016) };
        var all = new List<Computer> { Box("A", Server2016), Box("B", Server2016), Box("C", Server2016) };

        IReadOnlyList<Computer> result = ComponentCleanupTargets.Select(selected, all);

        Assert.Equal(["A", "C"], result.Select(c => c.Name));
    }

    [Fact]
    public void SelectedSet_excludes_non_2016_boxes()
    {
        // A selection mixing 2016 + non-2016 cleans only the 2016 ones.
        var selected = new List<Computer> { Box("WIN2016", Server2016), Box("WIN2019", Server2019) };

        IReadOnlyList<Computer> result = ComponentCleanupTargets.Select(selected, all: selected);

        Assert.Equal(["WIN2016"], result.Select(c => c.Name));
    }

    [Fact]
    public void SelectionWithNo2016_yields_empty_not_all()
    {
        // Selecting only non-2016 boxes and clicking Clean cleans nothing — it must NOT fall back to all-2016.
        var selected = new List<Computer> { Box("WIN2019", Server2019), Box("WIN2022", 20348) };
        var all = new List<Computer> { Box("WIN2016", Server2016), Box("WIN2019", Server2019) };

        IReadOnlyList<Computer> result = ComponentCleanupTargets.Select(selected, all);

        Assert.Empty(result);
    }

    [Fact]
    public void StagedFlag_is_ignored_for_the_selected_set()
    {
        // A selected 2016 box is a Clean target whether or not it's flagged for staged patching.
        var selected = new List<Computer> { Box("UNFLAGGED", Server2016, staged: false) };

        IReadOnlyList<Computer> result = ComponentCleanupTargets.Select(selected, all: selected);

        Assert.Equal(["UNFLAGGED"], result.Select(c => c.Name));
    }

    [Fact]
    public void UnknownBuild_is_excluded()
    {
        var all = new List<Computer> { Box("UNSCANNED", null), Box("WIN2016", Server2016) };

        IReadOnlyList<Computer> result = ComponentCleanupTargets.Select(selected: [], all: all);

        Assert.Equal(["WIN2016"], result.Select(c => c.Name));
    }

    [Fact]
    public void EmptyInputs_yield_empty()
    {
        Assert.Empty(ComponentCleanupTargets.Select(selected: [], all: []));
    }
}
