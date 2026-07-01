using Vivre.Core.Vitals;
using Xunit;

namespace Vivre.Core.Tests.Vitals;

public class VitalsShapingTests
{
    [Fact]
    public void StoppedServices_counts_all_but_caps_names_at_15()
    {
        // 20 matched services: the count reflects ALL 20 (truthful), the display list caps at 15 — the
        // load-bearing parity with WinRM ($stopped.Count vs Select-Object -First 15).
        var names = Enumerable.Range(1, 20).Select(i => (string?)$"Service {i}").ToList();

        (int count, IReadOnlyList<string> shaped) = VitalsShaping.StoppedServices(names);

        Assert.Equal(20, count);
        Assert.Equal(15, shaped.Count);
        Assert.Equal("Service 1", shaped[0]);
        Assert.Equal("Service 15", shaped[14]);
    }

    [Fact]
    public void StoppedServices_count_includes_blank_named_instances_but_names_skip_them()
    {
        // A matched instance with no DisplayName still counts (it IS a stopped auto-service), but it
        // isn't shown as a blank row in the names list.
        var names = new List<string?> { "Alpha", null, "", "  ", "Beta" };

        (int count, IReadOnlyList<string> shaped) = VitalsShaping.StoppedServices(names);

        Assert.Equal(5, count);
        Assert.Equal(new[] { "Alpha", "Beta" }, shaped);
    }

    [Fact]
    public void StoppedServices_empty_is_zero_and_empty()
    {
        (int count, IReadOnlyList<string> shaped) = VitalsShaping.StoppedServices([]);

        Assert.Equal(0, count);
        Assert.Empty(shaped);
    }

    [Fact]
    public void DistinctSortedOwners_dedupes_case_insensitively_sorts_and_drops_blanks()
    {
        var owners = new List<string?> { "BOB", "alice", "", null, "bob", "Alice", "  ", "carol" };

        IReadOnlyList<string> shaped = VitalsShaping.DistinctSortedOwners(owners);

        Assert.Equal(new[] { "alice", "BOB", "carol" }, shaped);
    }

    [Fact]
    public void DistinctSortedOwners_empty_is_empty() =>
        Assert.Empty(VitalsShaping.DistinctSortedOwners([]));
}
