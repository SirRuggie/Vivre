using Vivre.Core.Wug;
using Xunit;

namespace Vivre.Core.Tests.Wug;

/// <summary>
/// Locks the exact per-row Command-result strings of the WUG state check. The three "no result"
/// flavours must stay distinct: "state unknown" (WUG answered, no definite state), "no matching device"
/// (name mapped to nothing), and "not checked (read stopped)" (Vivre never got an answer). A regression
/// that blurred NotChecked into either of the other two would tell the operator a stopped/aborted read
/// was a definite finding.
/// </summary>
public class WugRowTextTests
{
    [Fact]
    public void Checking_exact()
        => Assert.Equal("WhatsUp Gold: checking state…", WugRowText.Checking);

    [Fact]
    public void InMaintenance_exact()
        => Assert.Equal("WhatsUp Gold: in maintenance", WugRowText.InMaintenance);

    [Fact]
    public void NotInMaintenance_exact()
        => Assert.Equal("WhatsUp Gold: not in maintenance", WugRowText.NotInMaintenance);

    [Fact]
    public void NoMatchingDevice_exact()
        => Assert.Equal("WhatsUp Gold: no matching device (by IP)", WugRowText.NoMatchingDevice);

    [Fact]
    public void StateUnknown_exact()
        => Assert.Equal("WhatsUp Gold: state unknown", WugRowText.StateUnknown);

    [Fact]
    public void NotChecked_exact()
        => Assert.Equal("WhatsUp Gold: not checked (read stopped)", WugRowText.NotChecked);

    [Fact]
    public void NotChecked_never_reads_as_a_data_gap_or_a_name_miss()
    {
        // Load-bearing: "not checked" means Vivre never got an answer for this row (abort/stop). It must
        // NEVER contain "unknown" (which means WUG answered without a definite state) or "no matching
        // device" (which means the name mapped to nothing) — conflating them would misreport a stopped
        // read as a definite WUG finding.
        Assert.DoesNotContain("unknown", WugRowText.NotChecked, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no matching device", WugRowText.NotChecked, System.StringComparison.OrdinalIgnoreCase);
    }
}
