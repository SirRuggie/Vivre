using System.Globalization;
using System.Text;
using Vivre.Core.Models;
using Vivre.Core.Updates;

namespace Vivre.Core.Columns;

/// <summary>One exported grid column: the header text exactly as displayed, and whether it is a
/// user-defined custom column (whose cells resolve from <see cref="Computer.CustomValues"/>).</summary>
public sealed record ReportColumn(string Header, bool IsCustom = false);

/// <summary>
/// Builds the "Shown rows + columns" CSV from the ACTIVE grid's visible columns, in display order.
/// The view passes exactly the columns the clicked grid is showing — hidden columns excluded,
/// custom columns only where they actually render — so the file always matches the screen.
/// Cell text mirrors the grid's rendering (e.g. the Status pill's label overrides), not the raw
/// model, so a visual check of CSV-vs-grid lines up.
/// </summary>
public static class GridReportCsv
{
    public static string Build(IReadOnlyList<ReportColumn> columns, IEnumerable<Computer> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", columns.Select(c => Escape(c.Header))));
        foreach (Computer row in rows)
        {
            sb.AppendLine(string.Join(",", columns.Select(c => Escape(Resolve(c, row)))));
        }

        return sb.ToString();
    }

    private static string Resolve(ReportColumn column, Computer c) =>
        column.IsCustom
            ? c.CustomValues[column.Header] ?? string.Empty
            : column.Header switch
            {
                "Name" => c.Name,
                // Health's "Online" and Patching's "Ping" are the same IsOnline dot.
                "Online" or "Ping" => c.IsOnline switch { true => "Online", false => "Offline", _ => "?" },
                "Vitals" => c.VitalityScore?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                "Status" => StatusLabel(c),
                "Staged" => LcuRouting.Is2016(c.OsBuild) && c.RequiresStagedPatching ? "Staged" : string.Empty,
                "Reboot message" => c.RebootMessage ?? string.Empty,
                "Windows update message" => c.UpdateMessage ?? string.Empty,
                "Progress" => c.UpdateProgress?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                // Patching's two-line "Pending / Reboot" header and Health's "Reboot / Pending" both
                // flatten (runs joined by a space) to these keys.
                "Pending Reboot" or "Reboot Pending" => YesNo(c.RebootRequired),
                "Updates Missing" => YesNo(c.MissingUpdates),
                "Install Running" => YesNo(c.RunningUpdates),
                "Users Online" => YesNo(c.UserLoggedOn),
                "Command result" => c.CommandResult ?? string.Empty,
                "Software" => c.SoftwareCheck ?? string.Empty,
                "Site code" => c.SiteCode ?? string.Empty,
                "Agent version" => c.AgentVersion ?? string.Empty,
                "Last reboot" => c.LastRebootDisplay ?? string.Empty,
                "Last error" => c.LastError ?? string.Empty,
                "Last status" => c.LastStatus ?? string.Empty,
                // Unmapped built-in (a future column this map doesn't know yet): keep the header,
                // emit blank cells — never silently drop a column the operator can see on screen.
                _ => string.Empty,
            };

    /// <summary>
    /// The Status pill's displayed text. Mirrors the grid's trigger chain — in WPF the LAST matching
    /// trigger wins, so the checks here run in the XAML's reverse document order: Scheduled beats
    /// everything, then the UpdatePhase-specific labels, then the friendly PatchState overrides,
    /// then the base <see cref="PatchStateLabels"/> map.
    /// </summary>
    public static string StatusLabel(Computer c)
    {
        if (c.IsScheduled)
        {
            return "Scheduled";
        }

        return c.UpdatePhase switch
        {
            "Unreachable" => "Can't reach WU",
            "Cleaned" => "Cleaned",
            "Rebooting" => "Rebooting",
            _ => c.PatchState switch
            {
                PatchState.Unverified => "Unverified",
                PatchState.Done => "Up to date",
                PatchState.Available => "Updates available",
                _ => c.UpdatePhase switch
                {
                    "Cleaning" => "Cleaning up",
                    "Staging" => "Staging",
                    _ => PatchStateLabels.For(c.PatchState),
                },
            },
        };
    }

    private static string YesNo(bool? value) => value switch { true => "Yes", false => "No", _ => "?" };

    /// <summary>
    /// CSV-escapes one cell, guarding against CSV/formula injection: a value from a target machine
    /// (software DisplayName, error string, custom-column output) that starts with = + - @ is
    /// interpreted as a formula by Excel / LibreOffice when the file is opened. Prefix with a tab to
    /// neutralise it, then fall through to the quoted branch so the tab is preserved in the output cell.
    /// </summary>
    public static string Escape(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
        {
            value = "\t" + value;
        }

        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r') || value.StartsWith('\t')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
