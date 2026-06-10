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

    // Cross-Domain RDP is a per-workstation feature — it only opens when Vivre runs on this machine.
    private const string CrossDomainRdpMachine = "APVHOP";

    // TODO: set true before release — gates Cross-Domain RDP to the designated host (CrossDomainRdpMachine).
    private const bool RequireRdpHost = false;

    public ShellViewModel(Func<WorkspaceViewModel> workspaceFactory, CrossDomainRdpViewModel rdpViewModel, CredentialStore credentials, IActivityLog activityLog)
    {
        _newWorkspace = workspaceFactory;
        RdpViewModel = rdpViewModel;
        Credentials = credentials;
        ActivityLog = new ActivityLogViewModel(activityLog);
        NewTab();
    }

    /// <summary>App-wide credential store (shared across tabs; edited from Settings).</summary>
    public CredentialStore Credentials { get; }

    /// <summary>The activity-log panel (search/filter over the shared history).</summary>
    public ActivityLogViewModel ActivityLog { get; }

    /// <summary>
    /// The singleton Cross-Domain RDP view model — created once at the composition root and kept
    /// for the app lifetime. The RDP section in <c>ContentHost</c> binds its DataContext here.
    /// </summary>
    public CrossDomainRdpViewModel RdpViewModel { get; }

    /// <summary>Open tabs — workspace tabs only (<see cref="WorkspaceViewModel"/>). Cross-Domain RDP is
    /// now a nav section, not a tab; it lives in its own keep-alive slot in ContentHost.</summary>
    public ObservableCollection<ITabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    public partial ITabViewModel? SelectedTab { get; set; }

    /// <summary>True when the active tab is a machine workspace — gates the machine-only chrome (command
    /// bar, fleet band, status bar).</summary>
    public bool IsWorkspaceTab => SelectedTab is WorkspaceViewModel;

    // The View menu's two radio dots for Machines / Windows Update mode.
    public bool IsMachinesView => SelectedTab is WorkspaceViewModel { IsUpdateMode: false };

    public bool IsWindowsUpdateView => SelectedTab is WorkspaceViewModel { IsUpdateMode: true };

    /// <summary>Cross-Domain RDP nav item visibility: always true when RequireRdpHost is false (for
    /// testing); when true, gates to the designated host (CrossDomainRdpMachine).</summary>
    public bool IsCrossDomainRdpAvailable =>
        !RequireRdpHost || string.Equals(Environment.MachineName, CrossDomainRdpMachine, StringComparison.OrdinalIgnoreCase);

    private WorkspaceViewModel? _modeWatched;

    partial void OnSelectedTabChanged(ITabViewModel? value)
    {
        // Watch the active workspace's mode so the Machines / Windows Update dots stay in sync when it flips.
        if (_modeWatched is not null)
        {
            _modeWatched.PropertyChanged -= OnWatchedWorkspacePropertyChanged;
            _modeWatched = null;
        }

        if (value is WorkspaceViewModel workspace)
        {
            workspace.PropertyChanged += OnWatchedWorkspacePropertyChanged;
            _modeWatched = workspace;
        }

        OnPropertyChanged(nameof(IsWorkspaceTab));
        RaiseViewFlags();
    }

    private void OnWatchedWorkspacePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceViewModel.IsUpdateMode))
        {
            RaiseViewFlags();
        }
    }

    private void RaiseViewFlags()
    {
        OnPropertyChanged(nameof(IsMachinesView));
        OnPropertyChanged(nameof(IsWindowsUpdateView));
    }

    /// <summary>Switches to a machine workspace (the active one, or the first open) and sets its mode — so the
    /// View-menu Machines / Windows Update items act as view-switchers, even from the Cross-Domain RDP tab.</summary>
    public void ShowMachineView(bool updateMode)
    {
        WorkspaceViewModel? workspace = SelectedTab as WorkspaceViewModel ?? Tabs.OfType<WorkspaceViewModel>().FirstOrDefault();
        if (workspace is null)
        {
            return;
        }

        SelectedTab = workspace;
        workspace.IsUpdateMode = updateMode;
    }

    [RelayCommand]
    private void NewTab()
    {
        WorkspaceViewModel workspace = _newWorkspace();
        workspace.Title = $"Tab {_nextTabNumber++}";
        Tabs.Add(workspace);
        SelectedTab = workspace;
    }

    [RelayCommand]
    private void CloseTab(ITabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        int index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);

        // Always keep at least one machine workspace open.
        if (!Tabs.OfType<WorkspaceViewModel>().Any())
        {
            NewTab();
        }
        else if (ReferenceEquals(SelectedTab, tab))
        {
            SelectedTab = Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
        }

        // Tear the closed tab down (stop background work and release resources).
        (tab as IDisposable)?.Dispose();
    }
}
