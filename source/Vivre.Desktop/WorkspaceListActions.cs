using Vivre.Desktop.ViewModels;

namespace Vivre.Desktop;

/// <summary>Shared approval boundary for paste replacement, saved-list opening, and clear.</summary>
internal static class WorkspaceListActions
{
    public static bool Replace(WorkspaceViewModel workspace, IEnumerable<string> names, Func<string, bool> confirm)
    {
        string[] target = names.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] before = workspace.Computers.Select(c => c.Name).ToArray();
        var keep = new HashSet<string>(target, StringComparer.OrdinalIgnoreCase);
        int removed = before.Count(n => !keep.Contains(n));
        if (workspace.IsClosed) throw new InvalidOperationException("This tab has been closed.");
        if (removed > 0)
        {
            if (workspace.HasActiveFleetWork)
                throw new InvalidOperationException("This tab has active operations. Wait for them to finish, or use Stop and wait for cancellation before replacing or clearing its machines.");
            if (!confirm($"Remove {removed} machine(s) from '{workspace.Title}'? Their results will be removed from this tab. Saved lists are unchanged; retained machines keep their results. Removing machines does not cancel remote or scheduled work.")) return false;
            // Modal dialogs pump UI messages: never apply approval to a different membership.
            if (!before.SequenceEqual(workspace.Computers.Select(c => c.Name), StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("The tab's machine list changed while confirmation was open. Review it and try again.");
        }
        workspace.SetComputers(target); // rechecks active work and disposal after the confirmation
        return true;
    }

    public static bool ConfirmExit(IEnumerable<WorkspaceViewModel> workspaces, Func<string, bool> confirm)
    {
        int count = workspaces.Count(w => w.NeedsExitWarning);
        return count == 0 || confirm($"{count} tab(s) have active work, monitoring, or scheduled operations. Exit Vivre?\n\nClosing stops local monitoring/control and requests cancellation of running work. Updates or scripts already started remotely, and scheduled tasks, may continue. Closing does not undo them.");
    }
}
