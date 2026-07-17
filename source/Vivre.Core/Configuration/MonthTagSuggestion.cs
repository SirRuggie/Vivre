using System.Globalization;

namespace Vivre.Core.Configuration;

/// <summary>
/// Suggests the human month/year label for the Server 2016 CU (e.g. "July 2026") from a package file's date.
///
/// <para><b>Honesty:</b> the file date is a DOWNLOAD date (when the operator saved the .msu locally), NOT the
/// update's release date. The label this produces is a convenience GUESS the operator confirms and edits in the
/// "Read from package" dialog — never present it as a derived or authoritative release month.</para>
///
/// <para>Pure and side-effect-free. It takes an ALREADY-LOCALIZED date (the caller does <c>ToLocalTime()</c>) so
/// unit tests are timezone-independent; the month/year is formatted with the invariant culture.</para>
/// </summary>
public static class MonthTagSuggestion
{
    /// <summary>The month-year label for <paramref name="localFileDate"/>, e.g. 2026-07-16 → "July 2026".
    /// Returns <see cref="string.Empty"/> when the date is null (no single file, or the file stat failed).</summary>
    public static string SuggestFrom(DateTime? localFileDate) =>
        localFileDate is null
            ? string.Empty
            : localFileDate.Value.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
}
