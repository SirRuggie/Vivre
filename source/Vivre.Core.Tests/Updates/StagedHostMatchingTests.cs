using System.Collections.Generic;
using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>Tests for <see cref="StagedHostMatching"/> — case-insensitive membership and Normalize.</summary>
public class StagedHostMatchingTests
{
    // --- IsStaged ---

    [Fact]
    public void IsStaged_exact_match_returns_true()
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NYC-FP1" };

        Assert.True(StagedHostMatching.IsStaged(hosts, "NYC-FP1"));
    }

    [Fact]
    public void IsStaged_case_insensitive_match_returns_true()
    {
        // Simulates a query whose casing differs from the persisted name.
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NYC-FP1" };

        Assert.True(StagedHostMatching.IsStaged(hosts, "nyc-fp1"));
    }

    [Fact]
    public void IsStaged_not_present_returns_false()
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NYC-FP1" };

        Assert.False(StagedHostMatching.IsStaged(hosts, "SFO-FP1"));
    }

    [Fact]
    public void IsStaged_null_hostName_returns_false()
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NYC-FP1" };

        Assert.False(StagedHostMatching.IsStaged(hosts, null));
    }

    [Fact]
    public void IsStaged_empty_hostName_returns_false()
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NYC-FP1" };

        Assert.False(StagedHostMatching.IsStaged(hosts, string.Empty));
    }

    [Fact]
    public void IsStaged_null_set_returns_false()
    {
        Assert.False(StagedHostMatching.IsStaged(null, "NYC-FP1"));
    }

    // --- Normalize ---

    [Fact]
    public void Normalize_result_contains_case_variant()
    {
        // Input casing differs from lookup casing — Normalize must produce an OIC set.
        var result = StagedHostMatching.Normalize(["NYC-FP1"]);

        Assert.Contains("nyc-fp1", result);
    }

    [Fact]
    public void Normalize_null_input_returns_empty_set()
    {
        var result = StagedHostMatching.Normalize(null);

        Assert.Empty(result);
    }

    [Fact]
    public void Normalize_over_ordinal_set_still_matches_case_insensitively()
    {
        // Simulates a JSON round-trip: deserializer produces a plain ordinal HashSet,
        // then Normalize rebuilds it with OrdinalIgnoreCase so membership works correctly.
        var ordinalSet = new HashSet<string> { "NYC-FP1" };  // ordinal comparer
        Assert.DoesNotContain("nyc-fp1", ordinalSet);        // confirm the comparer is ordinal

        var normalized = StagedHostMatching.Normalize(ordinalSet);

        Assert.True(StagedHostMatching.IsStaged(normalized, "nyc-fp1"));
    }
}
