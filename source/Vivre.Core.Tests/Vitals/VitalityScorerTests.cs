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
            RecentErrorEventCount: 0,
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
    public void Stopped_service_penalty_is_capped_and_names_are_listed()
    {
        var names = Enumerable.Range(1, 10).Select(i => $"Svc{i}").ToList();
        var vitals = new MachineVitals(SystemDriveFreePercent: 80, StoppedAutoServiceCount: 10)
        {
            StoppedAutoServices = names,
        };

        VitalityResult result = VitalityScorer.Score(vitals, isOnline: true);

        // 10 * 5 = 50, capped at 20 → score 80 (still Healthy boundary).
        Assert.Equal(80, result.Score);
        Assert.Contains(result.Reasons, r => r.Contains("Svc1"));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(3, 97)]   // few → -3
    [InlineData(8, 92)]   // some (>=5) → -8
    [InlineData(25, 85)]  // many (>=20) → -15
    public void Event_count_tiers_map_to_expected_score(int events, int expected)
    {
        var vitals = new MachineVitals(SystemDriveFreePercent: 80, RecentErrorEventCount: events);

        VitalityResult result = VitalityScorer.Score(vitals, isOnline: true);

        Assert.Equal(expected, result.Score);
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
        // disk 8% (-20) + 2 events (-3) → 77.
        var vitals = new MachineVitals(SystemDriveFreePercent: 8, RecentErrorEventCount: 2);

        VitalityResult result = VitalityScorer.Score(vitals, isOnline: true);

        Assert.Equal(77, result.Score);
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
            RebootPending: true,         // -10
            RecentErrorEventCount: 2);   // -3 (should be dropped — only top 3)

        VitalityResult result = VitalityScorer.Score(vitals, isOnline: true);

        Assert.Equal(3, result.Reasons.Count);
        Assert.StartsWith("Disk critically low", result.Reasons[0]);
        Assert.DoesNotContain(result.Reasons, r => r.Contains("event"));
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
}
