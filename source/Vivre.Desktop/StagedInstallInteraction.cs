using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Vivre.Core.Models;
using Vivre.Core.Updates;
using Vivre.Desktop.ViewModels;
using Wpf.Ui.Controls;

namespace Vivre.Desktop;

/// <summary>What an install entry point should do after the staged-patching gate ran.</summary>
internal enum StagedInstallOutcome
{
    /// <summary>No flagged-2016 box needed a decision — the caller runs its normal install as usual.</summary>
    ProceedNormally,

    /// <summary>The gate already ran everything it should (the chosen action on the flagged boxes + the normal
    /// install on the rest) — the caller must NOT also install, or it would double-install the normal set.</summary>
    Handled,
}

/// <summary>
/// The View-layer gate for the "Server 2016 staged update required" decision. Every Install / Install-all entry
/// point routes through <see cref="ResolveAsync"/>: when the target set has no flagged-2016 box awaiting a stage
/// decision it returns <see cref="StagedInstallOutcome.ProceedNormally"/> and the caller installs as usual; when
/// it does, the decision dialog is shown. The rest of the fleet ALWAYS installs normally — the choice only decides
/// what happens to the flagged-not-staged boxes (stage the CU, install their minor updates, or skip them for
/// later) — and the flagged action runs concurrently so a 30–60 min stage never blocks the other machines.
/// </summary>
internal static class StagedInstallInteraction
{
    /// <summary>Runs the staged-patching gate for <paramref name="targets"/>. The View must NOT install the
    /// targets itself when this returns <see cref="StagedInstallOutcome.Handled"/> — the gate already installed
    /// the non-flagged remainder (and ran the chosen flagged-box action, if any).</summary>
    public static async Task<StagedInstallOutcome> ResolveAsync(Window owner, WorkspaceViewModel vm, IReadOnlyList<Computer> targets)
    {
        StagedInstallPlan plan = vm.PlanStagedInstall(targets);
        if (!plan.NeedsDecision)
        {
            return StagedInstallOutcome.ProceedNormally;
        }

        var dialog = new StagedInstallDecisionDialog(plan) { Owner = owner };
        dialog.ShowDialog();

        // The rest of the fleet installs normally regardless of the choice; the choice only governs the
        // flagged-not-staged boxes. (Cancel = "deal with these later" — skip them, but don't stop everything.)
        switch (dialog.Choice)
        {
            case StagedInstallChoice.StageCu:
                // Start the normal install on the rest first (fire-and-forget, like the toolbar Install), then run
                // the stage workflow (which may show the scan-gate / package-needed prompts and then fires the
                // stage sweep). Both proceed concurrently — the long stage never holds up the other machines, and
                // the two row sets are disjoint so the sweeps don't collide.
                _ = InstallNormalAsync(vm, plan.Normal);
                await RunStageWorkflowAsync(owner, vm, plan.FlaggedNotStaged);
                break;

            case StagedInstallChoice.MinorOnly:
                // Fire both sweeps (disjoint row sets) — same fire-and-forget shape as a normal install.
                _ = vm.InstallMinorOnlyAsync(plan.FlaggedNotStaged);
                _ = InstallNormalAsync(vm, plan.Normal);
                break;

            default: // Cancel — skip the flagged boxes ("deal with them later"); the rest of the run still installs.
                _ = InstallNormalAsync(vm, plan.Normal);
                break;
        }

        return StagedInstallOutcome.Handled;
    }

    /// <summary>Installs the non-flagged remainder — guarded so an empty set never falls through to
    /// <see cref="WorkspaceViewModel.InstallSelectedAsync"/>'s "empty ⇒ all rows" behavior.</summary>
    private static Task InstallNormalAsync(WorkspaceViewModel vm, IReadOnlyList<Computer> normal) =>
        normal.Count > 0 ? vm.InstallSelectedAsync(normal) : Task.CompletedTask;

    /// <summary>
    /// The Server 2016 chip Stage workflow scoped to a specific set of boxes: the scan-this-session gate, then the
    /// package-readiness loop (guided "drop the .msu here" prompt), then the stage. Shared by the panel's Stage
    /// button and the decision dialog's "Stage CU first" branch so they behave identically. No box is touched
    /// until the package is confirmed present.
    /// </summary>
    public static async Task RunStageWorkflowAsync(Window owner, WorkspaceViewModel vm, IReadOnlyList<Computer> boxes)
    {
        if (boxes.Count == 0)
        {
            return;
        }

        // Scan-this-session gate: every Stage target must have been scanned first (read-only; no box touched).
        IReadOnlyList<string> unscanned = vm.UnscannedStageTargetsFor(boxes);
        if (unscanned.Count > 0)
        {
            string names = string.Join("\n", unscanned.Take(10))
                + (unscanned.Count > 10 ? $"\n+{unscanned.Count - 10} more" : string.Empty);
            var gate = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Scan before staging",
                Content = $"These Server 2016 machine(s) haven't been scanned this session:\n\n{names}\n\n"
                        + "Run Check for updates (Scan) on them first — Stage uses the scan to confirm the "
                        + "box's current state before patching. No machine was touched.",
                CloseButtonText = "OK",
            };
            await gate.ShowDialogAsync();
            return;
        }

        while (true)
        {
            LcuStageReadiness readiness = vm.CheckLcuStageReadiness();
            if (readiness.Ready)
            {
                _ = vm.StageLcuForAsync(boxes); // fire-and-forget, like the panel's Stage button
                return;
            }

            var dialog = new LcuPackageNeededDialog(readiness) { Owner = owner };
            if (dialog.ShowDialog() != true)
            {
                return; // Cancel / closed — nothing staged, no box touched
            }
            // "Stage now": loop and re-check the folder (the operator just dropped the file in).
        }
    }
}
