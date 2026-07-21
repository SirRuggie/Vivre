using System.Globalization;

namespace Vivre.Core.Wug;

/// <summary>
/// The exact per-row Command-result strings of the WUG state check, centralized so tests can lock
/// them. The distinctions are load-bearing for the operator: "state unknown" means WUG ANSWERED and
/// didn't know; "no matching device" means the name mapped to nothing; "not checked (read stopped)"
/// means Vivre never got an answer for this row (abort/stop) — it must never read as either of the
/// other two. In-maintenance rows may carry the optional manual-maintenance detail (reason / who /
/// since) via <see cref="ComposeInMaintenance"/> — which ALWAYS starts with the plain
/// <see cref="InMaintenance"/> text, so the state reads identically with or without detail.
/// </summary>
public static class WugRowText
{
    public const string Checking         = "WhatsUp Gold: checking state…";
    public const string InMaintenance    = "WhatsUp Gold: in maintenance";
    public const string NotInMaintenance = "WhatsUp Gold: not in maintenance";
    public const string NoMatchingDevice = "WhatsUp Gold: no matching device (by IP)";
    public const string StateUnknown     = "WhatsUp Gold: state unknown";
    public const string NotChecked       = "WhatsUp Gold: not checked (read stopped)";

    /// <summary>
    /// The in-maintenance row text with the optional manual-maintenance detail appended:
    /// <c>WhatsUp Gold: in maintenance — "reason" (user, since 2026-05-21, 60d)</c>. Every part is
    /// independently optional; all absent returns exactly <see cref="InMaintenance"/>. An unparseable
    /// <paramref name="sinceUtc"/> is OMITTED, never rendered as garbage. Dates render as the UTC
    /// calendar date (deterministic on any machine); the day count is floored and dropped below one
    /// day or on clock skew. Pure — pass <paramref name="nowUtc"/> in tests.
    /// </summary>
    public static string ComposeInMaintenance(string? reason, string? user, string? sinceUtc, DateTimeOffset? nowUtc = null)
        => InMaintenance + ComposeDetailSuffix(reason, user, sinceUtc, nowUtc ?? DateTimeOffset.UtcNow);

    /// <summary>
    /// One activity-log line naming every in-maintenance machine with its detail —
    /// <c>WhatsUp Gold in maintenance: A — "reason" (user, since 2026-05-21, 60d); B; … and 3 more</c> —
    /// or null when nothing is in maintenance. Machines are listed name-sorted (deterministic), capped
    /// at <paramref name="maxEntries"/> with an honest "and N more". Pure — pass
    /// <paramref name="nowUtc"/> in tests.
    /// </summary>
    public static string? ComposeMaintenanceDigest(
        IReadOnlyDictionary<string, bool?> byName,
        IReadOnlyDictionary<string, WugMaintenanceDetail>? details,
        DateTimeOffset? nowUtc = null,
        int maxEntries = 6)
    {
        DateTimeOffset now = nowUtc ?? DateTimeOffset.UtcNow;
        List<string> inMaint = [.. byName.Where(kv => kv.Value == true).Select(kv => kv.Key)
                                         .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
        if (inMaint.Count == 0)
        {
            return null;
        }

        IEnumerable<string> entries = inMaint.Take(maxEntries).Select(name =>
        {
            WugMaintenanceDetail? d = details is not null && details.TryGetValue(name, out WugMaintenanceDetail? found) ? found : null;
            return name + ComposeDetailSuffix(d?.Reason, d?.User, d?.SinceUtc, now);
        });

        string line = $"WhatsUp Gold in maintenance: {string.Join("; ", entries)}";
        return inMaint.Count > maxEntries ? $"{line}; … and {inMaint.Count - maxEntries} more" : line;
    }

    // The shared detail suffix — ` — "reason" (user, since 2026-05-21, 60d)` — used by the row text and
    // the digest so the two can never phrase the same fact differently. Empty string when nothing usable.
    private static string ComposeDetailSuffix(string? reason, string? user, string? sinceUtc, DateTimeOffset nowUtc)
    {
        reason = Normalize(reason);
        user = Normalize(user);

        string text = reason is not null ? $" — \"{reason}\"" : string.Empty;

        var parts = new List<string>(3);
        if (user is not null)
        {
            parts.Add(user);
        }

        if (Normalize(sinceUtc) is { } sinceText
            && DateTimeOffset.TryParse(sinceText, CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset since))
        {
            parts.Add("since " + since.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            int days = (int)Math.Floor((nowUtc - since).TotalDays);
            if (days >= 1)
            {
                parts.Add($"{days}d");
            }
        }

        if (parts.Count > 0)
        {
            text += $" ({string.Join(", ", parts)})";
        }

        return text;
    }

    private static string? Normalize(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
