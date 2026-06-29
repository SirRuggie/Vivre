using System.Globalization;
using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

public class ScheduleTimeFormatterTests
{
    // The helper intentionally converts using the Vivre HOST's live zone, so the test asserts the
    // INVARIANT (the emitted Z instant equals the picked wall-clock interpreted as host-local) and
    // derives the expected digits independently from the host offset. Hard-coding UTC digits would
    // make the test pass only in one zone — exactly the live-zone dependency we must avoid. This
    // assertion's outcome is the same on any machine: green when the conversion is correct.
    [Fact]
    public void FormatStartBoundaryUtc_emits_absolute_utc_for_host_local_pick()
    {
        // Mirrors ScheduleWindow: a Kind=Unspecified wall-clock value the operator picked.
        var picked = new DateTime(2026, 7, 1, 14, 30, 0);

        string z = ScheduleTimeFormatter.FormatStartBoundaryUtc(picked);

        // Trailing-Z absolute form, parseable as UTC.
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", z);

        // Independently compute the expected UTC instant (host-local wall-clock minus the host's
        // offset at that instant) WITHOUT routing through the helper's own conversion, so this
        // asserts the digits rather than restating the implementation.
        TimeSpan hostOffset = TimeZoneInfo.Local.GetUtcOffset(DateTime.SpecifyKind(picked, DateTimeKind.Local));
        string expected = picked.Add(-hostOffset).ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);
        Assert.Equal(expected, z);
    }

    [Fact]
    public void FormatStartBoundaryUtc_pins_kind_to_host_local_regardless_of_incoming_kind()
    {
        // Defensive: the picker yields Unspecified, but if a Utc-kind value ever reached the helper,
        // a missing Kind pin would make ToUniversalTime a no-op and emit the wrong instant. With the
        // SpecifyKind(Local) pin, the same digits convert identically whatever the incoming Kind.
        var wall = new DateTime(2026, 1, 15, 9, 0, 0);
        string fromUnspecified = ScheduleTimeFormatter.FormatStartBoundaryUtc(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified));
        string fromLocal = ScheduleTimeFormatter.FormatStartBoundaryUtc(DateTime.SpecifyKind(wall, DateTimeKind.Local));
        string fromUtcLabelled = ScheduleTimeFormatter.FormatStartBoundaryUtc(DateTime.SpecifyKind(wall, DateTimeKind.Utc));

        Assert.Equal(fromUnspecified, fromLocal);
        Assert.Equal(fromUnspecified, fromUtcLabelled);
    }
}
