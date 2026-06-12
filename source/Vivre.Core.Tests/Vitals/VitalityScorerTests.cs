using Vivre.Core.Vitals;
using Xunit;

namespace Vivre.Core.Tests.Vitals;

public class VitalityScorerTests
{
    [Fact]
    public void Healthy_box_scores_high_with_no_reasons()
    {
        var vitals = new MachineVitals(
            SystemDriveFreePercent: 60,
            MemoryUsedPercent: 40,
            CpuLoadPercent: 12,
            LastBootTime: DateTime.Now.AddDays(-2),
            StoppedAutoServiceCount: 0,
            RebootPending: false);

        VitalityResult result = VitalityScorer.Score(vitals, isOnline: true);

        Assert.Equal(VitalityBand.Healthy, result.Band);
        Assert.Equal(100, result.Score);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void Offline_short_circuits_regardless_of_vitals()
    {
        // Even a perfect-looking snapshot reads Offline when the box isn't reachable.
        var vitals = new MachineVitals(SystemDriveFreePercent: 90, MemoryUsedPercent: 10);

        VitalityResult result = VitalityScorer.Score(vitals, isOnline: false);

        Assert.Equal(VitalityBand.Offline, result.Band);
        Assert.Null(result.Score);
        Assert.Contains("Offline", result.Reasons);
    }

    [Fact]
    public void Null_vitals_read_as_unknown()
    {
        VitalityResult result = VitalityScorer.Score(null, isOnline: true);

        Assert.Equal(VitalityBand.Unknown, result.Band);
        Assert.Null(result.Score);
    }

    [Fact]
    public void Empty_snapshot_reads_as_unknown()
    {
        // A probe that round-tripped but couldn't read anything is "unknown", not "healthy".
        VitalityResult result = VitalityScorer.Score(new MachineVitals(), isOnline: true);

        Assert.Equal(VitalityBand.Unknown, result.Band);
    }

    [Fact]
    public void Critically_low_disk_drives_critical_band_and_leads_reasons()
    {
        var vitals = new MachineVitals(SystemDriveFreePercent: 3, MemoryUsedPercent: 50, CpuLoadPercent: 10);

        VitalityResult result = VitalityScorer.Score(vitals, isOnline: true);

        // 100 - 40 = 60 → Warning by itself, but disk should top the reasons.
        Assert.StartsWith("Disk critically low", result.Reasons[0]);
        Assert.True(result.Score < VitalityScorer.HealthyMin);
    }

    [Fact]
    public void Multiple_problems_compound_into_critical()
    {
        var vitals = new MachineVitals(
            SystemDriveFreePercent: 3,   // -40
            MemoryUsedPercent: 96,       // -20
            CpuLoadPercent: 97,          // -15
            RebootPending: true);        // -10

        VitalityResult result = VitalityScorer.Score(vitals, isOnline: true);

        Assert.Equal(VitalityBand.Critical, result.Band);
        Assert.Equal(15, result.Score); // 100 - 85
    }

    [Fact]
    public void Noisy_signals_do_not_lower_the_score()
    {
        // Lots of "stopped" auto-start services, but healthy resources. Stopped services are info-only
        // (idle-by-design: trigger/delayed-start, updaters), so the box still reads Healthy and they
        // never appear as score reasons.
        var vitals = new MachineVitals(
            SystemDriveFreePercent: 60, MemoryUsedPercent: 20, CpuLoadPercent: 5,
            StoppedAutoServiceCount: 12, RebootPending: false)
        {
            StoppedAutoServices = ["Edge Update", "Remote Registry"],
        };

        VitalityResult result = VitalityScorer.Score(vitals, isOnline: true);

        Assert.Equal(100, result.Score);
        Assert.Equal(VitalityBand.Healthy, result.Band);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void Score_of_exactly_80_is_healthy()
    {
        // disk 8% → -20 → 80, the Healthy floor (inclusive).
        VitalityResult result = VitalityScorer.Score(new MachineVitals(SystemDriveFreePercent: 8), isOnline: true);

        Assert.Equal(80, result.Score);
        Assert.Equal(VitalityBand.Healthy, result.Band);
    }

    [Fact]
    public void Just_below_80_is_warning()
    {
        // disk 8% (-20) + memory 91% (-10) → 70.
        var vitals = new MachineVitals(SystemDriveFreePercent: 8, MemoryUsedPercent: 91);

        VitalityResult result = VitalityScorer.Score(vitals, isOnline: true);

        Assert.Equal(70, result.Score);
        Assert.Equal(VitalityBand.Warning, result.Band);
    }

    [Fact]
    public void Score_of_exactly_50_is_warning()
    {
        // disk 3% (-40) + reboot (-10) → 50, the Warning floor (inclusive).
        var vitals = new MachineVitals(SystemDriveFreePercent: 3, RebootPending: true);

        VitalityResult result = VitalityScorer.Score(vitals, isOnline: true);

        Assert.Equal(50, result.Score);
        Assert.Equal(VitalityBand.Warning, result.Band);
    }

    [Fact]
    public void Just_below_50_is_critical()
    {
        // disk 3% (-40) + cpu 97% (-15) → 45.
        var vitals = new MachineVitals(SystemDriveFreePercent: 3, CpuLoadPercent: 97);

        VitalityResult result = VitalityScorer.Score(vitals, isOnline: true);

        Assert.Equal(45, result.Score);
        Assert.Equal(VitalityBand.Critical, result.Band);
    }

    [Fact]
    public void Reasons_are_ordered_worst_first_and_capped()
    {
        var vitals = new MachineVitals(
            SystemDriveFreePercent: 3,   // -40 (worst)
            MemoryUsedPercent: 96,       // -20
            CpuLoadPercent: 97,          // -15
            RebootPending: true);        // -10 (4th — dropped, only the top 3 are shown)

        VitalityResult result = VitalityScorer.Score(vitals, isOnline: true);

        Assert.Equal(3, result.Reasons.Count);
        Assert.StartsWith("Disk critically low", result.Reasons[0]);
        Assert.DoesNotContain(result.Reasons, r => r.Contains("Reboot"));
    }

    [Fact]
    public void Long_uptime_is_penalised()
    {
        var vitals = new MachineVitals(SystemDriveFreePercent: 80, LastBootTime: DateTime.Now.AddDays(-90));

        VitalityResult result = VitalityScorer.Score(vitals, isOnline: true);

        Assert.Equal(95, result.Score);
        Assert.Contains(result.Reasons, r => r.Contains("days"));
    }

    [Fact]
    public void Unread_signals_are_not_penalised()
    {
        // Only disk was read (all else null) — score reflects disk alone, nulls don't drag it down.
        var vitals = new MachineVitals(SystemDriveFreePercent: 60);

        VitalityResult result = VitalityScorer.Score(vitals, isOnline: true);

        Assert.Equal(100, result.Score);
        Assert.Equal(VitalityBand.Healthy, result.Band);
    }

    // --- WinRM/Kerberos degradation: an admin-attention finding (the box may be runtime-healthy).
    //     Docks the score modestly, ALWAYS surfaces the fix-bearing reason, sets NeedsAttention. ---

    [Fact]
    public void Kerberos_rejected_flags_attention_and_names_the_fix()
    {
        var vitals = new MachineVitals(
            SystemDriveFreePercent: 60, MemoryUsedPercent: 40, CpuLoadPercent: 10,
            LastBootTime: DateTime.Now.AddDays(-2), RebootPending: false)
        {
            WinRmHealth = WinRmHealth.KerberosRejected,
        };

        VitalityResult result = VitalityScorer.Score(vitals, isOnline: true);

        Assert.True(result.NeedsAttention);
        Assert.Equal(88, result.Score);            // pristine 100 - 12
        Assert.Contains(result.Reasons, r => r.Contains("0x80090322"));
    }

    [Fact]
    public void Kerberos_reason_is_always_surfaced_even_amid_worse_problems()
    {
        // Three real problems would normally fill the worst-3 reasons and truncate everything else,
        // but the Kerberos finding must still appear (it carries the fix).
        var vitals = new MachineVitals(
            SystemDriveFreePercent: 3,   // -40
            MemoryUsedPercent: 96,       // -20
            CpuLoadPercent: 97,          // -15
            RebootPending: true)         // -10
        {
            WinRmHealth = WinRmHealth.KerberosRejected,
        };

        VitalityResult result = VitalityScorer.Score(vitals, isOnline: true);

        Assert.True(result.NeedsAttention);
        Assert.True(result.Reasons.Count <= 3);                                  // still capped at the worst-few
        Assert.Contains("0x80090322", result.Reasons[0]);                        // Kerberos finding is always first
        Assert.Contains(result.Reasons, r => r.Contains("Disk critically low")); // the worst real penalty survives the cap
    }

    [Fact]
    public void Healthy_winrm_does_not_flag_attention_or_dock_score()
    {
        var vitals = new MachineVitals(SystemDriveFreePercent: 60, MemoryUsedPercent: 40, CpuLoadPercent: 10)
        {
            WinRmHealth = WinRmHealth.Healthy,
        };

        VitalityResult result = VitalityScorer.Score(vitals, isOnline: true);

        Assert.False(result.NeedsAttention);
        Assert.Equal(100, result.Score);
        Assert.DoesNotContain(result.Reasons, r => r.Contains("0x80090322"));
    }

    [Fact]
    public void Kerberos_rejected_on_empty_snapshot_still_flags_attention()
    {
        // Reached over SMB/DCOM but vitals came back blank: still Unknown, but flag the Kerberos fix.
        var vitals = new MachineVitals { WinRmHealth = WinRmHealth.KerberosRejected };

        VitalityResult result = VitalityScorer.Score(vitals, isOnline: true);

        Assert.Equal(VitalityBand.Unknown, result.Band);
        Assert.Null(result.Score);
        Assert.True(result.NeedsAttention);
        Assert.Contains(result.Reasons, r => r.Contains("0x80090322"));
        Assert.Contains("Vitals unavailable", result.Reasons); // the blank-read reason is retained alongside the fix
    }

    [Fact]
    public void Kerberos_fallback_with_perfect_vitals_floors_band_to_warning_not_healthy()
    {
        // A Kerberos-fallback box with pristine vitals must show amber, not green, so it can never
        // look identical to a genuinely healthy box on the grid. Score stays at 88 (docked by 12),
        // only the band is floored from Healthy → Warning.
        var vitals = new MachineVitals(
            SystemDriveFreePercent: 60,
            MemoryUsedPercent: 40,
            CpuLoadPercent: 10,
            LastBootTime: DateTime.Now.AddDays(-2),
            RebootPending: false)
        {
            WinRmHealth = WinRmHealth.KerberosRejected,
        };

        VitalityResult result = VitalityScorer.Score(vitals, isOnline: true);

        // Score is unchanged from the 12-point dock — band is floored, NOT the number.
        Assert.Equal(88, result.Score);
        Assert.Equal(VitalityBand.Warning, result.Band); // NOT Healthy
        Assert.True(result.NeedsAttention);
        Assert.Contains(result.Reasons, r => r.Contains("0x80090322"));
    }

    [Fact]
    public void Kerberos_penalty_can_tip_a_borderline_box_from_healthy_to_warning()
    {
        // disk 8% → -20 → 80 (the Healthy floor); + Kerberos -12 → 68 → Warning. Pins both the penalty
        // magnitude and the band edge.
        var vitals = new MachineVitals(SystemDriveFreePercent: 8) { WinRmHealth = WinRmHealth.KerberosRejected };

        VitalityResult result = VitalityScorer.Score(vitals, isOnline: true);

        Assert.Equal(68, result.Score);
        Assert.Equal(VitalityBand.Warning, result.Band);
        Assert.True(result.NeedsAttention);
        Assert.Contains(result.Reasons, r => r.Contains("0x80090322"));
    }

    [Fact]
    public void Winrm_unavailable_fallback_with_perfect_vitals_floors_band_and_flags_without_kerberos_text()
    {
        // A non-Kerberos DCOM-rescued box (WinRM service down / session dropped) with pristine vitals must
        // also show amber + flagged — never green — but its reason names the WinRM-service fix, not Kerberos.
        var vitals = new MachineVitals(
            SystemDriveFreePercent: 60, MemoryUsedPercent: 40, CpuLoadPercent: 10, RebootPending: false)
        {
            WinRmHealth = WinRmHealth.WinRmUnavailable,
        };

        VitalityResult result = VitalityScorer.Score(vitals, isOnline: true);

        Assert.Equal(88, result.Score);                  // same 12-point dock as the Kerberos case
        Assert.Equal(VitalityBand.Warning, result.Band); // floored from Healthy
        Assert.True(result.NeedsAttention);
        Assert.Contains(result.Reasons, r => r.Contains("WinRM", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Reasons, r => r.Contains("0x80090322")); // not the Kerberos finding
    }
}
