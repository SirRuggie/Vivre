namespace Vivre.Desktop.ViewModels;

/// <summary>
/// Common surface for anything shown as a top-level tab in the shell's tab strip — the machine
/// <see cref="WorkspaceViewModel"/> and the <see cref="CrossDomainRdpViewModel"/> remote-desktop area. Lets the
/// shell hold a mixed tab collection and the TabControl pick a content view by the tab's concrete type.
/// </summary>
public interface ITabViewModel
{
    /// <summary>Tab header text.</summary>
    string Title { get; }

    /// <summary>Whether the tab shows a close (✕) button and can be closed.</summary>
    bool CanClose { get; }
}
