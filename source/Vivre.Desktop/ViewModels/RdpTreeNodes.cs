using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Vivre.Core.Rdp;

namespace Vivre.Desktop.ViewModels;

/// <summary>Base for a node in the Cross-Domain RDP tree (a folder or a host). Wraps the persisted model and adds
/// the bindable tree state (selection / expansion).</summary>
public abstract partial class RdpNodeViewModel : ObservableObject
{
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public abstract string Name { get; }

    /// <summary>Re-reads <see cref="Name"/> from the model after an edit.</summary>
    public void RefreshName() => OnPropertyChanged(nameof(Name));
}

/// <summary>A folder node — holds child folders and hosts, and (optionally) a credential inherited by its
/// descendants. The wrapper's children mirror the model's <see cref="RdpFolder.Folders"/>/<see cref="RdpFolder.Hosts"/>.</summary>
public sealed partial class RdpFolderNodeViewModel : RdpNodeViewModel
{
    public RdpFolderNodeViewModel(RdpFolder folder, RdpFolderNodeViewModel? parent)
    {
        Folder = folder;
        Parent = parent;
        IsExpanded = true;

        // Keep the tree tidy: folders first, then hosts, each alphabetical. Sort the model lists too, so the
        // order persists and stays consistent with the sorted inserts in AddFolderNode / AddHostNode.
        folder.Folders.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        folder.Hosts.Sort(static (a, b) => string.Compare(HostKey(a), HostKey(b), StringComparison.OrdinalIgnoreCase));

        foreach (RdpFolder child in folder.Folders)
        {
            Children.Add(new RdpFolderNodeViewModel(child, this));
        }

        foreach (RdpHost host in folder.Hosts)
        {
            Children.Add(new RdpHostNodeViewModel(host, this));
        }
    }

    public RdpFolder Folder { get; }

    public RdpFolderNodeViewModel? Parent { get; }

    public override string Name => Folder.Name;

    /// <summary>Subfolder + host nodes, folders first.</summary>
    public ObservableCollection<RdpNodeViewModel> Children { get; } = [];

    public void AddFolderNode(RdpFolderNodeViewModel node)
    {
        // Folders sit above hosts and stay alphabetical.
        int insertAt = 0;
        while (insertAt < Children.Count
            && Children[insertAt] is RdpFolderNodeViewModel existing
            && string.Compare(existing.Name, node.Name, StringComparison.OrdinalIgnoreCase) <= 0)
        {
            insertAt++;
        }

        Children.Insert(insertAt, node);
        Folder.Folders.Insert(insertAt, node.Folder); // folders are the front of Children, so the index matches
    }

    public void AddHostNode(RdpHostNodeViewModel node)
    {
        // Hosts follow the folders, kept alphabetical by display name.
        int folderCount = Children.Count(c => c is RdpFolderNodeViewModel);
        int insertAt = folderCount;
        while (insertAt < Children.Count
            && string.Compare(Children[insertAt].Name, node.Name, StringComparison.OrdinalIgnoreCase) <= 0)
        {
            insertAt++;
        }

        Children.Insert(insertAt, node);
        Folder.Hosts.Insert(insertAt - folderCount, node.Host);
    }

    public void RemoveChild(RdpNodeViewModel node)
    {
        switch (node)
        {
            case RdpFolderNodeViewModel f:
                Folder.Folders.Remove(f.Folder);
                break;
            case RdpHostNodeViewModel h:
                Folder.Hosts.Remove(h.Host);
                break;
        }

        Children.Remove(node);
    }

    /// <summary>The host's display name (server when the name is blank) — the sort key, matching
    /// <see cref="RdpHostNodeViewModel.Name"/>.</summary>
    private static string HostKey(RdpHost host) => string.IsNullOrWhiteSpace(host.Name) ? host.Server : host.Name;
}

/// <summary>A host (leaf) node.</summary>
public sealed partial class RdpHostNodeViewModel : RdpNodeViewModel
{
    public RdpHostNodeViewModel(RdpHost host, RdpFolderNodeViewModel parent)
    {
        Host = host;
        Parent = parent;
    }

    public RdpHost Host { get; }

    public RdpFolderNodeViewModel Parent { get; }

    public override string Name => string.IsNullOrWhiteSpace(Host.Name) ? Host.Server : Host.Name;
}
