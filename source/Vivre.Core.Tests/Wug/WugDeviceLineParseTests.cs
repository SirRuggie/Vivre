using Vivre.Core.Wug;
using Xunit;

namespace Vivre.Core.Tests.Wug;

/// <summary>
/// Unit tests for ParseDeviceLine — the per-device streaming parse — and ComposeAbortError. The per-line
/// parser MUST stay in lockstep with AddDevice's tri-state contract: an unreadable state is null
/// (unknown), NEVER a fabricated false; a miss is only ever an explicit matched:false.
/// </summary>
public class WugDeviceLineParseTests
{
    [Fact]
    public void ParseDeviceLine_inMaintenance_true_reads_true()
    {
        WugDeviceState? d = WugMaintenance.ParseDeviceLine("""{"name":"BOX1","matched":true,"inMaintenance":true}""");

        Assert.NotNull(d);
        Assert.Equal("BOX1", d!.Name);
        Assert.True(d.Matched);
        Assert.True(d.InMaintenance);
    }

    [Fact]
    public void ParseDeviceLine_inMaintenance_false_reads_false()
    {
        WugDeviceState? d = WugMaintenance.ParseDeviceLine("""{"name":"BOX1","matched":true,"inMaintenance":false}""");

        Assert.NotNull(d);
        Assert.False(d!.InMaintenance);
    }

    [Fact]
    public void ParseDeviceLine_absent_inMaintenance_reads_null_not_false()
    {
        // Absent state field => UNKNOWN. Must be null, never a false that reads as a definite "not in maintenance".
        WugDeviceState? d = WugMaintenance.ParseDeviceLine("""{"name":"BOX1","matched":true}""");

        Assert.NotNull(d);
        Assert.Null(d!.InMaintenance);
    }

    [Fact]
    public void ParseDeviceLine_explicit_json_null_inMaintenance_reads_null()
    {
        // An explicit JSON null is still UNKNOWN — never coerced to false.
        WugDeviceState? d = WugMaintenance.ParseDeviceLine("""{"name":"BOX1","matched":true,"inMaintenance":null}""");

        Assert.NotNull(d);
        Assert.Null(d!.InMaintenance);
    }

    [Fact]
    public void ParseDeviceLine_matched_false_reads_false()
    {
        WugDeviceState? d = WugMaintenance.ParseDeviceLine("""{"name":"GHOST","matched":false,"inMaintenance":null}""");

        Assert.NotNull(d);
        Assert.False(d!.Matched);
        Assert.Null(d.InMaintenance);
    }

    [Fact]
    public void ParseDeviceLine_absent_matched_defaults_to_true()
    {
        // Only an explicit matched:false is a miss; an omitted field defaults to matched.
        WugDeviceState? d = WugMaintenance.ParseDeviceLine("""{"name":"BOX1","inMaintenance":true}""");

        Assert.NotNull(d);
        Assert.True(d!.Matched);
    }

    [Fact]
    public void ParseDeviceLine_missing_name_returns_null()
    {
        Assert.Null(WugMaintenance.ParseDeviceLine("""{"matched":true,"inMaintenance":true}"""));
    }

    [Fact]
    public void ParseDeviceLine_non_string_name_returns_null()
    {
        Assert.Null(WugMaintenance.ParseDeviceLine("""{"name":123,"matched":true,"inMaintenance":true}"""));
    }

    [Fact]
    public void ParseDeviceLine_malformed_json_returns_null_without_throwing()
    {
        Assert.Null(WugMaintenance.ParseDeviceLine("{not valid json}"));
    }

    [Fact]
    public void ParseDeviceLine_escaped_non_ascii_name_decodes_to_exact_unicode()
    {
        // 5.1's ConvertTo-Json escapes non-ASCII to \uXXXX on the wire; the parse must decode it back to
        // the exact Unicode string. The \\u00e9 in this C# literal becomes the on-the-wire JSON escape
        // é, so this feeds ParseDeviceLine the real escaped form for "café-01".
        WugDeviceState? d = WugMaintenance.ParseDeviceLine("{\"name\":\"caf\\u00e9-01\",\"matched\":true,\"inMaintenance\":true}");

        Assert.NotNull(d);
        Assert.Equal("café-01", d!.Name);
    }

