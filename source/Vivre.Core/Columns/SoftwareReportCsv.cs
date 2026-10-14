using System.Text;
using Vivre.Core.Models;

namespace Vivre.Core.Columns;

/// <summary>Filtered software results with explicit unknowns and separate diagnostic fields.</summary>
public static class SoftwareReportCsv
{
    public static string Build(IEnumerable<Computer> computers)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Machine,Online,Searched for,Installed,Product,Version,Service,Service running,Checked,Result,Check error,Query warning,Service check error");
        foreach (Computer c in computers)
        {
            bool attempted = c.SoftwareCheck is not null || c.SoftwareCheckedAt is not null || c.SoftwareError is not null;
            string service = !string.IsNullOrWhiteSpace(c.SoftwareServiceError) ? "Unknown"
                : c.SoftwareServiceState switch
                {
                    null => attempted && !string.IsNullOrWhiteSpace(c.SoftwareServiceName) ? "Unknown" : string.Empty,
                    "not found" => "Not found",
                    "Running" => "Yes",
                    _ => "No",
                };
            string[] values =
            [
                c.Name,
                c.IsOnline switch { true => "Online", false => "Offline", _ => "Unknown" },
                c.SoftwareQuery ?? string.Empty,
                c.SoftwareFound switch { true => "Yes", false => "No", _ => attempted ? "Unknown" : string.Empty },
                c.SoftwareName ?? string.Empty,
                c.SoftwareVersion ?? string.Empty,
                c.SoftwareServiceName ?? string.Empty,
                service,
                c.SoftwareCheckedAt?.ToString("yyyy-MM-dd HH:mm") ?? string.Empty,
                c.SoftwareCheck ?? string.Empty,
                c.SoftwareError ?? string.Empty,
                c.SoftwareQueryWarning ?? string.Empty,
                c.SoftwareServiceError ?? string.Empty,
            ];
            sb.AppendLine(string.Join(",", values.Select(GridReportCsv.Escape)));
        }
        return sb.ToString();
    }
}
