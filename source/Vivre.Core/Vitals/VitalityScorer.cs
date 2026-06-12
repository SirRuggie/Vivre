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
/// <param name="NeedsAttention">
/// True when the box has an admin-attention finding that is NOT a runtime-health metric — currently a
/// WinRM/Kerberos degradation (the host was reached over SMB/DCOM because it rejected Kerberos). The box
/// may still sit in the green band, so the UI surfaces this as a separate flag/badge; the matching
/// fix-bearing reason is always present in <paramref name="Reasons"/> (never dropped by the worst-few cap).
/// </param>
public sealed record VitalityResult(int? Score, VitalityBand Band, IReadOnlyList<string> Reasons, bool NeedsAttention = false);

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

    // NOTE: stopped auto-start services are deliberately NOT scored. In the field they're dominated by
    // benign noise — services that are idle-by-design (trigger/delayed start, updaters) — so they made
    // healthy boxes read "Warning" for no real reason. They're still gathered and shown in the triage
    // breakdown (with a Start button) WHEN any are stopped; they just don't move the number. The old
    // recent-error/critical-event digest was dropped entirely (pure noise, no triage action, and the
    // Get-WinEvent gather slowed every check).

    // Reboot pending / long uptime.
    private const int RebootPenalty = 10;
    private const int UptimeDaysThreshold = 60, UptimePenalty = 5;

    // WinRM transport degradation: WinRM couldn't read the host and Vivre reached it over SMB/DCOM
    // instead — either a Kerberos failure (KerberosRejected) or a non-Kerberos WinRM failure
    // (WinRmUnavailable: service stopped/misconfigured, or the session dropped). This is an
    // admin-attention finding (the box may be runtime-healthy), so it docks the score modestly, its
    // fix-bearing reason is ALWAYS surfaced (never truncated), and it sets NeedsAttention so the UI can
    // flag the box regardless of the green band. The cause + fix wording is single-sourced in
    // WinRmHealthGuidance (shared with the Machine Details "Connection" callout).
    private const int WinRmDegradedPenalty = 12;

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
            // Even with no readings, if the WinRM transport was degraded (we only reached the host — or
            // tried to — over SMB/DCOM), still flag it for attention and name the fix.
            string? degradedReason = WinRmHealthGuidance.Reason(vitals?.WinRmHealth);
            return new VitalityResult(
                null,
                VitalityBand.Unknown,
                degradedReason is not null ? [degradedReason, "Vitals unavailable"] : ["Vitals unavailable"],
                degradedReason is not null);
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

        // Stopped auto-start services are intentionally not scored (too noisy — see the note by the
        // consts above). They remain in the triage breakdown when present.

        if (vitals.RebootPending == true)
        {
            penalties.Add((RebootPenalty, "Reboot pending"));
        }

        if (vitals.Uptime is { } up && up.TotalDays > UptimeDaysThreshold)
        {
            penalties.Add((UptimePenalty, $"Up {(int)up.TotalDays} days — consider patching/reboot"));
        }

        // The WinRM transport degradation is an admin-attention finding, not a runtime-health metric:
        // dock the score modestly, but surface its fix-bearing reason ALWAYS (never truncated by the
        // worst-few cap) and flag NeedsAttention so the UI can mark the box even in the green band.
        string? attentionReason = WinRmHealthGuidance.Reason(vitals.WinRmHealth);
        bool needsAttention = attentionReason is not null;
        int degradedPenalty = needsAttention ? WinRmDegradedPenalty : 0;

        int score = Math.Clamp(100 - penalties.Sum(p => p.Points) - degradedPenalty, 0, 100);
        VitalityBand band = score >= HealthyMin ? VitalityBand.Healthy
            : score >= WarningMin ? VitalityBand.Warning
            : VitalityBand.Critical;

        // A Kerberos-fallback box must never look identical to a healthy box on the grid: if the box
        // needs attention AND the band computed to Healthy, floor it to Warning so the chip shows amber
        // rather than green. The Score number is left unchanged (already docked by 12). Only Healthy
        // is floored — Unknown/Offline are unaffected (they're handled above and never reach here).
        if (needsAttention && band == VitalityBand.Healthy)
        {
            band = VitalityBand.Warning;
        }

        IEnumerable<string> worstFirst = penalties
            .OrderByDescending(p => p.Points)
            .Select(p => p.Reason);

        IReadOnlyList<string> reasons = needsAttention
            ? new[] { attentionReason! }.Concat(worstFirst.Take(MaxReasons - 1)).ToList()
            : worstFirst.Take(MaxReasons).ToList();

        return new VitalityResult(score, band, reasons, needsAttention);
    }
}
