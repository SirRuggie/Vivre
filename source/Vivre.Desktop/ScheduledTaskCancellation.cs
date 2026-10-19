using Vivre.Core.Models;
using Vivre.Core.Updates;
using Vivre.Desktop.ViewModels;

namespace Vivre.Desktop;

/// <summary>Review cancellation once; dispatch only the captured, still-valid machines.</summary>
public static class ScheduledTaskCancellation
{
    public static async Task ReviewAsync(WorkspaceViewModel vm, Func<string, Task<bool>> confirm,
        Func<IReadOnlyList<Computer>, Task> execute, Action stale)
    {
        var scope = InstallTargetScope.Capture(vm.SelectedComputers);
        if (vm.IsClosed || scope.Targets.Count == 0) return;
        string prompt = $"Remove all Vivre scheduled tasks from {scope.Targets.Count} selected machine(s)?\n\n"
            + "This includes scheduled installs and reboots (all tasks named Vivre_*). "
            + "Other scheduled tasks are unchanged. Removing a schedule does not stop an operation that has already started.";
        if (!await confirm(prompt)) return;
        if (vm.IsClosed || !scope.IsUnchanged() || scope.Targets.Any(row => !vm.Computers.Contains(row)))
        {
            stale();
            return;
        }
        await execute(scope.Targets);
    }
}
