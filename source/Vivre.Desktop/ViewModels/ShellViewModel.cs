using System.Collections.ObjectModel;
using Vivre.Core.Credentials;
using Vivre.Core.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Vivre.Desktop.ViewModels;

/// <summary>Which Fleet sub-section is active.</summary>
public enum FleetSection { Health, Patching }

/// <summary>
/// The window-level view model: owns the open tabs in two independent collections — one per Fleet
/// section (Health and Patching). Each tab is an independent <see cref="WorkspaceViewModel"/>
/// created via the injected factory. Always keeps at least one tab open per section.
/// </summary>
public partial class ShellViewModel : ObservableObject
{
    private readonly Func<WorkspaceViewModel> _newWorkspace;
    // Monotonic counters so default tab titles never collide after a middle tab is closed.
    private int _nextHealthTabNumber = 1;
    private int _nextPatchingTabNumber = 1;

    // Cross-Domain RDP is a per-workstation feature — it only opens when Vivre runs on this machine.
    private const string CrossDomainRdpMachine = "APVHOP";

    // Gates Cross-Domain RDP to the designated host (CrossDomainRdpMachine). Set false only for
    // temporary visual testing of the RDP nav item on a non-APVHOP machine.
    private const bool RequireRdpHost = true;

    public ShellViewModel(Func<WorkspaceViewModel> workspaceFactory, CrossDomainRdpViewModel rdpViewModel, CredentialStore credentials, IActivityLog activityLog)
    {
        _newWorkspace = workspaceFactory;
        RdpViewModel = rdpViewModel;
        Credentials = credentials;
        ActivityLog = new ActivityLogViewModel(activityLog);

        // Seed each section with one tab.
        AddHealthTab();
        AddPatchingTab();

        // Default to Health section.
        ActiveFleetSection = FleetSection.Health;
        SyncSelectedTab();
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

    /// <summary>Health section workspace tabs — <c>IsUpdateMode=false</c>, never changed.</summary>
    public ObservableCollection<WorkspaceViewModel> HealthTabs { get; } = [];

    /// <summary>Patching section workspace tabs — <c>IsUpdateMode=true</c>, never changed.</summary>
    public ObservableCollection<WorkspaceViewModel> PatchingTabs { get; } = [];

    [ObservableProperty]
    public partial WorkspaceViewModel? SelectedHealthTab { get; set; }

    [ObservableProperty]
    public partial WorkspaceViewModel? SelectedPatchingTab { get; set; }

    /// <summary>Which Fleet sub-section is currently shown (Health or Patching). Default Health.</summary>
    [ObservableProperty]
    public partial FleetSection ActiveFleetSection { get; set; }

    /// <summary>
    /// Routes to the active section's selected tab. The shared toolbar and status bar bind here —
    /// they work unchanged because this always points at the correct <see cref="WorkspaceViewModel"/>.
    /// </summary>
    public WorkspaceViewModel? SelectedTab =>
        ActiveFleetSection == FleetSection.Health ? SelectedHealthTab : SelectedPatchingTab;

    /// <summary>True when the active tab is a machine workspace — gates the machine-only chrome.</summary>
    public bool IsWorkspaceTab => SelectedTab is not null;

    /// <summary>Cross-Domain RDP nav item visibility: always true when RequireRdpHost is false (for
    /// testing); when true, gates to the designated host (CrossDomainRdpMachine).</summary>
    public bool IsCrossDomainRdpAvailable =>
        !RequireRdpHost || string.Equals(Environment.MachineName, CrossDomainRdpMachine, StringComparison.OrdinalIgnoreCase);

    partial void OnActiveFleetSectionChanged(FleetSection value)
    {
        SyncSelectedTab();
    }

    partial void OnSelectedHealthTabChanged(WorkspaceViewModel? value)
    {
        if (ActiveFleetSection == FleetSection.Health)
        {
            OnPropertyChanged(nameof(SelectedTab));
            OnPropertyChanged(nameof(IsWorkspaceTab));
        }
    }

    partial void OnSelectedPatchingTabChanged(WorkspaceViewModel? value)
    {
        if (ActiveFleetSection == FleetSection.Patching)
        {
            OnPropertyChanged(nameof(SelectedTab));
            OnPropertyChanged(nameof(IsWorkspaceTab));
        }
    }

    private void SyncSelectedTab()
    {
        OnPropertyChanged(nameof(SelectedTab));
        OnPropertyChanged(nameof(IsWorkspaceTab));
    }

    // --- tab management ---

    private WorkspaceViewModel CreateWorkspace(bool isUpdateMode, string title)
    {
        WorkspaceViewModel workspace = _newWorkspace();
        workspace.Title = title;
        workspace.IsUpdateMode = isUpdateMode;
        return workspace;
    }

    private void AddHealthTab()
    {
        WorkspaceViewModel workspace = CreateWorkspace(false, $"Tab {_nextHealthTabNumber++}");
        HealthTabs.Add(workspace);
        SelectedHealthTab = workspace;
    }

    private void AddPatchingTab()
    {
        WorkspaceViewModel workspace = CreateWorkspace(true, $"Tab {_nextPatchingTabNumber++}");
        PatchingTabs.Add(workspace);
        SelectedPatchingTab = workspace;
    }

    [RelayCommand]
    private void NewTab()
    {
        if (ActiveFleetSection == FleetSection.Health)
        {
            AddHealthTab();
        }
        else
        {
            AddPatchingTab();
        }
    }

    [RelayCommand]
    private void CloseTab(WorkspaceViewModel? tab)
    {
        if (tab is null) return;

        ObservableCollection<WorkspaceViewModel> tabs =
            ActiveFleetSection == FleetSection.Health ? HealthTabs : PatchingTabs;

        int index = tabs.IndexOf(tab);
        if (index < 0) return;

        tabs.Remove(tab);

        // Always keep at least one tab open per section.
        if (tabs.Count == 0)
        {
            if (ActiveFleetSection == FleetSection.Health)
                AddHealthTab();
            else
                AddPatchingTab();
        }
        else
        {
            WorkspaceViewModel next = tabs[Math.Clamp(index, 0, tabs.Count - 1)];
            if (ActiveFleetSection == FleetSection.Health)
                SelectedHealthTab = next;
            else
                SelectedPatchingTab = next;
        }

        // Tear the closed tab down (stop background work and release resources).
        (tab as IDisposable)?.Dispose();
    }

    /// <summary>Flips ActiveFleetSection Health↔Patching (Ctrl+M). Ensures the new section has ≥1 tab.</summary>
    public void ToggleFleetSection()
    {
        ActiveFleetSection = ActiveFleetSection == FleetSection.Health
            ? FleetSection.Patching
            : FleetSection.Health;

        // Ensure the newly-shown section has at least one tab.
        if (ActiveFleetSection == FleetSection.Health && HealthTabs.Count == 0)
            AddHealthTab();
        else if (ActiveFleetSection == FleetSection.Patching && PatchingTabs.Count == 0)
            AddPatchingTab();
    }

    /// <summary>All open workspace tabs across both sections — used for resource iteration (timers, dispose).</summary>
    public IEnumerable<WorkspaceViewModel> AllTabs => HealthTabs.Concat(PatchingTabs);
}
