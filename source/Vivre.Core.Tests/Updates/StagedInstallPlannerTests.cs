using System.Collections.Generic;
using System.Linq;
using Vivre.Core.Models;
using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Tests for <see cref="StagedInstallPlanner"/> — partitions an install target set into the flagged-2016 boxes
/// that need the stage decision versus everything that proceeds via the normal install, and surfaces per-box
/// Settings-vs-scan CU KB mismatches.
/// </summary>
public class StagedInstallPlannerTests
{
    private static Computer Box(
        string name,
        int? osBuild = null,
        bool flagged = false,
        bool? rebootRequired = null,
        bool stagedThisSession = false,
        bool verifiedThisSession = false,
        (string Title, string? Kb)[]? applicable = null)
    {
        var c = new Computer(name)
        {
            OsBuild = osBuild,
            RequiresStagedPatching = flagged,
            RebootRequired = rebootRequired,
            StagedThisSession = stagedThisSession,
            LcuVerifiedThisSession = verifiedThisSession,
        };
        foreach ((string title, string? kb) in applicable ?? [])
        {
            c.ApplicableUpdates.Add(new SelectableUpdate(new SoftwareUpdate(title, kb, IsDownloaded: false, MinDownloadSizeBytes: 0, MaxDownloadSizeBytes: 0)));
        }

        return c;
    }

    [Fact]
    public void Flagged_2016_not_staged_needs_decision()
    {
        var box = Box("BOX", osBuild: 14393, flagged: true);

        StagedInstallPlan plan = StagedInstallPlanner.Plan([box], settingsCuKb: "KB5094122");

        Assert.True(plan.NeedsDecision);
        Assert.Single(plan.FlaggedNotStaged);
        Assert.Empty(plan.Normal);
    }

    [Fact]
    public void Non_flagged_2016_proceeds_normally()
    {
        // A 2016 box NOT marked for staged patching patches via WUA — never the dialog.
        var box = Box("BOX", osBuild: 14393, flagged: false);

        StagedInstallPlan plan = StagedInstallPlanner.Plan([box], settingsCuKb: "KB5094122");

        Assert.False(plan.NeedsDecision);
        Assert.Empty(plan.FlaggedNotStaged);
        Assert.Single(plan.Normal);
    }

    [Theory]
    [InlineData(17763)] // 2019
    [InlineData(20348)] // 2022
    [InlineData(null)]  // unread
    public void Non_2016_proceeds_normally_even_if_flagged(int? build)
    {
        // The flag is only meaningful on 2016 — a non-2016 box is never routed to the dialog.
        var box = Box("BOX", osBuild: build, flagged: true);

        StagedInstallPlan plan = StagedInstallPlanner.Plan([box], settingsCuKb: "KB5094122");

        Assert.False(plan.NeedsDecision);
        Assert.Single(plan.Normal);
    }

    [Fact]
    public void Flagged_2016_already_staged_proceeds_normally()
    {
        // staged + reboot-pending → run Reboot Wave; the normal install gives it the skip note, no dialog.
        var box = Box("BOX", osBuild: 14393, flagged: true, rebootRequired: true, stagedThisSession: true);

        StagedInstallPlan plan = StagedInstallPlanner.Plan([box], settingsCuKb: "KB5094122");

        Assert.False(plan.NeedsDecision);
        Assert.Single(plan.Normal);
    }

    [Fact]
    public void Flagged_2016_already_verified_proceeds_normally()
    {
        // CU committed this session → remaining minor updates go via WUA, no dialog.
        var box = Box("BOX", osBuild: 14393, flagged: true, verifiedThisSession: true);

        StagedInstallPlan plan = StagedInstallPlanner.Plan([box], settingsCuKb: "KB5094122");

        Assert.False(plan.NeedsDecision);
        Assert.Single(plan.Normal);
    }

    [Fact]
    public void Mixed_set_partitions_correctly()
    {
        var flagged = Box("FLAGGED", osBuild: 14393, flagged: true);
        var nonFlagged2016 = Box("PLAIN2016", osBuild: 14393, flagged: false);
        var server2019 = Box("S2019", osBuild: 17763);
        var alreadyStaged = Box("STAGED", osBuild: 14393, flagged: true, rebootRequired: true, stagedThisSession: true);

        StagedInstallPlan plan = StagedInstallPlanner.Plan(
            [flagged, nonFlagged2016, server2019, alreadyStaged], settingsCuKb: "KB5094122");

        Assert.Equal(["FLAGGED"], plan.FlaggedNotStaged.Select(c => c.Name));
        Assert.Equal(["PLAIN2016", "S2019", "STAGED"], plan.Normal.Select(c => c.Name));
    }

    [Fact]
    public void Mismatch_raised_when_scan_kb_differs_from_settings()
    {
        // Settings still says last month's KB; the box's scan found a different CU KB.
        var box = Box("BOX", osBuild: 14393, flagged: true, applicable:
        [
            ("2026-06 Cumulative Update for Windows Server 2016 (KB5094122)", "5094122"),
        ]);

        StagedInstallPlan plan = StagedInstallPlanner.Plan([box], settingsCuKb: "KB5090000");

        StagedCuKbMismatch m = Assert.Single(plan.Mismatches);
        Assert.Equal("BOX", m.MachineName);
        Assert.Equal("5090000", m.SettingsKb);
        Assert.Equal("5094122", m.ScanKb);
    }

