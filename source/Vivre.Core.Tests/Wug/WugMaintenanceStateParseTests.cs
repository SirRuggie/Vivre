using Vivre.Core.Wug;
using Xunit;

namespace Vivre.Core.Tests.Wug;

/// <summary>
/// Unit tests for the parse seam added by the WUG maintenance-state read.
/// Live Get-WUGDevice calls are operator-verified; only the JSON parse paths are tested here. The
/// read FAILS OPEN — an unreadable state must surface as null (unknown), never a fabricated "false".
/// </summary>
public class WugMaintenanceStateParseTests
{
    [Fact]
    public void ParseMaintenanceState_device_in_maintenance_reads_true()
    {
        string stdout = """__WUGRESULT__{"ok":true,"devices":[{"name":"BOX1","inMaintenance":true}],"unmatched":[],"error":null}""" + "\n";

        WugMaintenanceStateResult r = WugMaintenance.ParseMaintenanceState(stdout, string.Empty);

        Assert.True(r.ByName["BOX1"]);
        Assert.Null(r.Error);
    }

    [Fact]
    public void ParseMaintenanceState_device_not_in_maintenance_reads_false()
    {
        string stdout = """__WUGRESULT__{"ok":true,"devices":[{"name":"BOX1","inMaintenance":false}],"unmatched":[],"error":null}""" + "\n";

        WugMaintenanceStateResult r = WugMaintenance.ParseMaintenanceState(stdout, string.Empty);

        Assert.False(r.ByName["BOX1"]);
    }

    [Fact]
    public void ParseMaintenanceState_missing_inMaintenance_reads_null_not_false()
    {
        // The state fields were absent on the WUG object => UNKNOWN. Must be null, never a false that
        // would read as a definite "not in maintenance".
        string stdout = """__WUGRESULT__{"ok":true,"devices":[{"name":"BOX1"}],"unmatched":[],"error":null}""" + "\n";

        WugMaintenanceStateResult r = WugMaintenance.ParseMaintenanceState(stdout, string.Empty);

        Assert.True(r.ByName.ContainsKey("BOX1"));
        Assert.Null(r.ByName["BOX1"]);
    }

    [Fact]
    public void ParseMaintenanceState_devices_as_single_object_parses_one_device()
    {
        // A one-device result serializes as a single JSON object, not an array — must still parse.
        string stdout = """__WUGRESULT__{"ok":true,"devices":{"name":"BOX1","inMaintenance":true},"unmatched":[],"error":null}""" + "\n";

        WugMaintenanceStateResult r = WugMaintenance.ParseMaintenanceState(stdout, string.Empty);

        Assert.Single(r.ByName);
        Assert.True(r.ByName["BOX1"]);
    }

    [Fact]
    public void ParseMaintenanceState_no_output_with_stderr_returns_empty_map_and_surfaces_stderr()
    {
        // No result line (e.g. killed on timeout before emitting). Empty map (all unknown), error carries
        // the stderr — never a fabricated state.
        WugMaintenanceStateResult r = WugMaintenance.ParseMaintenanceState(string.Empty, "Some error from stderr");

        Assert.Empty(r.ByName);
        Assert.Equal("Some error from stderr", r.Error);
    }

    [Fact]
    public void ParseMaintenanceState_no_output_no_stderr_returns_nonblank_error()
    {
        WugMaintenanceStateResult r = WugMaintenance.ParseMaintenanceState(string.Empty, string.Empty);

        Assert.Empty(r.ByName);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    [Fact]
    public void ParseMaintenanceState_malformed_json_is_typed_failure_not_a_throw()
    {
        WugMaintenanceStateResult r = WugMaintenance.ParseMaintenanceState("{not valid json}", "stderr detail");

        Assert.Empty(r.ByName);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    [Fact]
    public void ParseMaintenanceState_marker_line_wins_over_trailing_braced_noise()
    {
        // A cmdlet might print a brace-bearing object AFTER the result; the marker line must still win.
        string stdout = """__WUGRESULT__{"ok":true,"devices":[{"name":"BOX1","inMaintenance":true}],"unmatched":[],"error":null}""" + "\n" +
                        "Trailing @{ Name = stuff } noise\n";

        WugMaintenanceStateResult r = WugMaintenance.ParseMaintenanceState(stdout, string.Empty);

        Assert.True(r.ByName["BOX1"]);
    }

    [Fact]
    public void ParseMaintenanceState_mixed_multi_device_keeps_states_and_unmatched_separate()
    {
        // One true, one false, one unknown (no inMaintenance), plus two unmatched names kept out of ByName.
        string stdout = "__WUGRESULT__" +
                        """{"ok":true,"devices":[{"name":"BOX1","inMaintenance":true},{"name":"BOX2","inMaintenance":false},{"name":"BOX3"}],"unmatched":["GHOST1","GHOST2"],"error":null}""" + "\n";

        WugMaintenanceStateResult r = WugMaintenance.ParseMaintenanceState(stdout, string.Empty);

        Assert.True(r.ByName["BOX1"]);
        Assert.False(r.ByName["BOX2"]);
        Assert.Null(r.ByName["BOX3"]);
        Assert.Equal(3, r.ByName.Count);

        Assert.Equal(new[] { "GHOST1", "GHOST2" }, r.Unmatched);
        Assert.DoesNotContain("GHOST1", r.ByName.Keys);
    }

    [Fact]
    public void ParseMaintenanceState_ByName_lookup_is_case_insensitive()
    {
        // The map is keyed by the input name case-insensitively — a differently-cased lookup must hit.
        string stdout = """__WUGRESULT__{"ok":true,"devices":[{"name":"Box1","inMaintenance":true}],"unmatched":[],"error":null}""" + "\n";

        WugMaintenanceStateResult r = WugMaintenance.ParseMaintenanceState(stdout, string.Empty);

        Assert.True(r.ByName["bOx1"]);
    }
}
