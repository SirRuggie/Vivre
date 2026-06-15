using System;
using System.Collections.Generic;
using Vivre.Core.Models;
using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Tests for <see cref="StagePreconditions.IsAlreadyStaged"/> — both conditions required.
/// </summary>
public class StagePreconditionsTests
{
    [Fact]
    public void BothTrue_returns_true()
    {
        bool result = StagePreconditions.IsAlreadyStaged(rebootRequired: true, stagedThisSession: true);

        Assert.True(result);
    }

    [Fact]
    public void BothFalse_returns_false()
    {
        bool result = StagePreconditions.IsAlreadyStaged(rebootRequired: false, stagedThisSession: false);

        Assert.False(result);
    }

    [Fact]
    public void RebootRequired_only_returns_false()
    {
        // A reboot-pending-only box (not staged this session) must not be skipped.
        bool result = StagePreconditions.IsAlreadyStaged(rebootRequired: true, stagedThisSession: false);

        Assert.False(result);
    }

    [Fact]
    public void StagedThisSession_only_returns_false()
    {
        // Staged but no reboot-pending means the flag cleared; allow re-stage.
        bool result = StagePreconditions.IsAlreadyStaged(rebootRequired: false, stagedThisSession: true);

        Assert.False(result);
    }
}

/// <summary>
/// Tests for <see cref="StagePreconditions.IsAlreadyCurrent"/> — true ONLY on a definitive
/// Verified verdict; WrongBuild and Unreachable both fail-open (proceed to Stage).
/// </summary>
public class StagePreconditions_IsAlreadyCurrent_Tests
{
    [Fact]
    public void Verified_returns_true()
    {
        // Definitive match — skip Stage.
        bool result = StagePreconditions.IsAlreadyCurrent(LcuVerifyOutcome.Verified);

        Assert.True(result);
    }

    [Fact]
    public void WrongBuild_returns_false()
    {
        // UBR readable but differs — different build, proceed to Stage.
        bool result = StagePreconditions.IsAlreadyCurrent(LcuVerifyOutcome.WrongBuild);

        Assert.False(result);
    }

    [Fact]
    public void Unreachable_returns_false()
    {
        // Null/failed read — fail-open, proceed to Stage (DISM will catch an already-current box).
        bool result = StagePreconditions.IsAlreadyCurrent(LcuVerifyOutcome.Unreachable);

        Assert.False(result);
    }
}

/// <summary>
/// Tests for <see cref="StagePreconditions.UnscannedThisSession"/> — returns the names of targets
/// whose <c>LastScannedApplicable</c> is null (i.e. not yet scanned this session).
/// </summary>
public class StagePreconditions_UnscannedThisSession_Tests
{
    [Fact]
    public void AllScanned_returns_empty_list()
    {
        // Every target has been scanned — Stage may proceed.
        var targets = new List<Computer>
        {
            new Computer("BOX01") { LastScannedApplicable = DateTime.Now },
            new Computer("BOX02") { LastScannedApplicable = DateTime.Now },
        };

        IReadOnlyList<string> result = StagePreconditions.UnscannedThisSession(targets);

        Assert.Empty(result);
    }

    [Fact]
    public void OneUnscanned_returns_that_box_name()
    {
        // BOX02 has not been scanned — it must appear in the result; BOX01 must not.
        var targets = new List<Computer>
        {
            new Computer("BOX01") { LastScannedApplicable = DateTime.Now },
            new Computer("BOX02") { LastScannedApplicable = null },
        };

        IReadOnlyList<string> result = StagePreconditions.UnscannedThisSession(targets);

        Assert.Equal(["BOX02"], result);
    }

    [Fact]
    public void PostRebootRescan_satisfies_gate()
    {
        // A box whose LastScannedApplicable was set (as a post-reboot rescan would) is NOT unscanned.
        var targets = new List<Computer>
        {
            new Computer("REBOOTED") { LastScannedApplicable = DateTime.Now.AddMinutes(-2) },
        };

        IReadOnlyList<string> result = StagePreconditions.UnscannedThisSession(targets);

        Assert.DoesNotContain("REBOOTED", result);
    }

    [Fact]
    public void EmptyInput_returns_empty_list()
    {
        // No targets at all — trivially satisfied (nothing blocks Stage).
        IReadOnlyList<string> result = StagePreconditions.UnscannedThisSession([]);

        Assert.Empty(result);
    }
}

/// <summary>
/// Tests for <see cref="StagePreconditions.IsStageTarget"/> / <see cref="StagePreconditions.HasAnyStageTarget"/>
/// — the "flagged Server 2016 box" rule the panel's Clean up / Stage / Verify act on. When no box qualifies the
/// View shows the "mark a box for staged patching first" guidance instead of silently no-opping.
/// </summary>
public class StagePreconditions_StageTarget_Tests
{
    private const int Server2016 = 14393; // LcuRouting.Server2016Build
    private const int Server2019 = 17763;

    [Fact]
    public void Flagged_2016_box_is_a_stage_target()
    {
        var box = new Computer("BOX") { OsBuild = Server2016, RequiresStagedPatching = true };

        Assert.True(StagePreconditions.IsStageTarget(box));
    }

    [Fact]
    public void Unflagged_2016_box_is_not_a_stage_target()
    {
        // A 2016 box the operator hasn't marked patches via Windows Update — not the DISM lane.
        var box = new Computer("BOX") { OsBuild = Server2016, RequiresStagedPatching = false };

        Assert.False(StagePreconditions.IsStageTarget(box));
    }

    [Fact]
    public void Flagged_non_2016_box_is_not_a_stage_target()
    {
        // The staged flag only routes 2016 boxes; a flagged 2019 box is still a normal-WUA box.
        var box = new Computer("BOX") { OsBuild = Server2019, RequiresStagedPatching = true };

        Assert.False(StagePreconditions.IsStageTarget(box));
    }

    [Fact]
    public void Box_with_unknown_build_is_not_a_stage_target()
    {
        // An unscanned box (null OsBuild) can't be a 2016 stage target.
        var box = new Computer("BOX") { OsBuild = null, RequiresStagedPatching = true };

        Assert.False(StagePreconditions.IsStageTarget(box));
    }

    [Fact]
    public void HasAnyStageTarget_true_when_one_flagged_2016_present()
    {
        var boxes = new List<Computer>
        {
            new("A") { OsBuild = Server2019, RequiresStagedPatching = true },
            new("B") { OsBuild = Server2016, RequiresStagedPatching = false },
            new("C") { OsBuild = Server2016, RequiresStagedPatching = true },
        };

        Assert.True(StagePreconditions.HasAnyStageTarget(boxes));
    }

    [Fact]
    public void HasAnyStageTarget_false_when_no_flagged_2016()
    {
        // 2016 boxes exist but none are flagged (the gap the guidance dialog covers), plus a flagged 2019 box.
        var boxes = new List<Computer>
        {
            new("A") { OsBuild = Server2016, RequiresStagedPatching = false },
            new("B") { OsBuild = Server2019, RequiresStagedPatching = true },
        };

        Assert.False(StagePreconditions.HasAnyStageTarget(boxes));
    }

    [Fact]
    public void HasAnyStageTarget_false_for_empty()
    {
        Assert.False(StagePreconditions.HasAnyStageTarget([]));
    }
}