    // ── Optional maintenance detail (reason / user / sinceUtc) — display-only, never state ──────────

    [Fact]
    public void ParseDeviceLine_reads_the_full_maintenance_detail()
    {
        WugDeviceState? d = WugMaintenance.ParseDeviceLine(
            """{"name":"BOX1","matched":true,"inMaintenance":true,"reason":"Rebuilding - SB","user":"admin_sbridges","sinceUtc":"2026-05-27T21:14:42Z"}""");

        Assert.NotNull(d);
        Assert.True(d!.InMaintenance);
        Assert.Equal("Rebuilding - SB", d.Reason);
        Assert.Equal("admin_sbridges", d.User);
        Assert.Equal("2026-05-27T21:14:42Z", d.SinceUtc);
    }

    [Fact]
    public void ParseDeviceLine_absent_detail_fields_read_null()
    {
        WugDeviceState? d = WugMaintenance.ParseDeviceLine("""{"name":"BOX1","matched":true,"inMaintenance":true}""");

        Assert.NotNull(d);
        Assert.Null(d!.Reason);
        Assert.Null(d.User);
        Assert.Null(d.SinceUtc);
    }

    [Fact]
    public void ParseDeviceLine_non_string_or_whitespace_detail_fields_read_null()
    {
        // A malformed detail can only DROP display text — number/null/whitespace never become text and
        // never touch the state.
        WugDeviceState? d = WugMaintenance.ParseDeviceLine(
            """{"name":"BOX1","matched":true,"inMaintenance":true,"reason":123,"user":null,"sinceUtc":"   "}""");

        Assert.NotNull(d);
        Assert.True(d!.InMaintenance);
        Assert.Null(d.Reason);
        Assert.Null(d.User);
        Assert.Null(d.SinceUtc);
    }

    [Fact]
    public void ParseDeviceLine_detail_values_are_trimmed()
    {
        WugDeviceState? d = WugMaintenance.ParseDeviceLine(
            """{"name":"BOX1","matched":true,"inMaintenance":true,"reason":"  spaced  ","user":" u1 "}""");

        Assert.NotNull(d);
        Assert.Equal("spaced", d!.Reason);
        Assert.Equal("u1", d.User);
    }

    // ── ComposeAbortError: the two exact formats ────────────────────────────────────────────────────

    [Fact]
    public void ComposeAbortError_seen_positive_names_last_machine()
    {
        string msg = WugMaintenance.ComposeAbortError("Stalled", "BOX7", 7, 20, TimeSpan.FromSeconds(90));

        Assert.Equal("Stalled after BOX7 — 7 of 20 checked (no result for 90s)", msg);
    }

    [Fact]
    public void ComposeAbortError_seen_zero_says_before_first_result()
    {
        string msg = WugMaintenance.ComposeAbortError("Timed out", null, 0, 20, TimeSpan.FromSeconds(90));

        Assert.Equal("Timed out before the first result — 0 of 20 checked (no result for 90s)", msg);
    }

    // ── ComposeStoppedMessage: the operator-stopped / superseded activity line ───────────────────────

    [Fact]
    public void ComposeStoppedMessage_names_seen_of_total()
    {
        Assert.Equal("Stopped — 47 of 324 checked", WugMaintenance.ComposeStoppedMessage(47, 324));
    }

    [Fact]
    public void ComposeStoppedMessage_zero_seen()
    {
        // A stop before any row resolved still reads as an aborted run, never a completed one.
        Assert.Equal("Stopped — 0 of 5 checked", WugMaintenance.ComposeStoppedMessage(0, 5));
    }
}
