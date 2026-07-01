namespace Vivre.Core.Vitals;

/// <summary>
/// Pure shaping for the vitals fields the DCOM probe assembles from raw CIM results, mirroring the
/// WinRM script's shapes EXACTLY so a box read over DCOM (a Kerberos-broken / WinRM-down host) renders
/// identically to one read over WinRM. Kept in Core so the parity contracts are unit-tested directly.
/// </summary>
public static class VitalsShaping
{
    /// <summary>Max stopped-auto-service DISPLAY NAMES surfaced (the COUNT is never capped).</summary>
    public const int MaxStoppedServiceNames = 15;

    /// <summary>
    /// Shapes the raw stopped-auto-service display names (one entry per matched <c>Win32_Service</c>
    /// instance, possibly null for a service with no DisplayName) into (full count, first-N names) —
    /// matching the WinRM path (<c>$stopped.Count</c> for the count; <c>Select-Object -First 15
    /// DisplayName</c> for the names). The count reflects ALL matched instances so it stays truthful;
    /// only the display list is capped, and blank names are dropped from it so no empty rows show.
    /// </summary>
    public static (int Count, IReadOnlyList<string> Names) StoppedServices(IReadOnlyCollection<string?> displayNames) =>
        (displayNames.Count,
         displayNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).Take(MaxStoppedServiceNames).ToList());

    /// <summary>
    /// Shapes raw explorer.exe owner usernames into a distinct, sorted list (dropping blanks) — matching
    /// the WinRM path's <c>Sort-Object -Unique</c>. Case-insensitive (Windows usernames), so "BOB" and
    /// "bob" collapse to one. Mirrors the interactive-user definition (console + RDP) the WinRM path uses.
    /// </summary>
    public static IReadOnlyList<string> DistinctSortedOwners(IEnumerable<string?> owners) =>
        owners.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(u => u, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
