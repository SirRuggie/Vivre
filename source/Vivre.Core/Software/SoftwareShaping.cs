namespace Vivre.Core.Software;

/// <summary>One uninstall-registry entry's shaping-relevant values.</summary>
public sealed record UninstallRow(string? DisplayName, string? DisplayVersion, string? Publisher);

/// <summary>
/// Pure shaping for the installed-software answer the DCOM reader assembles from raw uninstall-registry
/// rows, mirroring the WinRM script's match/sort semantics EXACTLY so a box read over DCOM (a
/// Kerberos-broken host) renders identically to one read over WinRM. Kept in Core so the parity
/// contracts are unit-tested directly.
/// </summary>
public static class SoftwareShaping
{
    /// <summary>
    /// Finds the first product whose DisplayName or Publisher contains <paramref name="query"/> among
    /// <paramref name="rows"/>. Rows with a blank DisplayName are dropped (parity with the WinRM script's
    /// <c>$_.DisplayName -and</c> guard). Matching is a case-insensitive ORDINAL substring test — the
    /// WinRM script wildcard-escapes the query then <c>-like "*q*"</c>, i.e. a literal substring, so
    /// <c>* ? [ ]</c> in the query match literally. Matched rows are ordered by DisplayName
    /// (OrdinalIgnoreCase) and the first wins; Version is the DisplayVersion, null when blank.
    /// </summary>
    public static (bool Found, string? Name, string? Version) Match(IReadOnlyList<UninstallRow> rows, string query)
    {
        // PS Sort-Object is culture-aware, so multi-match ordering is best-effort parity; a single-match
        // query (the norm) is exact regardless.
        UninstallRow? winner = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.DisplayName))
            .Where(r => Contains(r.DisplayName, query) || Contains(r.Publisher, query))
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (winner is null)
        {
            return (false, null, null);
        }

        string? version = string.IsNullOrWhiteSpace(winner.DisplayVersion) ? null : winner.DisplayVersion;
        return (true, winner.DisplayName, version);
    }

    /// <summary>
    /// Resolves a match across the two uninstall hives (64-bit then WOW6432Node). Each hive is matched
    /// independently and the FIRST hive with any match wins — even when the other hive holds an
    /// alphabetically-earlier DisplayName (parity with the WinRM script's foreach-break). NEVER
    /// concatenate the hives and sort globally: that would let a later hive's alphabetically-earlier name
    /// silently override the first hive's match, diverging from the WinRM path.
    /// </summary>
    public static (bool Found, string? Name, string? Version) MatchAcrossHives(
        IReadOnlyList<UninstallRow> hive1Rows, IReadOnlyList<UninstallRow> hive2Rows, string query)
    {
        (bool found, string? name, string? version) = Match(hive1Rows, query);
        return found ? (found, name, version) : Match(hive2Rows, query);
    }

    /// <summary>
    /// Normalizes a service state to the Get-Service spelling the WinRM path emits. <c>Win32_Service.State</c>
    /// (the DCOM source) uses spaced spellings ("Start Pending"); Get-Service emits
    /// <c>ServiceControllerStatus.ToString()</c> ("StartPending"). Running/Stopped/Paused — and any
    /// unrecognized value — pass through unchanged.
    /// </summary>
    public static string NormalizeServiceState(string state) => state switch
    {
        "Start Pending" => "StartPending",
        "Stop Pending" => "StopPending",
        "Continue Pending" => "ContinuePending",
        "Pause Pending" => "PausePending",
        _ => state,
    };

    private static bool Contains(string? value, string query) =>
        value is not null && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
}
