namespace Vivre.Core.Vitals;

/// <summary>The glanceable health band a machine's vitality score falls into.</summary>
public enum VitalityBand
{
    /// <summary>Vitals couldn't be read (probe failed / never run) — grey.</summary>
    Unknown,

    /// <summary>The machine is offline — dark grey.</summary>
    Offline,

    /// <summary>Score below 50 — something needs attention now (red).</summary>
    Critical,

    /// <summary>Score 50-79 — degraded but serviceable (amber).</summary>
    Warning,

    /// <summary>Score 80-100 — nothing notable wrong (green).</summary>
    Healthy,
}

/// <summary>
/// The outcome of scoring one machine: the 0-100 number, the band it maps to, and the
/// worst contributing factors as human-readable reasons (worst first). One immutable result.
/// </summary>
/// <param name="Score">0-100 vitality, or null when the band is <see cref="VitalityBand.Unknown"/>/<see cref="VitalityBand.Offline"/>.</param>
/// <param name="Band">The display band.</param>
/// <param name="Reasons">The penalties that fired, worst first, capped at a few — the "why".</param>
public sealed record VitalityResult(int? Score, VitalityBand Band, IReadOnlyList<string> Reasons);

/// <summary>
/// Reduces a <see cref="MachineVitals"/> snapshot to a single 0-100 vitality score + band + reasons.
/// A pure function (no WPF / PowerShell types) so it's the single source of truth for the grid chip,
/// the fleet tally, and the triage panel — the diagnostic analogue of <c>Computer.DerivePatchState</c>,
/// kept in Core so it's trivially unit-testable.
///
/// <para>Each machine starts at 100 and loses weighted points per problem found; the total clamps to
/// [0,100] and maps to a band. A <c>null</c> signal is "unknown" and never penalised, so a box that
/// only answers some probes still scores usefully. Thresholds/weights are the <c>const</c>s below —
/// the one place to tune the rubric.</para>
/// </summary>
public static class VitalityScorer
{
    // Band cut-offs.
    public const int HealthyMin = 80;
    public const int WarningMin = 50;

    // Disk (system drive) free-percent thresholds and penalties.
    private const double DiskCriticalPct = 5, DiskLowPct = 10, DiskTightPct = 15;
    private const int DiskCriticalPenalty = 40, DiskLowPenalty = 20, DiskTightPenalty = 8;

    // Memory used-percent thresholds and penalties.
    private const double MemCriticalPct = 95, MemHighPct = 90;
    private const int MemCriticalPenalty = 20, MemHighPenalty = 10;

    // CPU load-percent thresholds and penalties.
    private const double CpuCriticalPct = 95, CpuHighPct = 85;
    private const int CpuCriticalPenalty = 15, CpuHighPenalty = 6;

    // Stopped auto-start services: per-service penalty, capped.
    private const int PerStoppedServicePenalty = 5, StoppedServiceCap = 20;

    // Recent error/critical events (last 24h) thresholds and penalties.
    private const int EventsManyCount = 20, EventsSomeCount = 5;
    private const int EventsManyPenalty = 15, EventsSomePenalty = 8, EventsFewPenalty = 3;

    // Reboot pending / long uptime.
    private const int RebootPenalty = 10;
    private const int UptimeDaysThreshold = 60, UptimePenalty = 5;

    // Cap on the number of reasons surfaced (the worst few).
    private const int MaxReasons = 3;

    /// <summary>Scores <paramref name="vitals"/> for a machine whose reachability is <paramref name="isOnline"/>.</summary>
    public static VitalityResult Score(MachineVitals? vitals, bool? isOnline)
    {
        // Short-circuits: a known-offline box, or a probe that came back with nothing, can't be scored.
        if (isOnline == false)
        {
            return new VitalityResult(null, VitalityBand.Offline, ["Offline"]);
        }

        if (vitals is null || vitals.IsEmpty)
        {
            return new VitalityResult(null, VitalityBand.Unknown, ["Vitals unavailable"]);
        }

        var penalties = new List<(int Points, string Reason)>();

        if (vitals.SystemDriveFreePercent is { } disk)
        {
            if (disk < DiskCriticalPct)
            {
                penalties.Add((DiskCriticalPenalty, $"Disk critically low: {disk:0.#}% free"));
            }
            else if (disk < DiskLowPct)
            {
                penalties.Add((DiskLowPenalty, $"Low disk: {disk:0.#}% free"));
            }
            else if (disk < DiskTightPct)
            {
                penalties.Add((DiskTightPenalty, $"Disk getting tight: {disk:0.#}% free"));
            }
        }

        if (vitals.MemoryUsedPercent is { } mem)
        {
            if (mem > MemCriticalPct)
            {
                penalties.Add((MemCriticalPenalty, $"Memory nearly exhausted: {mem:0}% used"));
            }
            else if (mem > MemHighPct)
            {
                penalties.Add((MemHighPenalty, $"High memory use: {mem:0}% used"));
            }
        }

        if (vitals.CpuLoadPercent is { } cpu)
        {
            if (cpu > CpuCriticalPct)
            {
                penalties.Add((CpuCriticalPenalty, $"CPU pinned: {cpu:0}%"));
            }
            else if (cpu > CpuHighPct)
            {
                penalties.Add((CpuHighPenalty, $"High CPU: {cpu:0}%"));
            }
        }

        if (vitals.StoppedAutoServiceCount is { } stopped && stopped > 0)
        {
            int points = Math.Min(stopped * PerStoppedServicePenalty, StoppedServiceCap);
            string names = vitals.StoppedAutoServices.Count > 0
                ? ": " + string.Join(", ", vitals.StoppedAutoServices.Take(3))
                : string.Empty;
            string plural = stopped == 1 ? "service" : "services";
            penalties.Add((points, $"{stopped} auto-start {plural} stopped{names}"));
        }

        if (vitals.RecentErrorEventCount is { } events && events > 0)
        {
            int points = events >= EventsManyCount ? EventsManyPenalty
                : events >= EventsSomeCount ? EventsSomePenalty
                : EventsFewPenalty;
            penalties.Add((points, $"{events} error/critical event(s) in 24h"));
        }

        if (vitals.RebootPending == true)
        {
            penalties.Add((RebootPenalty, "Reboot pending"));
        }

        if (vitals.Uptime is { } up && up.TotalDays > UptimeDaysThreshold)
        {
            penalties.Add((UptimePenalty, $"Up {(int)up.TotalDays} days — consider patching/reboot"));
        }

        int score = Math.Clamp(100 - penalties.Sum(p => p.Points), 0, 100);
        VitalityBand band = score >= HealthyMin ? VitalityBand.Healthy
            : score >= WarningMin ? VitalityBand.Warning
            : VitalityBand.Critical;

        IReadOnlyList<string> reasons = penalties
            .OrderByDescending(p => p.Points)
            .Take(MaxReasons)
            .Select(p => p.Reason)
            .ToList();

        return new VitalityResult(score, band, reasons);
    }
}
