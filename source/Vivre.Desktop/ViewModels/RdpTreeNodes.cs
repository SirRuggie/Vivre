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
        Folder.Folders.Add(node.Folder);
        // Keep folders above hosts in the tree.
        int insertAt = Children.TakeWhile(c => c is RdpFolderNodeViewModel).Count();
        Children.Insert(insertAt, node);
    }

    public void AddHostNode(RdpHostNodeViewModel node)
    {
        Folder.Hosts.Add(node.Host);
        Children.Add(node);
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
