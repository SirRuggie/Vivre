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
    private readonly Func<CrossDomainRdpViewModel> _newCrossDomainRdp;
    // Monotonic so default tab titles never collide after a middle tab is closed.
    private int _nextTabNumber = 1;

    // Cross-Domain RDP is a per-workstation feature — it only appears / opens when Vivre runs on this machine.
    private const string CrossDomainRdpMachine = "APVHOP";

    public ShellViewModel(Func<WorkspaceViewModel> workspaceFactory, Func<CrossDomainRdpViewModel> crossDomainRdpFactory, CredentialStore credentials, IActivityLog activityLog)
    {
        _newWorkspace = workspaceFactory;
        _newCrossDomainRdp = crossDomainRdpFactory;
        Credentials = credentials;
        ActivityLog = new ActivityLogViewModel(activityLog);
        NewTab();
    }

    /// <summary>App-wide credential store (shared across tabs; edited from Settings).</summary>
    public CredentialStore Credentials { get; }

    /// <summary>The activity-log panel (search/filter over the shared history).</summary>
    public ActivityLogViewModel ActivityLog { get; }

    /// <summary>Open tabs — a mixed collection of machine workspaces and the singleton Cross-Domain RDP tab
    /// (both <see cref="ITabViewModel"/>). The shell's TabControl renders each by its concrete type.</summary>
    public ObservableCollection<ITabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    public partial ITabViewModel? SelectedTab { get; set; }

    /// <summary>True when the active tab is a machine workspace — gates the machine-only chrome (command
    /// bar, fleet band, status bar) so the Cross-Domain RDP tab shows only its own content.</summary>
    public bool IsWorkspaceTab => SelectedTab is WorkspaceViewModel;

    // The View menu's three radio dots. Each always returns a bool (no binding-path failures), so the menu
    // renders cleanly for any tab type. Machines / Windows Update reflect a machine tab's mode; Cross-Domain
    // RDP reflects its own tab. Bound one-way (display only) — they're set by the menu's Click handlers.
    public bool IsMachinesView => SelectedTab is WorkspaceViewModel { IsUpdateMode: false };

    public bool IsWindowsUpdateView => SelectedTab is WorkspaceViewModel { IsUpdateMode: true };

    public bool IsCrossDomainRdpView => SelectedTab is CrossDomainRdpViewModel;

    /// <summary>Cross-Domain RDP only shows on its designated workstation — the View-menu item binds its
    /// visibility here, and OpenCrossDomainRdp is a no-op elsewhere.</summary>
    public bool IsCrossDomainRdpAvailable =>
        string.Equals(Environment.MachineName, CrossDomainRdpMachine, StringComparison.OrdinalIgnoreCase);

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
        OnPropertyChanged(nameof(IsCrossDomainRdpView));
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

    /// <summary>Opens the Cross-Domain RDP tab, or re-selects it if already open (it's a singleton —
    /// one host tree, one set of live sessions).</summary>
    [RelayCommand]
    private void OpenCrossDomainRdp()
    {
        if (!IsCrossDomainRdpAvailable)
        {
            return;
        }

        CrossDomainRdpViewModel? existing = Tabs.OfType<CrossDomainRdpViewModel>().FirstOrDefault();
        if (existing is not null)
        {
            SelectedTab = existing;
            return;
        }

        CrossDomainRdpViewModel vm = _newCrossDomainRdp();
        Tabs.Add(vm);
        SelectedTab = vm;
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

        // Always keep at least one machine workspace open (the Cross-Domain RDP tab alone isn't enough to work in).
        if (!Tabs.OfType<WorkspaceViewModel>().Any())
        {
            NewTab();
        }
        else if (ReferenceEquals(SelectedTab, tab))
        {
            SelectedTab = Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
        }

        // Now that selection has moved off it, tear the closed tab down (stop background work / live sessions
        // and release resources) rather than leaving it to the GC. Both tab types are IDisposable.
        (tab as IDisposable)?.Dispose();
    }
}
