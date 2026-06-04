using System.Collections.ObjectModel;
using Vivre.Core.Credentials;
using Vivre.Core.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Vivre.Desktop.ViewModels;

/// <summary>
/// The window-level view model: owns the open tabs. Each tab is an independent
/// <see cref="WorkspaceViewModel"/> created via the injected factory (so they all
/// share the same singleton services from the composition root). Always keeps at
/// least one tab open.
/// </summary>
public partial class ShellViewModel : ObservableObject
{
    private readonly Func<WorkspaceViewModel> _newWorkspace;
    // Monotonic so default tab titles never collide after a middle tab is closed.
    private int _nextTabNumber = 1;

    public ShellViewModel(Func<WorkspaceViewModel> workspaceFactory, CredentialStore credentials, IActivityLog activityLog)
    {
        _newWorkspace = workspaceFactory;
        Credentials = credentials;
        ActivityLog = new ActivityLogViewModel(activityLog);
        NewTab();
    }

    /// <summary>App-wide credential store (shared across tabs; edited from Settings).</summary>
    public CredentialStore Credentials { get; }

    /// <summary>The activity-log panel (search/filter over the shared history).</summary>
    public ActivityLogViewModel ActivityLog { get; }

    public ObservableCollection<WorkspaceViewModel> Tabs { get; } = [];

    [ObservableProperty]
    public partial WorkspaceViewModel? SelectedTab { get; set; }

    [RelayCommand]
    private void NewTab()
    {
        WorkspaceViewModel workspace = _newWorkspace();
        workspace.Title = $"Tab {_nextTabNumber++}";
        Tabs.Add(workspace);
        SelectedTab = workspace;
    }

    [RelayCommand]
    private void CloseTab(WorkspaceViewModel? workspace)
    {
        if (workspace is null)
        {
            return;
        }

        int index = Tabs.IndexOf(workspace);
        Tabs.Remove(workspace);

        if (Tabs.Count == 0)
        {
            NewTab();
        }
        else if (SelectedTab == workspace)
        {
            SelectedTab = Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
        }

        // Now that selection has moved off it, tear the closed tab down: stop its monitor loop + any
        // in-flight sweeps and release its cancellation source (rather than leaving it all to the GC).
        workspace.Dispose();
    }
}
