using System.Collections.Generic;
using System.Linq;
using Vivre.Core.Models;

namespace Vivre.Core.Updates;

/// <summary>Pure pre-Stage decision predicates for the Server 2016 lane (no I/O), so the Stage
/// short-circuits are unit-testable independently of the view-model.</summary>
public static class StagePreconditions
{
    /// <summary>A box already staged AND reboot-pending this session should not be re-staged —
    /// the operator just needs to run the Reboot Wave. (Both conditions required: a reboot pending
    /// for an unrelated reason on a never-staged box is NOT "already staged".)</summary>
    public static bool IsAlreadyStaged(bool rebootRequired, bool stagedThisSession) =>
        rebootRequired && stagedThisSession;

    /// <summary>The box is already at the target UBR (no Stage needed) — true ONLY for a definitive
    /// Verified verdict. WrongBuild (readable but different) and Unreachable (null/unreadable read)
    /// both return false so the caller proceeds to Stage — fail-open on an unreadable box.</summary>
    public static bool IsAlreadyCurrent(LcuVerifyOutcome outcome) => outcome == LcuVerifyOutcome.Verified;

    /// <summary>The Stage targets that have NOT been scanned this session (LastScannedApplicable is null).
    /// An empty result means every target is scanned and Stage may proceed; a non-empty result lists the
    /// machine names the operator must scan first.</summary>
    public static IReadOnlyList<string> UnscannedThisSession(IEnumerable<Computer> targets) =>
        targets.Where(c => c.LastScannedApplicable is null).Select(c => c.Name).ToList();

    /// <summary>The 2016-staged-patching rule the panel's Stage / Clean up / Verify act on: a Server 2016
    /// box the operator has explicitly flagged for staged patching. A non-flagged 2016 box patches via
    /// Windows Update, so the DISM lane never touches it.</summary>
    public static bool IsStageTarget(Computer c) => LcuRouting.Is2016(c.OsBuild) && c.RequiresStagedPatching;

    /// <summary>True when <paramref name="boxes"/> contains at least one flagged Server 2016 box — i.e. the
    /// panel's Stage / Clean up / Verify have something to act on. When false those buttons would silently
    /// no-op, so the View shows the "mark a box for staged patching first" guidance instead of touching
    /// anything.</summary>
    public static bool HasAnyStageTarget(IEnumerable<Computer> boxes) => boxes.Any(IsStageTarget);
}
