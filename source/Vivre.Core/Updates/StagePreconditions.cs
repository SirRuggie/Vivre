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
}
