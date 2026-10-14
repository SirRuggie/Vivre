using Vivre.Core.Wug;
using Xunit;

namespace Vivre.Core.Tests.Wug;

public class F06OutcomeParseTests
{
    [Theory]
    [InlineData("Ambiguous", WugLookupOutcome.Ambiguous)]
    [InlineData("LookupError", WugLookupOutcome.LookupError)]
    public void Unresolved_streamed_identity_retains_its_reason(string value, WugLookupOutcome expected)
    {
        var row = WugMaintenance.ParseDeviceLine(
            $$"""{"name":"DUMMY01","matched":true,"inMaintenance":null,"outcome":"{{value}}"}""");
        Assert.NotNull(row);
        Assert.Equal(expected, row.Outcome);
        Assert.Null(row.InMaintenance);
    }

    [Fact]
    public void Final_state_keeps_ambiguous_failed_and_absent_names_separate()
    {
        var result = WugMaintenance.ParseMaintenanceState(
            """__WUGRESULT__{"ok":true,"devices":[{"name":"VALID","inMaintenance":false}],"unmatched":["ABSENT"],"ambiguousNames":["DUPLICATE"],"lookupErrorNames":["FAILED"],"ambiguous":1,"lookupErrors":1}""", "");
        Assert.Equal(new[] { "ABSENT" }, result.Unmatched);
        Assert.Equal(new[] { "DUPLICATE" }, result.AmbiguousNames);
        Assert.Equal(new[] { "FAILED" }, result.LookupErrorNames);
        Assert.False(result.ByName["VALID"]);
        Assert.False(result.ByName.ContainsKey("DUPLICATE"));
        Assert.False(result.ByName.ContainsKey("FAILED"));
        Assert.Equal(1, result.Ambiguous);
        Assert.Equal(1, result.LookupErrors);
    }

    [Fact]
    public void Mixed_maintenance_result_preserves_successful_aliases_despite_overall_failure()
    {
        var result = WugMaintenance.Parse(
            """{"ok":false,"devicesSet":1,"succeededNames":["VALID","VALID.example.test"],"unmatched":["ABSENT"],"ambiguousNames":["DUPLICATE"],"lookupErrorNames":["FAILED"],"error":"Partial coverage","ambiguous":1,"lookupErrors":1}""", "");
        Assert.False(result.Ok);
        Assert.Equal(1, result.DevicesSet);
        Assert.Equal(new[] { "VALID", "VALID.example.test" }, result.SucceededNames);
        Assert.Equal(new[] { "DUPLICATE" }, result.AmbiguousNames);
        Assert.Equal(new[] { "FAILED" }, result.LookupErrorNames);
        Assert.Equal(new[] { "ABSENT" }, result.Unmatched);
        Assert.Equal("Partial coverage", result.Error);
    }

    [Fact]
    public void Aggregate_partial_success_never_invents_successful_names()
    {
        var result = WugMaintenance.Parse(
            """{"ok":false,"devicesSet":1,"succeededNames":[],"unmatched":[],"error":"One of two operations confirmed"}""", "");
        Assert.False(result.Ok);
        Assert.Equal(1, result.DevicesSet);
        Assert.NotNull(result.SucceededNames);
        Assert.Empty(result.SucceededNames);
    }

    [Fact]
    public void Legacy_streamed_unknown_stays_unknown_without_invented_lookup_reason()
    {
        var row = WugMaintenance.ParseDeviceLine(
            """{"name":"DUMMY01","matched":true,"inMaintenance":null}""");
        Assert.NotNull(row);
        Assert.Null(row.Outcome);
        Assert.Null(row.InMaintenance);
    }
}
