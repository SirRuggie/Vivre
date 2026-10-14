using Vivre.Core.Columns;
using Vivre.Core.Models;
using Xunit;

namespace Vivre.Core.Tests.Columns;

public class SoftwareReportCsvTests
{
    private static string[] Row(Computer computer) => SoftwareReportCsv.Build([computer])
        .Split(Environment.NewLine)[1].Split(',');

    [Fact]
    public void FailedCheck_IsUnknownWithReason_NotAbsent()
    {
        string[] row = Row(new Computer("DUMMY-01")
        {
            SoftwareQuery = "Dummy product", SoftwareCheck = "Could not check — denied",
            SoftwareError = "denied", SoftwareServiceName = "DummySvc",
        });
        Assert.Equal("Unknown", row[3]);
        Assert.Equal("Unknown", row[7]);
        Assert.Equal("denied", row[10]);
        Assert.Equal(13, row.Length);
    }

    [Fact]
    public void CompletedAbsence_IsNo_AndVerifiedMissingServiceIsExplicit()
    {
        string[] row = Row(new Computer("DUMMY-01")
        {
            SoftwareFound = false, SoftwareCheck = "Not installed — Dummy",
            SoftwareServiceName = "DummySvc", SoftwareServiceState = "not found",
        });
        Assert.Equal("No", row[3]);
        Assert.Equal("Not found", row[7]);
    }

    [Fact]
    public void ProvenMatch_WithFailures_PreservesYesAndUnknownService()
    {
        string[] row = Row(new Computer("DUMMY-01")
        {
            SoftwareFound = true, SoftwareName = "Dummy", SoftwareVersion = "1.2",
            SoftwareCheck = "Installed — Dummy · partial check", SoftwareQueryWarning = "registry denied",
            SoftwareServiceName = "DummySvc", SoftwareServiceError = "service denied",
            SoftwareServiceState = "Stopped", // An error takes precedence over any stale state.
        });
        Assert.Equal("Yes", row[3]);
        Assert.Equal("Dummy", row[4]);
        Assert.Equal("1.2", row[5]);
        Assert.Equal("Unknown", row[7]);
        Assert.Equal("registry denied", row[11]);
        Assert.Equal("service denied", row[12]);
    }

    [Fact]
    public void UncheckedRow_DoesNotInventAnOutcome()
    {
        string[] row = Row(new Computer("DUMMY-01"));
        Assert.Empty(row[3]);
        Assert.Empty(row[7]);
        Assert.Empty(row[8]);
        Assert.Empty(row[9]);
    }

    [Fact]
    public void DiagnosticsAndResults_UseFormulaAndQuoteEscaping()
    {
        string csv = SoftwareReportCsv.Build([new Computer("DUMMY-01")
        {
            SoftwareCheck = "=dummy", SoftwareError = "denied, registry",
            SoftwareQueryWarning = "+dummy", SoftwareServiceError = "@dummy",
        }]);
        Assert.Contains(GridReportCsv.Escape("=dummy"), csv);
        Assert.Contains(GridReportCsv.Escape("denied, registry"), csv);
        Assert.Contains(GridReportCsv.Escape("+dummy"), csv);
        Assert.Contains(GridReportCsv.Escape("@dummy"), csv);
    }
}
