using System.Collections.Generic;
using System.Linq;
using Vivre.Core.Models;

namespace Vivre.Core.Updates;

/// <summary>A Settings-vs-scan cumulative-update KB disagreement on one box: Windows found a different CU KB
/// than the one set in Settings, so staging that Settings KB would target the wrong package. Surfaced as the
/// decision dialog's top warning.</summary>
/// <param name="MachineName">The box whose scan disagrees with Settings.</param>
/// <param name="SettingsKb">The CU KB configured in Settings (normalized, prefix-less).</param>
/// <param name="ScanKb">The CU KB the box's scan actually found (normalized, prefix-less).</param>
public sealed record StagedCuKbMismatch(string MachineName, string SettingsKb, string ScanKb);

/// <summary>
/// The partition of an Install / Install-all target set with respect to the Server 2016 staged-patching flag.
/// <list type="bullet">
///   <item><description><see cref="FlaggedNotStaged"/> — flagged 2016 boxes whose CU isn't staged or verified
///   yet: these need the operator's decision (the dialog lists them).</description></item>
///   <item><description><see cref="Normal"/> — everything else (non-2016, non-flagged 2016 → WUA, flagged boxes
///   already staged → skip note, flagged boxes already verified → WUA minor): the normal install handles each
///   correctly per-row.</description></item>
/// </list>
/// </summary>
/// <param name="FlaggedNotStaged">Flagged 2016 boxes awaiting a stage decision (the dialog set).</param>
/// <param name="Normal">All other targets — proceed via the normal install.</param>
/// <param name="Mismatches">Per-box Settings-vs-scan CU KB disagreements among the dialog set.</param>
public sealed record StagedInstallPlan(
    IReadOnlyList<Computer> FlaggedNotStaged,
    IReadOnlyList<Computer> Normal,
    IReadOnlyList<StagedCuKbMismatch> Mismatches)
{
    /// <summary>True when at least one box needs the staged-patching decision (the dialog must be shown).</summary>
    public bool NeedsDecision => FlaggedNotStaged.Count > 0;
}

/// <summary>
/// Pure planner for the staged-patching decision: given the install targets and the Settings CU KB, decides which
/// flagged 2016 boxes still need their cumulative update staged (so the View can prompt) versus which proceed via
/// the normal install. No I/O — reads only in-memory row state + the last scan — so it's unit-testable independent
/// of the view model.
/// </summary>
public static class StagedInstallPlanner
{
    /// <summary>Partitions <paramref name="targets"/> and computes per-box Settings-vs-scan CU KB mismatches.</summary>
    /// <param name="targets">The Install / Install-all target rows.</param>
    /// <param name="settingsCuKb">This month's CU KB from Settings (with or without the "KB" prefix); null/empty
    /// when not configured — no mismatch can be computed then.</param>
    public static StagedInstallPlan Plan(IEnumerable<Computer> targets, string? settingsCuKb)
    {
        var flaggedNotStaged = new List<Computer>();
        var normal = new List<Computer>();
        var mismatches = new List<StagedCuKbMismatch>();

        string? settingsKbNorm = string.IsNullOrWhiteSpace(settingsCuKb)
            ? null
            : Lcu2016CuMatcher.NormalizeKb(settingsCuKb);

        foreach (Computer c in targets ?? Enumerable.Empty<Computer>())
        {
            if (NeedsStageDecision(c))
            {
                flaggedNotStaged.Add(c);

                // Mismatch is only meaningful for a box we're about to prompt about, that has been scanned, and
                // whose scan yields a confident single CU KB that disagrees with Settings.
                if (settingsKbNorm is not null)
                {
                    string? scanKb = Lcu2016CuMatcher.FindCuKb(
                        c.ApplicableUpdates.Select(u => (u.Title, u.Kb)));
                    if (scanKb is not null
                        && !string.Equals(scanKb, settingsKbNorm, StringComparison.OrdinalIgnoreCase))
                    {
                        mismatches.Add(new StagedCuKbMismatch(c.Name, settingsKbNorm, scanKb));
                    }
                }
            }
            else
            {
                normal.Add(c);
            }
        }

        return new StagedInstallPlan(flaggedNotStaged, normal, mismatches);
    }

    /// <summary>A flagged 2016 box needs the stage decision when its CU is neither staged (this session) nor
    /// already verified committed. Already-staged → run Reboot Wave (handled by the normal install's skip note);
    /// already-verified → its remaining minor updates go via WUA (normal install). Non-2016 / non-flagged boxes
    /// never need the decision.</summary>
    public static bool NeedsStageDecision(Computer c) =>
        LcuRouting.Is2016(c.OsBuild)
        && c.RequiresStagedPatching
        && !StagePreconditions.IsAlreadyStaged(c.RebootRequired == true, c.StagedThisSession)
        && !c.LcuVerifiedThisSession;

    /// <summary>
    /// Pre-dialog currency split for the flagged-not-staged boxes: before prompting, decide which boxes are
    /// ALREADY current this cycle (so they skip the dialog and just install their minor updates via WUA) versus
    /// which still need the staging decision. A box is already current if EITHER it's been verified this session
    /// (<see cref="Computer.LcuVerifiedThisSession"/>) OR its pre-stage UBR read came back
    /// <see cref="LcuVerifyOutcome.Verified"/> (UBR == this month's target). <paramref name="outcomeFor"/> supplies
    /// that read per box (null = not read / unreadable). FAIL-OPEN: anything other than a definitive Verified —
    /// <see cref="LcuVerifyOutcome.WrongBuild"/>, <see cref="LcuVerifyOutcome.Unreachable"/>, or a null read —
    /// leaves the box in <c>StillNeed</c>; we never skip a box we couldn't confirm.
    /// </summary>
    public static (IReadOnlyList<Computer> AlreadyCurrent, IReadOnlyList<Computer> StillNeed) PartitionByCurrency(
        IEnumerable<Computer> flaggedNotStaged,
        Func<Computer, LcuVerifyOutcome?> outcomeFor)
    {
        var alreadyCurrent = new List<Computer>();
        var stillNeed = new List<Computer>();

        foreach (Computer c in flaggedNotStaged ?? Enumerable.Empty<Computer>())
        {
            LcuVerifyOutcome? outcome = outcomeFor(c);
            bool current = c.LcuVerifiedThisSession
                || (outcome is { } o && StagePreconditions.IsAlreadyCurrent(o));

            (current ? alreadyCurrent : stillNeed).Add(c);
        }

        return (alreadyCurrent, stillNeed);
    }
}
