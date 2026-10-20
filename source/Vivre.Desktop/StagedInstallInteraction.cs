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
/// it does, the decision dialog is shown. Cancel/dismiss dispatches nothing. The explicit installation choices
/// include the normal remainder; the flagged action runs concurrently so a long stage never blocks other machines.
/// </summary>
internal static class StagedInstallInteraction
{
    /// <summary>Runs the staged-patching gate for <paramref name="targets"/>. The View must NOT install the
    /// targets itself when this returns <see cref="StagedInstallOutcome.Handled"/> — the gate already installed
    /// the non-flagged remainder (and ran the chosen flagged-box action, if any).</summary>
    public static async Task<StagedInstallOutcome> ResolveAsync(
        Window owner, WorkspaceViewModel vm, IReadOnlyList<Computer> targets,
        bool allowPendingRebootInstall = false, Func<bool>? targetScopeIsUnchanged = null)
    {
        StagedInstallPlan plan = vm.PlanStagedInstall(targets);
        if (!plan.NeedsDecision)
        {
            return StagedInstallOutcome.ProceedNormally;
        }

        // Before prompting, skip any flagged box already current this cycle (verified this session, or its UBR
        // already matches the target). Already-current boxes are marked verified (the skip is noted in the activity
        // log) and install their minor updates via WUA; fail-open leaves an unreadable box in the dialog. Re-plan so
        // the now-current boxes move from the dialog set into Normal.
        await vm.ResolveAlreadyCurrentAsync(plan.FlaggedNotStaged);
        if (!await EnsureScopeUnchangedAsync(owner, targetScopeIsUnchanged))
        {
            return StagedInstallOutcome.Handled;
        }
        plan = vm.PlanStagedInstall(targets);
        if (!plan.NeedsDecision)
        {
            return StagedInstallOutcome.ProceedNormally; // every flagged box was already current → install all normally
        }

        var dialog = new StagedInstallDecisionDialog(plan) { Owner = owner };
        dialog.ShowDialog();
        if (dialog.Choice == StagedInstallChoice.Cancel) return StagedInstallOutcome.Handled;

        if (!await EnsureScopeUnchangedAsync(owner, targetScopeIsUnchanged))
        {
            return StagedInstallOutcome.Handled;
        }

        await ExecuteChoiceAsync(dialog.Choice,
            () => InstallNormalAsync(vm, plan.Normal, allowPendingRebootInstall),
            () => RunStageWorkflowAsync(owner, vm, plan.FlaggedNotStaged),
            () => vm.InstallMinorOnlyAsync(plan.FlaggedNotStaged, allowPendingRebootInstall));

        return StagedInstallOutcome.Handled;
    }

    // One dispatch boundary for every dialog outcome. Unknown/dismissed choices fail closed.
    private static async Task ExecuteChoiceAsync(
        StagedInstallChoice choice, Func<Task> installNormal, Func<Task> stageCu, Func<Task> installMinor)
    {
        switch (choice)
        {
            case StagedInstallChoice.StageCu:
                // Start the normal install on the rest first (fire-and-forget, like the toolbar Install), then run
                // the stage workflow (which may show the scan-gate / package-needed prompts and then fires the
                // stage sweep). Both proceed concurrently — the long stage never holds up the other machines, and
                // the two row sets are disjoint so the sweeps don't collide.
                _ = installNormal();
                await stageCu();
                break;

            case StagedInstallChoice.MinorOnly:
                // Fire both sweeps (disjoint row sets) — same fire-and-forget shape as a normal install.
                _ = installMinor();
                _ = installNormal();
                break;

            case StagedInstallChoice.SkipCu:
                _ = installNormal();
                break;
        }
    }

    private static async Task<bool> EnsureScopeUnchangedAsync(Window owner, Func<bool>? validator)
    {
        if (validator is null || validator()) return true;
        await new Wpf.Ui.Controls.MessageBox
        {
            Owner = owner,
            Title = "Install cancelled",
            Content = "A target name changed while confirmation was open. Review the targets and try again.",
            CloseButtonText = "OK",
        }.ShowDialogAsync();
        return false;
    }

    /// <summary>Installs the non-flagged remainder — guarded so an empty set never falls through to
    /// <see cref="WorkspaceViewModel.InstallSelectedAsync"/>'s "empty ⇒ all rows" behavior.</summary>
    private static Task InstallNormalAsync(
        WorkspaceViewModel vm, IReadOnlyList<Computer> normal, bool allowPendingRebootInstall) =>
        normal.Count > 0 ? vm.InstallSelectedAsync(normal, allowPendingRebootInstall) : Task.CompletedTask;

    private static readonly HashSet<WorkspaceViewModel> PreparingStage = [];

    /// <summary>
    /// The Server 2016 chip Stage workflow scoped to a specific set of boxes: the scan-this-session gate, then the
    /// package-readiness loop (guided "drop the .msu here" prompt), then the stage. Shared by the panel's Stage
    /// button and the decision dialog's "Stage CU first" branch so they behave identically. No box is touched
    /// until the package is confirmed present.
    /// </summary>
    public static async Task RunStageWorkflowAsync(Window owner, WorkspaceViewModel vm, IReadOnlyList<Computer> boxes)
    {
        // UI-thread entry: a second click must not launch a duplicate wave while preflight awaits I/O.
        if (!PreparingStage.Add(vm)) return;
        try { await RunStageWorkflowCoreAsync(owner, vm, boxes.ToArray()); }
        catch (OperationCanceledException) { /* Stop during package preflight: no staging starts. */ }
        finally { PreparingStage.Remove(vm); }
    }

    private static async Task RunStageWorkflowCoreAsync(Window owner, WorkspaceViewModel vm, IReadOnlyList<Computer> boxes)
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
                Title = "Scan before installing CU",
                Content = $"These Server 2016 machine(s) haven't been scanned this session:\n\n{names}\n\n"
                        + "Run Check for updates (Scan) on them first — Install CU uses the scan to confirm the "
                        + "box's current state before patching. No machine was touched.",
                CloseButtonText = "OK",
            };
            await gate.ShowDialogAsync();
            return;
        }

        while (true)
        {
            LcuStageReadiness readiness = await vm.CheckLcuStageReadinessAsync();
            if (!owner.IsVisible) return;
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
