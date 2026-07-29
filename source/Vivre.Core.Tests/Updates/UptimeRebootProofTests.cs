using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Locks the CLOCK-IMMUNE reboot proof used by the fast watch, where a boot-time advance is the ONLY
/// evidence — nobody saw the box go offline and it was reachable throughout.
/// <para>New behaviour, not a regression guard: this rule had no shared home before, and the fast watch's
/// first cut compared raw boot times, which a target clock step can fake.</para>
/// </summary>
public class UptimeRebootProofTests
{
    private static BootTimeReading At(DateTime localNow, TimeSpan uptime) =>
        new(localNow, localNow - uptime);

    private static readonly DateTime T0 = new(2026, 7, 28, 20, 31, 0, DateTimeKind.Local);

    [Fact]
    public void A_reboot_resets_the_uptime_and_is_proven_even_though_nobody_saw_it_drop()
    {
        // The field case: a VM up for 5 days, force-rebooted, back in ~20s. Never observed offline.
        BootTimeReading baseline = At(T0, TimeSpan.FromDays(5));
        BootTimeReading current = At(T0 + TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(20));

        Assert.True(UptimeRebootProof.IsProven(baseline, current, TimeSpan.FromSeconds(25)));
    }

    [Fact]
    public void A_box_that_never_rebooted_is_NOT_proven_however_long_we_watch()
    {
        // Uptime keeps climbing in lockstep with elapsed time — the drop is ~zero, so no proof.
        BootTimeReading baseline = At(T0, TimeSpan.FromDays(5));
        TimeSpan elapsed = TimeSpan.FromMinutes(16);
        BootTimeReading current = At(T0 + elapsed, TimeSpan.FromDays(5) + elapsed);

        Assert.False(UptimeRebootProof.IsProven(baseline, current, elapsed));
    }

    [Fact]
    public void A_TARGET_CLOCK_STEP_is_not_mistaken_for_a_reboot()
    {
        // THE reason this rule exists rather than a raw boot-time comparison. An NTP correction shoves the
        // target's wall clock forward an hour: LastBootUpTime moves with it, so a boot-time diff would read
        // as a reboot. Uptime is taken from the same single query, so it is untouched — no proof.
        BootTimeReading baseline = At(T0, TimeSpan.FromDays(5));
        TimeSpan elapsed = TimeSpan.FromSeconds(30);
        BootTimeReading afterClockStep = At(T0 + elapsed + TimeSpan.FromHours(1), TimeSpan.FromDays(5) + elapsed);

        Assert.False(UptimeRebootProof.IsProven(baseline, afterClockStep, elapsed));
        // ...and the raw boot times DID move by an hour, which is exactly what would have fooled the old form.
        Assert.True(afterClockStep.LastBootUpTime - baseline.LastBootUpTime > RebootWave.UptimeProofMargin);
    }

    [Fact]
    public void Read_jitter_inside_the_margin_is_not_a_reboot()
    {
        BootTimeReading baseline = At(T0, TimeSpan.FromDays(5));
        TimeSpan elapsed = TimeSpan.FromSeconds(30);
        // Uptime came back ~1 min short of expected — jitter between two independent reads, not a restart.
        BootTimeReading current = At(T0 + elapsed, TimeSpan.FromDays(5) + elapsed - TimeSpan.FromMinutes(1));

        Assert.False(UptimeRebootProof.IsProven(baseline, current, elapsed));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void An_unreadable_reading_is_never_proof(bool baselineNull, bool currentNull)
    {
        BootTimeReading? baseline = baselineNull ? null : At(T0, TimeSpan.FromDays(5));
        BootTimeReading? current = currentNull ? null : At(T0, TimeSpan.FromSeconds(10));

        Assert.False(UptimeRebootProof.IsProven(baseline, current, TimeSpan.FromSeconds(30)));
    }
}
