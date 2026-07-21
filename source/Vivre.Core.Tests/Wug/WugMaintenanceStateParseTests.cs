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
    public void ParseMaintenanceState_summary_detail_lands_in_DetailsByName()
    {
        string stdout = """__WUGRESULT__{"ok":true,"devices":[{"name":"BOX1","inMaintenance":true,"reason":"Rebuilding - SB","user":"admin_sbridges","sinceUtc":"2026-05-27T21:14:42Z"}],"unmatched":[],"error":null}""" + "\n";

        WugMaintenanceStateResult r = WugMaintenance.ParseMaintenanceState(stdout, string.Empty);

        Assert.True(r.ByName["BOX1"]);
        Assert.NotNull(r.DetailsByName);
        WugMaintenanceDetail d = r.DetailsByName!["BOX1"];
        Assert.Equal("Rebuilding - SB", d.Reason);
        Assert.Equal("admin_sbridges", d.User);
        Assert.Equal("2026-05-27T21:14:42Z", d.SinceUtc);
    }

    [Fact]
    public void ParseMaintenanceState_device_without_detail_gets_no_DetailsByName_entry()
    {
        string stdout = """__WUGRESULT__{"ok":true,"devices":[{"name":"BOX1","inMaintenance":true},{"name":"BOX2","inMaintenance":true,"reason":"r"}],"unmatched":[],"error":null}""" + "\n";

        WugMaintenanceStateResult r = WugMaintenance.ParseMaintenanceState(stdout, string.Empty);

        Assert.NotNull(r.DetailsByName);
        Assert.False(r.DetailsByName!.ContainsKey("BOX1"));
        Assert.Equal("r", r.DetailsByName["BOX2"].Reason);
    }

    [Fact]
    public void ParseMaintenanceState_malformed_detail_never_touches_the_state()
    {
        // Detail is display-only: number/whitespace detail fields are dropped, the tri-state stands.
        string stdout = """__WUGRESULT__{"ok":true,"devices":[{"name":"BOX1","inMaintenance":true,"reason":42,"user":"  ","sinceUtc":false}],"unmatched":[],"error":null}""" + "\n";

        WugMaintenanceStateResult r = WugMaintenance.ParseMaintenanceState(stdout, string.Empty);

        Assert.True(r.ByName["BOX1"]);
        Assert.False(r.DetailsByName!.ContainsKey("BOX1"));
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

    // ── The __WUGRESULT__ marker is now REQUIRED (the last-braced-line fallback was removed) ──────────

    [Fact]
    public void ParseMaintenanceState_device_lines_without_result_marker_is_error_not_a_clean_read()
    {
        // Streamed __WUGDEV__ lines with NO __WUGRESULT__ summary must NOT be misparsed as a clean read.
        // Before the fallback removal a braced device line could have been taken as the summary → a
        // fabricated clean-but-empty state. Now: no marker → typed error, empty map. Locks the fix.
        string stdout =
            """__WUGDEV__{"name":"BOX1","matched":true,"inMaintenance":true}""" + "\n" +
            """__WUGDEV__{"name":"BOX2","matched":true,"inMaintenance":false}""" + "\n";

        WugMaintenanceStateResult r = WugMaintenance.ParseMaintenanceState(stdout, string.Empty);

        Assert.Empty(r.ByName);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    [Fact]
    public void ParseMaintenanceState_braced_line_without_marker_is_error_not_parsed()
    {
        // A braced JSON summary-shaped line with no __WUGRESULT__ marker must NOT be parsed (fallback removed).
        string stdout = """{"ok":true,"devices":[{"name":"BOX1","inMaintenance":true}],"unmatched":[],"error":null}""" + "\n";

        WugMaintenanceStateResult r = WugMaintenance.ParseMaintenanceState(stdout, string.Empty);

        Assert.Empty(r.ByName);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }
}
