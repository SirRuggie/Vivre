using Vivre.Core.Models;
using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Locks the monitor self-heal razor (<see cref="MonitorSelfHeal.ShouldSelfHeal"/>) and the boundary
/// facts it depends on: only the probe-only Unverified variant (a definitely-clean reboot probe over a
/// row whose rescan already came back clean) may go green; every other variant, and every unknown
/// (null / timeout / Kerberos) probe, must stay Unverified.
/// </summary>
public class MonitorSelfHealTests
{
    // ── the razor ─────────────────────────────────────────────────────────────

    [Fact]
    public void Probe_only_Unverified_with_confirmed_clean_probe_heals()
    {
        // Variant A: rescan clean, only the reboot-pending probe had been unknown; it now answered false.
        Assert.True(MonitorSelfHeal.ShouldSelfHeal("Unverified", probeOnlyUnverified: true, probeResult: false));
    }

    [Fact]
    public void Unknown_probe_never_heals()
    {
        // null = the probe couldn't answer (timeout, or the Kerberos cohort whose WinRM probe can only ever
        // produce null/absent). This is also that cohort's honesty case: it structurally never reaches green.
        Assert.False(MonitorSelfHeal.ShouldSelfHeal("Unverified", probeOnlyUnverified: true, probeResult: null));
    }

    [Fact]
    public void Confirmed_pending_probe_never_heals_and_derives_amber()
    {
        // true = a reboot is confirmed still pending — the caller's pending guard upgrades to amber, never green.
        Assert.False(MonitorSelfHeal.ShouldSelfHeal("Unverified", probeOnlyUnverified: true, probeResult: true));

        var c = new Computer("HOST") { UpdatePhase = "Unverified", RebootRequired = true };
        Assert.Equal(PatchState.RebootPending, c.PatchState);
    }

    [Fact]
    public void Non_probe_only_Unverified_never_heals()
    {
        // Variants B/C/D (couldn't rescan / scan failed) carry no marker — a clean probe must never green them.
        Assert.False(MonitorSelfHeal.ShouldSelfHeal("Unverified", probeOnlyUnverified: false, probeResult: false));
    }

    [Fact]
    public void A_non_Unverified_phase_never_heals()
    {
        // The live phase check: a concurrent scan that already moved the row off Unverified suppresses the heal,
        // and an absent phase can't heal either.
        Assert.False(MonitorSelfHeal.ShouldSelfHeal("Done", probeOnlyUnverified: true, probeResult: false));
        Assert.False(MonitorSelfHeal.ShouldSelfHeal(null, probeOnlyUnverified: true, probeResult: false));
    }

    // ── selector boundary locks: only CouldntConfirm arms the marker ──────────

    [Fact]
    public void Classify_couldnt_confirm_is_the_only_marker_arming_outcome()
    {
        // The WUA branch arms UnverifiedRebootProbeOnly iff kind == CouldntConfirm. These four lock the
        // boundary the marker keys off so a future selector change can't silently re-arm it elsewhere.
        Assert.Equal(RebootOutcomeKind.CouldntConfirm,
            RebootOutcomeSelector.Classify(failed: null, remaining: 0, rebootStillPending: null, scanFailed: false));
        Assert.Equal(RebootOutcomeKind.CouldntRescan,
            RebootOutcomeSelector.Classify(failed: null, remaining: 0, rebootStillPending: null, scanFailed: true));
        Assert.Equal(RebootOutcomeKind.UpToDate,
            RebootOutcomeSelector.Classify(failed: null, remaining: 0, rebootStillPending: false, scanFailed: false));
        Assert.Equal(RebootOutcomeKind.Remaining,
            RebootOutcomeSelector.Classify(failed: null, remaining: 1, rebootStillPending: null, scanFailed: false));
    }

    // ── heal target honesty ───────────────────────────────────────────────────

    [Fact]
    public void Heal_target_derives_green_Done_and_message_claims_no_fabricated_count()
    {
        // The heal writes UpdatePhase=Done, RebootRequired=false → PatchState.Done (green).
        var c = new Computer("HOST") { UpdatePhase = "Done", RebootRequired = false };
        Assert.Equal(PatchState.Done, c.PatchState);

        // And it writes BackOnlineUpToDate(null): honest "up to date" with no invented installed count.
        string msg = RebootOutcomeMessages.BackOnlineUpToDate(null);
        Assert.Contains("up to date", msg);
        Assert.DoesNotContain(msg, (char ch) => char.IsDigit(ch));
    }
}