    [Fact]
    public void No_mismatch_when_scan_kb_matches_settings()
    {
        var box = Box("BOX", osBuild: 14393, flagged: true, applicable:
        [
            ("2026-06 Cumulative Update for Windows Server 2016 (KB5094122)", "5094122"),
        ]);

        StagedInstallPlan plan = StagedInstallPlanner.Plan([box], settingsCuKb: "KB5094122");

        Assert.Empty(plan.Mismatches);
    }

    [Fact]
    public void No_mismatch_when_box_unscanned()
    {
        // No applicable updates (never scanned) ⇒ no scan KB to compare ⇒ no false warning.
        var box = Box("BOX", osBuild: 14393, flagged: true);

        StagedInstallPlan plan = StagedInstallPlanner.Plan([box], settingsCuKb: "KB5090000");

        Assert.True(plan.NeedsDecision);
        Assert.Empty(plan.Mismatches);
    }

    [Fact]
    public void No_mismatch_when_settings_kb_unset()
    {
        var box = Box("BOX", osBuild: 14393, flagged: true, applicable:
        [
            ("2026-06 Cumulative Update for Windows Server 2016 (KB5094122)", "5094122"),
        ]);

        StagedInstallPlan plan = StagedInstallPlanner.Plan([box], settingsCuKb: null);

        Assert.Empty(plan.Mismatches);
    }

    [Fact]
    public void Empty_targets_needs_no_decision()
    {
        StagedInstallPlan plan = StagedInstallPlanner.Plan([], settingsCuKb: "KB5094122");

        Assert.False(plan.NeedsDecision);
        Assert.Empty(plan.FlaggedNotStaged);
        Assert.Empty(plan.Normal);
        Assert.Empty(plan.Mismatches);
    }

    // --- PartitionByCurrency: pre-dialog "already current this cycle" split (fail-open) ---

    private static LcuVerifyOutcome? OutcomeMap(Dictionary<string, LcuVerifyOutcome?> map, Computer c) =>
        map.TryGetValue(c.Name, out LcuVerifyOutcome? o) ? o : null;

    [Fact]
    public void PartitionByCurrency_all_current_leaves_none_needing_the_dialog()
    {
        var a = Box("A", osBuild: 14393, flagged: true);
        var b = Box("B", osBuild: 14393, flagged: true);
        var map = new Dictionary<string, LcuVerifyOutcome?>
        {
            ["A"] = LcuVerifyOutcome.Verified,
            ["B"] = LcuVerifyOutcome.Verified,
        };

        var (alreadyCurrent, stillNeed) = StagedInstallPlanner.PartitionByCurrency([a, b], c => OutcomeMap(map, c));

        Assert.Equal(["A", "B"], alreadyCurrent.Select(c => c.Name));
        Assert.Empty(stillNeed); // → the gate skips the dialog entirely
    }

    [Fact]
    public void PartitionByCurrency_mixed_keeps_only_non_current_for_the_dialog()
    {
        var current = Box("CURRENT", osBuild: 14393, flagged: true);
        var behind = Box("BEHIND", osBuild: 14393, flagged: true);
        var map = new Dictionary<string, LcuVerifyOutcome?>
        {
            ["CURRENT"] = LcuVerifyOutcome.Verified,
            ["BEHIND"] = LcuVerifyOutcome.WrongBuild,
        };

        var (alreadyCurrent, stillNeed) = StagedInstallPlanner.PartitionByCurrency([current, behind], c => OutcomeMap(map, c));

        Assert.Equal(["CURRENT"], alreadyCurrent.Select(c => c.Name));
        Assert.Equal(["BEHIND"], stillNeed.Select(c => c.Name)); // dialog shows only the box that still needs the CU
    }

    [Theory]
    [InlineData(LcuVerifyOutcome.Unreachable)] // null UBR read → unreachable
    [InlineData(LcuVerifyOutcome.WrongBuild)]  // readable but not at target
    public void PartitionByCurrency_fails_open_keeps_unconfirmed_box_in_the_dialog(LcuVerifyOutcome outcome)
    {
        var box = Box("BOX", osBuild: 14393, flagged: true);
        var map = new Dictionary<string, LcuVerifyOutcome?> { ["BOX"] = outcome };

        var (alreadyCurrent, stillNeed) = StagedInstallPlanner.PartitionByCurrency([box], c => OutcomeMap(map, c));

        Assert.Empty(alreadyCurrent);
        Assert.Equal(["BOX"], stillNeed.Select(c => c.Name));
    }

    [Fact]
    public void PartitionByCurrency_null_read_fails_open()
    {
        // A box the check never produced an outcome for (errored/cancelled) must stay in the dialog set.
        var box = Box("BOX", osBuild: 14393, flagged: true);

        var (alreadyCurrent, stillNeed) = StagedInstallPlanner.PartitionByCurrency([box], _ => null);

        Assert.Empty(alreadyCurrent);
        Assert.Equal(["BOX"], stillNeed.Select(c => c.Name));
    }

    [Fact]
    public void PartitionByCurrency_excludes_verified_this_session_without_a_read()
    {
        // Condition 1: a box already verified this session is current even if the UBR read is null/unavailable.
        var box = Box("BOX", osBuild: 14393, flagged: true, verifiedThisSession: true);

        var (alreadyCurrent, stillNeed) = StagedInstallPlanner.PartitionByCurrency([box], _ => null);

        Assert.Equal(["BOX"], alreadyCurrent.Select(c => c.Name));
        Assert.Empty(stillNeed);
    }
}
