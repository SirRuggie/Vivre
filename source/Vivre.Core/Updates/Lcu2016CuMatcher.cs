using System.Collections.Generic;
using System.Linq;

namespace Vivre.Core.Updates;

/// <summary>
/// Identifies the Windows Server 2016 OS cumulative update among a box's scanned applicable updates, so the
/// staged-patching decision dialog can compare the KB Windows actually found against the one set in Settings.
/// Pure + fail-safe: a confident single match returns its (prefix-less) KB; zero matches or an ambiguous set
/// (several distinct CU KBs) returns <see langword="null"/> so the caller never raises a false "wrong KB"
/// warning. Deliberately excludes the separate ".NET Framework" cumulative updates — those patch via WUA and
/// are not the OS LCU the DISM lane stages.
/// </summary>
public static class Lcu2016CuMatcher
{
    /// <summary>The (prefix-less) KB of the single 2016 OS cumulative update in <paramref name="applicable"/>,
    /// or null when none — or more than one distinct CU KB — is found (fail-safe: don't guess). Used for the
    /// decision dialog's Settings-vs-scan mismatch warning, where guessing among several CUs would be wrong.</summary>
    public static string? FindCuKb(IEnumerable<(string Title, string? Kb)> applicable)
    {
        string[] kbs = CuKbs(applicable).ToArray();

        // Exactly one CU KB ⇒ confident. None ⇒ nothing to compare. Several distinct ⇒ ambiguous, don't guess.
        return kbs.Length == 1 ? kbs[0] : null;
    }

    /// <summary>EVERY distinct (prefix-less) KB in <paramref name="applicable"/> whose title looks like the
    /// Server 2016 OS cumulative update. Unlike <see cref="FindCuKb"/> this does NOT collapse to a single guess —
    /// it returns all of them, so the "Install minor updates only" path can exclude every CU-looking update from
    /// WUA even when the scan is ambiguous (two distinct CU KBs). Conservative by design: the OS CU must never
    /// slip through WUA on a flagged box.</summary>
    public static HashSet<string> CuKbs(IEnumerable<(string Title, string? Kb)> applicable) =>
        applicable is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : applicable
                .Where(u => IsServer2016Cu(u.Title) && !string.IsNullOrWhiteSpace(u.Kb))
                .Select(u => NormalizeKb(u.Kb!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when a title looks like the Server 2016 / Windows 10 1607 OS cumulative update (and is not
    /// a .NET Framework cumulative update, which is a separate WUA-installable package, not the OS LCU).</summary>
    private static bool IsServer2016Cu(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        if (title.Contains("Cumulative Update", StringComparison.OrdinalIgnoreCase)
            && !title.Contains(".NET", StringComparison.OrdinalIgnoreCase))
        {
            return title.Contains("Windows Server 2016", StringComparison.OrdinalIgnoreCase)
                || title.Contains("1607", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>Strips a leading "KB" (any case) and trims, so a Settings "KB5094122" compares equal to a
    /// scan's prefix-less "5094122". Returns the trimmed input unchanged when there's no prefix.</summary>
    public static string NormalizeKb(string kb)
    {
        string trimmed = (kb ?? string.Empty).Trim();
        return trimmed.StartsWith("KB", StringComparison.OrdinalIgnoreCase)
            ? trimmed[2..].Trim()
            : trimmed;
    }
}
