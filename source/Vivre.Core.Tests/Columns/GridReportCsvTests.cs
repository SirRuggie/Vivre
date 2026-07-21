using Vivre.Core.Columns;
using Vivre.Core.Models;
using Xunit;

namespace Vivre.Core.Tests.Columns;

/// <summary>
/// <see cref="GridReportCsv"/> — the shown-rows export builds from the ACTIVE grid's visible
/// columns, passed by the view in display order. The CSV must match the grid the operator
/// right-clicked: Patching exports Patching's columns, Health exports Health's (hidden excluded,
/// custom columns where they render), and cell text mirrors the grid's rendering.
/// </summary>
public class GridReportCsvTests
{
    // The Patching grid's full visible column set, in declared order (WorkspaceView.xaml UpdateGrid).
    private static readonly string[] PatchingHeaders =
        ["Name", "Ping", "Vitals", "Status", "Staged", "Reboot message", "Windows update message", "Progress", "Pending Reboot", "Command result"];

    private static List<ReportColumn> Columns(params string[] headers) =>
        [.. headers.Select(h => new ReportColumn(h))];

    private static string[] Lines(string csv) =>
        csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Patching_export_emits_the_patching_grids_columns_and_nothing_else()
    {
        var c = new Computer("AZR-P1")
        {
            IsOnline = true,
            VitalityScore = 88,
            UpdatePhase = "Done",
            RebootRequired = false,
            RebootMessage = "Back online — verifying",
            UpdateMessage = "Up to date",
            UpdateProgress = 100,
            CommandResult = "OK",
            OsBuild = 14393,
            RequiresStagedPatching = true,
            // Health-only data must NOT leak into a Patching export:
            SoftwareCheck = "CrowdStrike 7.18",
        };
        c.CustomValues["Serial"] = "ABC123"; // custom columns render on Health only → not passed → absent

        string[] lines = Lines(GridReportCsv.Build(Columns(PatchingHeaders), [c]));

        Assert.Equal("Name,Ping,Vitals,Status,Staged,Reboot message,Windows update message,Progress,Pending Reboot,Command result", lines[0]);
        Assert.Equal("AZR-P1,Online,88,Up to date,Staged,Back online — verifying,Up to date,100,No,OK", lines[1]);
        Assert.DoesNotContain("CrowdStrike", lines[1]);
        Assert.DoesNotContain("ABC123", lines[1]);
    }

    [Fact]
    public void Health_export_respects_display_order_hidden_columns_and_custom_columns()
    {
        var c = new Computer("SRV1")
        {
            IsOnline = false,
            SiteCode = "ABC",
            RebootRequired = true,
            LastStatus = "Offline",
        };
        c.CustomValues["Serial"] = "XYZ-9";

        // The view passes only what the grid shows, in display order: "Software" is hidden (absent),
        // and the custom "Serial" column sits where the user dragged it (mid-list).
        List<ReportColumn> cols =
            [new("Name"), new("Online"), new("Serial", IsCustom: true), new("Site code"), new("Reboot Pending"), new("Last status")];

        string[] lines = Lines(GridReportCsv.Build(cols, [c]));

        Assert.Equal("Name,Online,Serial,Site code,Reboot Pending,Last status", lines[0]);
        Assert.Equal("SRV1,Offline,XYZ-9,ABC,Yes,Offline", lines[1]);
    }

    [Fact]
    public void Custom_column_with_no_value_yet_emits_a_blank_cell()
    {
        var c = new Computer("SRV1");

        string[] lines = Lines(GridReportCsv.Build([new("Name"), new ReportColumn("Serial", IsCustom: true)], [c]));

        Assert.Equal("SRV1,", lines[1]);
    }

    // Cell text must mirror the grid's Status chip, including the friendly overrides and the
    // phase-specific labels (trigger precedence: see GridReportCsv.StatusLabel).
    [Theory]
    [InlineData("Done", false, "Up to date")]
    [InlineData("Done", true, "Reboot pending")]
    [InlineData("Available", false, "Updates available")]
    [InlineData("Scanning", false, "Scanning")]
    [InlineData("Staging", false, "Staging")]
    [InlineData("Cleaning", false, "Cleaning up")]
    [InlineData("Cleaned", false, "Cleaned")]
    [InlineData("Rebooting", true, "Rebooting")]
    [InlineData("Unreachable", false, "Can't reach WU")]
    [InlineData("Unverified", false, "Unverified")]
    [InlineData("Error", false, "Error")]
    public void Status_cell_matches_the_grids_chip_label(string phase, bool rebootRequired, string expected)
    {
        var c = new Computer("H") { UpdatePhase = phase, RebootRequired = rebootRequired };

        Assert.Equal(expected, GridReportCsv.StatusLabel(c));
    }

    [Fact]
    public void Scheduled_overrides_every_other_status_label()
    {
        var c = new Computer("H") { UpdatePhase = "Available", ScheduledNextRun = new DateTime(2026, 7, 22, 2, 0, 0) };

        Assert.Equal("Scheduled", GridReportCsv.StatusLabel(c));
    }

    [Fact]
    public void Cells_are_escaped_and_formula_injection_is_neutralised()
    {
        var c = new Computer("EVIL")
        {
            UpdateMessage = "a,b \"q\"",
            CommandResult = "=cmd|' /C calc'!A0",
        };

        string[] lines = Lines(GridReportCsv.Build(Columns("Name", "Windows update message", "Command result"), [c]));

        Assert.Equal("EVIL,\"a,b \"\"q\"\"\",\"\t=cmd|' /C calc'!A0\"", lines[1]);
    }

    [Fact]
    public void Unknown_builtin_header_keeps_the_column_and_emits_blank_cells()
    {
        string[] lines = Lines(GridReportCsv.Build(Columns("Name", "Mystery"), [new Computer("A")]));

        Assert.Equal("Name,Mystery", lines[0]);
        Assert.Equal("A,", lines[1]);
    }

    [Fact]
    public void Staged_cell_is_blank_unless_a_2016_box_is_flagged()
    {
        var flagged2019 = new Computer("A") { OsBuild = 17763, RequiresStagedPatching = true };
        var unflagged2016 = new Computer("B") { OsBuild = 14393 };

        string[] lines = Lines(GridReportCsv.Build(Columns("Name", "Staged"), [flagged2019, unflagged2016]));

        Assert.Equal("A,", lines[1]);
        Assert.Equal("B,", lines[2]);
    }

    [Fact]
    public void No_rows_yields_header_only()
    {
        string[] lines = Lines(GridReportCsv.Build(Columns("Name", "Ping"), []));

        Assert.Equal(["Name,Ping"], lines);
    }
}
