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
