using System.Globalization;
using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

public class ScheduleTimeFormatterTests
{
    // A fixed, DST-free offset zone so the expected UTC digits below are hand-computed CONSTANTS — they
    // are NOT derived from any GetUtcOffset/ToUniversalTime call, so the conversion can't be circular,
    // and every assertion's outcome is identical on any machine regardless of the test box's own zone.
    private static TimeZoneInfo FixedOffset(double hours)
    {
        string id = "Test UTC" + hours.ToString("+0.##;-0.##", CultureInfo.InvariantCulture);
        return TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(hours), id, id);
    }

    [Theory]
    // A known host-local pick + a fixed host offset → the literal absolute-UTC digits, computed by hand.
    // The negative-offset rows (host behind UTC) must ADD the offset; the positive row must SUBTRACT it,
    // so a backwards/symmetric conversion fails at least one row on every machine.
    [InlineData(2026, 1, 15, 14, 0, -5.0, "2026-01-15T19:00:00Z")] // UTC-5 (US Eastern, winter): 14:00 + 5h
    [InlineData(2026, 7, 1, 14, 0, -4.0, "2026-07-01T18:00:00Z")]  // UTC-4 (US Eastern, summer): 14:00 + 4h
    [InlineData(2026, 7, 1, 14, 0, 0.0, "2026-07-01T14:00:00Z")]   // UTC host:                   unchanged
    [InlineData(2026, 7, 1, 14, 0, 9.5, "2026-07-01T04:30:00Z")]   // UTC+9:30 (ahead of UTC):    14:00 - 9:30
    public void FormatStartBoundaryUtc_converts_fixed_local_pick_to_fixed_utc_digits(
        int year, int month, int day, int hour, int minute, double hostOffsetHours, string expectedUtc)
    {
        // An explicit wall-clock the operator picked, interpreted in a FIXED host zone.
        var picked = new DateTime(year, month, day, hour, minute, 0);

        string z = ScheduleTimeFormatter.FormatStartBoundaryUtc(picked, FixedOffset(hostOffsetHours));

        Assert.Equal(expectedUtc, z);
    }

    [Fact]
    public void FormatStartBoundaryUtc_interprets_digits_in_host_zone_ignoring_incoming_kind()
    {
        // The picker yields Unspecified; a Local- or Utc-labelled value with the SAME digits must
        // produce the SAME boundary — the helper anchors the digits to the host zone and never trusts
        // the incoming Kind. UTC-5 host: 09:00 + 5h = 14:00Z (fixed, hand-computed).
        TimeZoneInfo zone = FixedOffset(-5.0);
        var wall = new DateTime(2026, 1, 15, 9, 0, 0);

        Assert.Equal("2026-01-15T14:00:00Z", ScheduleTimeFormatter.FormatStartBoundaryUtc(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), zone));
        Assert.Equal("2026-01-15T14:00:00Z", ScheduleTimeFormatter.FormatStartBoundaryUtc(DateTime.SpecifyKind(wall, DateTimeKind.Local), zone));
        Assert.Equal("2026-01-15T14:00:00Z", ScheduleTimeFormatter.FormatStartBoundaryUtc(DateTime.SpecifyKind(wall, DateTimeKind.Utc), zone));
    }

    [Fact]
    public void FormatStartBoundaryUtc_public_overload_anchors_to_host_local_zone()
    {
        // Lock the public (production) overload to the tested core fed the machine's own zone, so the
        // two can't drift. This is intentionally zone-relative (not fixed digits): it proves the public
        // entry point uses TimeZoneInfo.Local, while the fixed-digit theory above proves the math.
        var picked = new DateTime(2026, 7, 1, 14, 30, 0);

        Assert.Equal(
            ScheduleTimeFormatter.FormatStartBoundaryUtc(picked, TimeZoneInfo.Local),
            ScheduleTimeFormatter.FormatStartBoundaryUtc(picked));
    }
}
