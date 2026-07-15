using Vivre.Core.Wug;
using Xunit;

namespace Vivre.Core.Tests.Wug;

/// <summary>
/// Unit tests for the parse + record additions from the FQDN-aware resolver fix:
/// <c>WugDeviceState.MatchedByIp</c>, the <c>WugMaintenanceStateResult</c> honesty counts
/// (LookupErrors / Ambiguous / MatchedByIp), and the DEFAULTED-record back-compat that keeps every
/// pre-fix 3-arg construction site compiling and reading zero.
/// </summary>
public class WugResolverParseTests
{
    // ── ParseDeviceLine: the optional matchedByIp flag ───────────────────────────────────────────────

    [Fact]
    public void ParseDeviceLine_matchedByIp_true_reads_true()
    {
        WugDeviceState? d = WugMaintenance.ParseDeviceLine(
            """{"name":"BOX1","matched":true,"inMaintenance":true,"matchedByIp":true}""");

        Assert.NotNull(d);
        Assert.True(d!.MatchedByIp);
    }

    [Fact]
    public void ParseDeviceLine_matchedByIp_absent_defaults_false()
    {
        // The field is emitted ONLY when true — an absent field must read false, never throw.
        WugDeviceState? d = WugMaintenance.ParseDeviceLine(
            """{"name":"BOX1","matched":true,"inMaintenance":true}""");

        Assert.NotNull(d);
        Assert.False(d!.MatchedByIp);
    }

    [Theory]
    [InlineData("""{"name":"BOX1","matched":true,"inMaintenance":true,"matchedByIp":false}""")]
    [InlineData("""{"name":"BOX1","matched":true,"inMaintenance":true,"matchedByIp":"yes"}""")]
    [InlineData("""{"name":"BOX1","matched":true,"inMaintenance":true,"matchedByIp":1}""")]
    [InlineData("""{"name":"BOX1","matched":true,"inMaintenance":true,"matchedByIp":null}""")]
    public void ParseDeviceLine_matchedByIp_non_true_reads_false(string json)
    {
        // Anything that is not a JSON true (false / string / number / null) reads false — never throws.
        WugDeviceState? d = WugMaintenance.ParseDeviceLine(json);

        Assert.NotNull(d);
        Assert.False(d!.MatchedByIp);
    }

    // ── ParseMaintenanceState: the new summary counts ────────────────────────────────────────────────

    [Fact]
    public void ParseMaintenanceState_reads_new_summary_counts_when_present()
    {
        string stdout = "__WUGRESULT__" +
            """{"ok":true,"devices":[{"name":"BOX1","inMaintenance":true}],"unmatched":[],"error":"2 of 5 lookups failed - boom","lookupErrors":2,"ambiguous":3,"matchedByIp":1}""" + "\n";

        WugMaintenanceStateResult r = WugMaintenance.ParseMaintenanceState(stdout, string.Empty);

        Assert.Equal(2, r.LookupErrors);
        Assert.Equal(3, r.Ambiguous);
        Assert.Equal(1, r.MatchedByIp);
        Assert.Equal("2 of 5 lookups failed - boom", r.Error);
        Assert.True(r.ByName["BOX1"]);
    }

    [Fact]
    public void ParseMaintenanceState_absent_summary_counts_default_to_zero()
    {
        // A pre-fix summary with no count fields must parse to zeros (back-compat), never throw.
        string stdout = """__WUGRESULT__{"ok":true,"devices":[{"name":"BOX1","inMaintenance":true}],"unmatched":[],"error":null}""" + "\n";

        WugMaintenanceStateResult r = WugMaintenance.ParseMaintenanceState(stdout, string.Empty);

        Assert.Equal(0, r.LookupErrors);
        Assert.Equal(0, r.Ambiguous);
        Assert.Equal(0, r.MatchedByIp);
    }

    [Fact]
    public void ParseMaintenanceState_non_numeric_count_defaults_to_zero_without_throwing()
    {
        // A malformed (non-number) count must fall back to 0, not throw through the JsonException-only catch.
        string stdout = """__WUGRESULT__{"ok":true,"devices":[],"unmatched":[],"error":null,"lookupErrors":"lots","ambiguous":null}""" + "\n";

        WugMaintenanceStateResult r = WugMaintenance.ParseMaintenanceState(stdout, string.Empty);

        Assert.Equal(0, r.LookupErrors);
        Assert.Equal(0, r.Ambiguous);
    }

    // ── Defaulted-record back-compat: pre-fix construction sites keep compiling and read zero/false ──

    [Fact]
    public void WugMaintenanceStateResult_three_arg_construction_yields_zero_counts()
    {
        // The exact shape WorkspaceViewModel + the abort paths construct — must still compile and default.
        var r = new WugMaintenanceStateResult(
            new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase), [], "some error");

        Assert.Equal(0, r.LookupErrors);
        Assert.Equal(0, r.Ambiguous);
        Assert.Equal(0, r.MatchedByIp);
        Assert.Equal("some error", r.Error);
    }

    [Fact]
    public void WugDeviceState_three_arg_construction_defaults_matchedByIp_false()
    {
        var d = new WugDeviceState("BOX1", true, false);

        Assert.False(d.MatchedByIp);
    }
}
