using System.Collections.Generic;
using System.Linq;

namespace Vivre.Core.Updates;

/// <summary>Pure host-set helpers for the "needs staged patching" persisted set — case-insensitive
/// membership and normalization, independent of any settings type so they're unit-testable and robust
/// to a JSON-round-tripped set whose comparer may have reset to ordinal.</summary>
public static class StagedHostMatching
{
    /// <summary>Rebuilds a host set with OrdinalIgnoreCase (call after deserialization).</summary>
    public static HashSet<string> Normalize(IEnumerable<string>? hosts) =>
        new(hosts ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Case-insensitive membership — robust regardless of the source set's comparer.</summary>
    public static bool IsStaged(IEnumerable<string>? stagedHosts, string? hostName) =>
        !string.IsNullOrEmpty(hostName) && stagedHosts is not null
        && stagedHosts.Any(h => string.Equals(h, hostName, StringComparison.OrdinalIgnoreCase));
}
