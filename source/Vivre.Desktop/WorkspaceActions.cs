using System.Windows;
using System.Windows.Controls;
using Vivre.Core.Models;
using Vivre.Desktop.ViewModels;

namespace Vivre.Desktop;

public enum WorkspaceAction { CheckSoftware, StageSoftware, RunScript, Maintenance }

/// <summary>Shared dialog entry points: opening a shortcut never starts an operation.</summary>
public static class WorkspaceActions
{
    public static void Show(Window? owner, WorkspaceViewModel vm, WorkspaceAction action, IReadOnlyList<Computer> targets)
    {
        if (targets.Count == 0) return; // never widen an empty selection to the whole tab
        Window dialog = action switch
        {
            WorkspaceAction.CheckSoftware => new SoftwareCheckWindow(vm, targets),
            WorkspaceAction.StageSoftware => new DeployWindow(vm, targets),
            WorkspaceAction.RunScript => new ScriptRunnerWindow(targets, vm.PowerShell, vm.Credentials, vm.Activity, vm.ScriptLibrary, operations: vm.Operations),
            WorkspaceAction.Maintenance => new MaintenanceWindow(vm, targets),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        dialog.Owner = owner;
        if (action == WorkspaceAction.RunScript) dialog.Show();
        else dialog.ShowDialog();
    }

    /// <summary>Four shortcuts bound to one selection snapshot; no all-machines fallback.</summary>
    public static ContextMenu CreateMenu(WorkspaceViewModel vm, Func<bool> isCurrent,
        Action<WorkspaceAction, IReadOnlyList<Computer>> show, Action selectionChanged)
    {
        Computer[] targets = vm.SelectedComputers.ToArray();
        string[] names = targets.Select(row => row.Name).ToArray();
        var menu = new ContextMenu();
        foreach (var (action, title) in new[] {
            (WorkspaceAction.CheckSoftware, "Check software…"),
            (WorkspaceAction.StageSoftware, "Stage software…"),
            (WorkspaceAction.RunScript, "Run script…"),
            (WorkspaceAction.Maintenance, "Maintenance…") })
        {
            var item = new MenuItem { Header = title, IsEnabled = targets.Length > 0, Tag = action };
            item.Click += (_, _) =>
            {
                if (targets.Length == 0) return;
                if (!isCurrent() || vm.SelectedComputers.Count != targets.Length
                    || targets.Where((row, i) => row.Name != names[i] || !vm.SelectedComputers.Contains(row) || !vm.Computers.Contains(row)).Any())
                {
                    selectionChanged();
                    return;
                }
                show(action, targets);
            };
            menu.Items.Add(item);
        }
        return menu;
    }
}
