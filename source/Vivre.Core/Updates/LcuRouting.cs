using System.Text.RegularExpressions;

namespace Vivre.Core.Updates;

/// <summary>
/// The one place that decides whether a machine belongs to the Server 2016 full-package lane. Vivre
/// encodes "which box is special" here so an operator never has to know: the self-populating 2016 panel
/// and the mixed "Install all" both ask <see cref="Is2016"/>. A build we haven't read yet is
/// <see langword="null"/> → NOT 2016 → kept out of the panel and never mis-routed (fail-safe: confirm
/// before you classify).
/// </summary>
public static partial class LcuRouting
{
    /// <summary>Windows Server 2016 / Windows 10 1607 OS build. (2019 = 17763, 2022 = 20348.)</summary>
    public const int Server2016Build = 14393;

    /// <summary>True only when the build is confirmed to be 2016 (14393). Null/unknown is deliberately
    /// false — an unread box is never treated as 2016.</summary>
    public static bool Is2016(int? osBuild) => osBuild == Server2016Build;

    /// <summary>
    /// Pulls the OS build number out of the OS string Vivre already captures (caption + version, e.g.
    /// "Windows Server 2016 Standard — 10.0.14393" → 14393). Modern Windows all report 10.0.&lt;build&gt;.
    /// Returns null when the string is absent or has no recognisable build (so the box stays unclassified).
    /// </summary>
    public static int? ParseBuild(string? operatingSystem)
    {
        if (string.IsNullOrWhiteSpace(operatingSystem))
        {
            return null;
        }

        Match m = BuildRegex().Match(operatingSystem);
        return m.Success && int.TryParse(m.Groups[1].Value, out int build) ? build : null;
    }

    // "10.0.<build>" — the version form every Windows 10 / Server 2016+ reports.
    [GeneratedRegex(@"10\.0\.(\d{3,})", RegexOptions.CultureInvariant)]
    private static partial Regex BuildRegex();
}
