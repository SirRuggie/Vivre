using System;
using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Tests for <see cref="Lcu2016RowState.MapStageTerminal"/> — the load-bearing STAGE decision. The
/// cardinal invariant these protect (against ~20 production 2016 boxes): a servicing-busy <b>Deferred</b>
/// refusal must NEVER read as "Staged" — neither the Staged flag nor the amber "run Reboot Wave" message.
/// </summary>
public class Lcu2016RowState_MapStageTerminal_Tests
{
    [Fact]
    public void Deferred_never_reads_as_staged()
    {
        // The whole point of the Deferred phase: a reboot-already-pending refusal did NOT stage anything.
        Lcu2016RowState.StageRowOutcome outcome = Lcu2016RowState.MapStageTerminal(PatchPhase.Deferred, "KB5094122", "ignored");

        Assert.False(outcome.Staged);                            // NEVER staged
        Assert.True(outcome.RebootRequired);                     // but the box IS reboot-pending (amber)
        Assert.Equal("Deferred", outcome.Phase);
        Assert.Contains("reboot", outcome.Message, StringComparison.OrdinalIgnoreCase);
        // It must NOT carry the staged path's "Reboot Wave" message.
        Assert.DoesNotContain("Reboot Wave", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Staged", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PendingReboot_is_a_real_stage_with_reboot_wave_message()
    {
        Lcu2016RowState.StageRowOutcome outcome = Lcu2016RowState.MapStageTerminal(PatchPhase.PendingReboot, "KB5094122", "ignored");

        Assert.True(outcome.Staged);                             // a real stage
        Assert.True(outcome.RebootRequired);
        Assert.Equal("PendingReboot", outcome.Phase);
        Assert.Contains("Staged", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("Reboot Wave", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("KB5094122", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Done_is_already_current_not_staged()
    {
        Lcu2016RowState.StageRowOutcome outcome = Lcu2016RowState.MapStageTerminal(PatchPhase.Done, "KB5094122", "ignored");

        Assert.False(outcome.Staged);
        Assert.False(outcome.RebootRequired);
        Assert.Equal("Done", outcome.Phase);
        Assert.Contains("Already current", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("KB5094122", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Error_surfaces_the_status_message_and_is_not_staged()
    {
        Lcu2016RowState.StageRowOutcome outcome = Lcu2016RowState.MapStageTerminal(PatchPhase.Error, "KB5094122", "DISM add-package failed (0x800f0922)");

        Assert.False(outcome.Staged);
        Assert.False(outcome.RebootRequired);
        Assert.Equal("Error", outcome.Phase);
        Assert.Equal("DISM add-package failed (0x800f0922)", outcome.Message);
    }

    [Fact]
    public void A_deferral_produces_neither_Staged_nor_the_Reboot_Wave_message()
    {
        // Belt-and-braces guard on the cardinal rule, stated as its own assertion.
        Lcu2016RowState.StageRowOutcome deferred = Lcu2016RowState.MapStageTerminal(PatchPhase.Deferred, "KB5094122", "x");
        Lcu2016RowState.StageRowOutcome staged = Lcu2016RowState.MapStageTerminal(PatchPhase.PendingReboot, "KB5094122", "x");

        // Deferred and a real stage must be distinguishable on BOTH the Staged flag and the message.
        Assert.NotEqual(staged.Staged, deferred.Staged);
        Assert.NotEqual(staged.Message, deferred.Message);
    }
}

/// <summary>
/// Tests for <see cref="Lcu2016RowState.MapCleanupTerminal"/> — the three distinct per-box CLEANUP
/// end-states the operator reads at a glance, plus Error.
/// </summary>
public class Lcu2016RowState_MapCleanupTerminal_Tests
{
    [Fact]
    public void Done_reads_cleaned_ready_to_stage()
    {
        (string phase, string message) = Lcu2016RowState.MapCleanupTerminal(PatchPhase.Done, "ignored");

        Assert.Equal("Cleaned", phase);
        Assert.Equal("Cleaned — ready to Stage", message);
    }

    [Fact]
    public void PendingReboot_reads_cleaned_reboot_pending()
    {
        (string phase, string message) = Lcu2016RowState.MapCleanupTerminal(PatchPhase.PendingReboot, "ignored");

        // Still "Cleaned" (so it doesn't read "up to date"), but reboot-pending — the amber state.
        Assert.Equal("Cleaned", phase);
        Assert.Contains("reboot-pending", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reboot before Stage", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deferred_reads_couldnt_clean_reboot_first()
    {
        (string phase, string message) = Lcu2016RowState.MapCleanupTerminal(PatchPhase.Deferred, "ignored");

        Assert.Equal("Deferred", phase);
        Assert.Contains("reboot", message, StringComparison.OrdinalIgnoreCase);
        // It is NOT a "Cleaned" success.
        Assert.DoesNotContain("Cleaned", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Error_surfaces_the_status_message()
    {
        (string phase, string message) = Lcu2016RowState.MapCleanupTerminal(PatchPhase.Error, "DISM cleanup failed (0x80004005)");

        Assert.Equal("Error", phase);
        Assert.Equal("DISM cleanup failed (0x80004005)", message);
    }

    [Fact]
    public void The_three_success_paths_are_distinct_labels()
    {
        (string p1, string m1) = Lcu2016RowState.MapCleanupTerminal(PatchPhase.Done, "x");
        (string p2, string m2) = Lcu2016RowState.MapCleanupTerminal(PatchPhase.PendingReboot, "x");
        (string p3, string m3) = Lcu2016RowState.MapCleanupTerminal(PatchPhase.Deferred, "x");

        // Three distinct operator-facing messages: clean-ready, clean-but-reboot, and couldn't-clean.
        Assert.NotEqual(m1, m2);
        Assert.NotEqual(m1, m3);
        Assert.NotEqual(m2, m3);
        // Deferred is its own phase; the two Cleaned variants share the "Cleaned" chip but differ by reboot state.
        Assert.Equal("Cleaned", p1);
        Assert.Equal("Cleaned", p2);
        Assert.Equal("Deferred", p3);
    }
}

/// <summary>
/// Tests for <see cref="Lcu2016RowState.IsPastCleanupCeiling"/> — the display-only ceiling predicate.
/// Past the ceiling the row gets a "still going, check the box" FLAG; it never cancels or tears down.
/// </summary>
public class Lcu2016RowState_IsPastCleanupCeiling_Tests
{
    [Fact]
    public void Under_the_ceiling_is_false()
    {
        Assert.False(Lcu2016RowState.IsPastCleanupCeiling(TimeSpan.FromHours(7.5), TimeSpan.FromHours(8)));
    }

    [Fact]
    public void At_the_ceiling_is_true()
    {
        // Inclusive — exactly at the ceiling flags.
        Assert.True(Lcu2016RowState.IsPastCleanupCeiling(TimeSpan.FromHours(8), TimeSpan.FromHours(8)));
    }

    [Fact]
    public void Over_the_ceiling_is_true()
    {
        Assert.True(Lcu2016RowState.IsPastCleanupCeiling(TimeSpan.FromHours(9), TimeSpan.FromHours(8)));
    }

    [Fact]
    public void The_default_ceiling_is_eight_hours()
    {
        Assert.Equal(TimeSpan.FromHours(8), Lcu2016RowState.CleanupCeiling);
    }
}

/// <summary>
/// Tests for <see cref="Lcu2016RowState.BuildCleanupProgressLabel"/> — the live host-side "Cleaning —
/// {elapsed}" readout that never looks frozen, across: no percent, with percent, stalled, past-ceiling,
/// and combinations.
/// </summary>
public class Lcu2016RowState_BuildCleanupProgressLabel_Tests
{
    [Fact]
    public void No_percent_shows_just_elapsed()
    {
        string label = Lcu2016RowState.BuildCleanupProgressLabel(TimeSpan.FromMinutes(12), percent: null, stalled: false, pastCeiling: false);

        Assert.Equal("Cleaning — 12m", label);
    }

    [Fact]
    public void With_percent_appends_the_percent()
    {
        string label = Lcu2016RowState.BuildCleanupProgressLabel(TimeSpan.FromMinutes(12), percent: 40, stalled: false, pastCeiling: false);

        Assert.Equal("Cleaning — 12m · 40%", label);
    }

    [Fact]
    public void Stalled_appends_the_may_still_be_working_hint()
    {
        string label = Lcu2016RowState.BuildCleanupProgressLabel(TimeSpan.FromMinutes(12), percent: 40, stalled: true, pastCeiling: false);

        Assert.Contains("12m", label);
        Assert.Contains("40%", label);
        Assert.Contains("looks stalled (may still be working)", label);
    }

    [Fact]
    public void Past_ceiling_appends_the_still_going_flag()
    {
        string label = Lcu2016RowState.BuildCleanupProgressLabel(TimeSpan.FromHours(9), percent: null, stalled: false, pastCeiling: true);

        Assert.Contains("still going, check the box", label);
    }

    [Fact]
    public void Stalled_and_past_ceiling_carry_both_flags()
    {
        string label = Lcu2016RowState.BuildCleanupProgressLabel(TimeSpan.FromHours(9), percent: 88, stalled: true, pastCeiling: true);

        Assert.Contains("88%", label);
        Assert.Contains("looks stalled", label);
        Assert.Contains("still going, check the box", label);
    }

    [Fact]
    public void Seconds_under_a_minute_and_hours_format_compactly()
    {
        Assert.Equal("Cleaning — 30s", Lcu2016RowState.BuildCleanupProgressLabel(TimeSpan.FromSeconds(30), null, false, false));
        Assert.Equal("Cleaning — 2h 5m", Lcu2016RowState.BuildCleanupProgressLabel(TimeSpan.FromMinutes(125), null, false, false));
    }
}
