namespace Vivre.Core.Wug;

/// <summary>
/// The exact per-row Command-result strings of the WUG state check, centralized so tests can lock
/// them. The distinctions are load-bearing for the operator: "state unknown" means WUG ANSWERED and
/// didn't know; "no matching device" means the name mapped to nothing; "not checked (read stopped)"
/// means Vivre never got an answer for this row (abort/stop) — it must never read as either of the
/// other two.
/// </summary>
public static class WugRowText
{
    public const string Checking         = "WhatsUp Gold: checking state…";
    public const string InMaintenance    = "WhatsUp Gold: in maintenance";
    public const string NotInMaintenance = "WhatsUp Gold: not in maintenance";
    public const string NoMatchingDevice = "WhatsUp Gold: no matching device (by IP)";
    public const string StateUnknown     = "WhatsUp Gold: state unknown";
    public const string NotChecked       = "WhatsUp Gold: not checked (read stopped)";
}
