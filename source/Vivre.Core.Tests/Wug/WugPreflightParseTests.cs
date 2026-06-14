using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vivre.Core.Wug;
using Xunit;

namespace Vivre.Core.Tests.Wug;

/// <summary>
/// Unit tests for the parse seams added by the WUG pre-flight feature.
/// Live Connect-WUGServer calls are operator-verified; only the JSON parse paths are tested here.
/// </summary>
public class WugPreflightParseTests
{
    // ── ParsePreflight ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParsePreflight_module_missing_returns_module_false_connected_false_with_error()
    {
        const string json = """{"modulePresent":false,"connected":false,"error":"The WhatsUpGoldPS module isn't installed."}""";

        WugPreflightResult r = WugMaintenance.ParsePreflight(json + "\n", string.Empty);

        Assert.False(r.ModulePresent);
        Assert.False(r.Connected);
        Assert.Equal("The WhatsUpGoldPS module isn't installed.", r.Error);
    }

    [Fact]
    public void ParsePreflight_connected_returns_module_true_connected_true_no_error()
    {
        const string json = """{"modulePresent":true,"connected":true,"error":null}""";

        WugPreflightResult r = WugMaintenance.ParsePreflight(json + "\n", string.Empty);

        Assert.True(r.ModulePresent);
        Assert.True(r.Connected);
        Assert.Null(r.Error);
    }

    [Fact]
    public void ParsePreflight_connect_failed_returns_module_true_connected_false_with_error()
    {
        const string errorMsg = "Couldn't reach WhatsUp Gold at 10.70.25.111 — check the address, that the server is reachable, and the username/password. (timeout)";
        string json = $$"""{"modulePresent":true,"connected":false,"error":"{{errorMsg}}"}""";

        WugPreflightResult r = WugMaintenance.ParsePreflight(json + "\n", string.Empty);

        Assert.True(r.ModulePresent);
        Assert.False(r.Connected);
        Assert.Equal(errorMsg, r.Error);
    }

    [Fact]
    public void ParsePreflight_bad_creds_returns_module_true_connected_false_with_rejected_message()
    {
        const string json = """{"modulePresent":true,"connected":false,"error":"The WhatsUp Gold username or password was rejected."}""";

        WugPreflightResult r = WugMaintenance.ParsePreflight(json + "\n", string.Empty);

        Assert.True(r.ModulePresent);
        Assert.False(r.Connected);
        Assert.Equal("The WhatsUp Gold username or password was rejected.", r.Error);
    }

    [Fact]
    public void ParsePreflight_no_json_does_not_claim_module_missing_surfaces_stderr()
    {
        // No result line (e.g. the pre-flight was killed on timeout before emitting). That says nothing
        // about whether the module is installed — it must NOT be reported as missing (which would wrongly
        // offer to reinstall a present module); it surfaces as a connection-stage failure with the detail.
        WugPreflightResult r = WugMaintenance.ParsePreflight(string.Empty, "Some error from stderr");

        Assert.True(r.ModulePresent);
        Assert.False(r.Connected);
        Assert.Equal("Some error from stderr", r.Error);
    }

    [Fact]
    public void ParsePreflight_no_json_no_stderr_does_not_claim_module_missing()
    {
        WugPreflightResult r = WugMaintenance.ParsePreflight(string.Empty, string.Empty);

        Assert.True(r.ModulePresent);
        Assert.False(r.Connected);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    [Fact]
    public void ParsePreflight_malformed_json_does_not_claim_module_missing()
    {
        WugPreflightResult r = WugMaintenance.ParsePreflight("{not valid json}", "stderr detail");

        Assert.True(r.ModulePresent);
        Assert.False(r.Connected);
        // Should contain the stderr or fallback, not throw.
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    [Fact]
    public void ParsePreflight_picks_last_json_line_ignoring_preceding_noise()
    {
        // Script might emit debug lines before the final JSON — parser should take the last JSON line.
        string stdout = "Some noise\nMore noise\n" +
                        """{"modulePresent":true,"connected":true,"error":null}""" + "\n";

        WugPreflightResult r = WugMaintenance.ParsePreflight(stdout, string.Empty);

        Assert.True(r.ModulePresent);
        Assert.True(r.Connected);
        Assert.Null(r.Error);
    }

    // ── ParsePreflight connected=false with error=null (unexpected but shouldn't throw) ──────────

    [Fact]
    public void ParsePreflight_connected_false_null_error_returns_no_error_string()
    {
        const string json = """{"modulePresent":true,"connected":false,"error":null}""";

        WugPreflightResult r = WugMaintenance.ParsePreflight(json + "\n", string.Empty);

        Assert.True(r.ModulePresent);
        Assert.False(r.Connected);
        Assert.Null(r.Error);
    }

    // ── Result-marker extraction (the script tags its result line with __WUGRESULT__) ───────────────

    [Fact]
    public void ParsePreflight_reads_marker_tagged_result_line()
    {
        string stdout = "Some module banner\n" +
                        """__WUGRESULT__{"modulePresent":true,"connected":true,"error":null}""" + "\n";

        WugPreflightResult r = WugMaintenance.ParsePreflight(stdout, string.Empty);

        Assert.True(r.ModulePresent);
        Assert.True(r.Connected);
        Assert.Null(r.Error);
    }

    [Fact]
    public void ParsePreflight_marker_line_wins_over_trailing_braced_noise()
    {
        // A cmdlet might print a brace-bearing object AFTER the result; the marker line must still win.
        string stdout = """__WUGRESULT__{"modulePresent":true,"connected":true,"error":null}""" + "\n" +
                        "Trailing @{ Name = stuff } noise\n";

        WugPreflightResult r = WugMaintenance.ParsePreflight(stdout, string.Empty);

        Assert.True(r.ModulePresent);
        Assert.True(r.Connected);
    }

    [Fact]
    public void ParsePreflight_module_missing_via_marker_still_returns_false()
    {
        string stdout = """__WUGRESULT__{"modulePresent":false,"connected":false,"error":"The WhatsUpGoldPS module isn't installed."}""" + "\n";

        WugPreflightResult r = WugMaintenance.ParsePreflight(stdout, string.Empty);

        Assert.False(r.ModulePresent);
        Assert.False(r.Connected);
    }

    [Fact]
    public void ParsePreflight_modulePresent_omitted_defaults_to_present()
    {
        // Only an explicit modulePresent:false counts as missing — an omitted field defaults to present.
        const string json = """{"connected":false,"error":"Couldn't reach the server."}""";

        WugPreflightResult r = WugMaintenance.ParsePreflight(json + "\n", string.Empty);

        Assert.True(r.ModulePresent);
        Assert.False(r.Connected);
        Assert.Equal("Couldn't reach the server.", r.Error);
    }

    // ── Script file encoding (the BOM that Windows PowerShell 5.1 needs) ─────────────────────────────

    [Fact]
    public async Task WritePs51Script_writes_utf8_with_bom()
    {
        // Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI and corrupts non-ASCII characters (an
        // em-dash in a message once broke the whole pre-flight parse). The temp script MUST start with
        // the UTF-8 BOM (EF BB BF). This guards a regression that the build alone can't catch.
        string path = Path.Combine(Path.GetTempPath(), $"Vivre_WugBomTest_{System.Guid.NewGuid():N}.ps1");
        try
        {
            await WugMaintenance.WritePs51ScriptAsync(path, "Write-Output 'em-dash —'", CancellationToken.None);

            byte[] bytes = await File.ReadAllBytesAsync(path);
            Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "Scripts handed to Windows PowerShell 5.1 must be UTF-8 with a BOM.");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
