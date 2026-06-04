using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vivre.Core.Logging;
using Vivre.Core.Rdp;

namespace Vivre.Desktop.ViewModels;

/// <summary>
/// The Cross-Domain RDP tab: an embedded remote-desktop manager — a folder tree of hosts on the left and live
/// RDP sessions as inner tabs on the right. A singleton tab (opened from the menu). The host tree + saved
/// credentials persist via <see cref="RdpHostStore"/> / <see cref="RdpCredentialStore"/>; the view owns the
/// dialogs and the embedded controls and calls back into the methods here to mutate + persist the tree.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class CrossDomainRdpViewModel : ObservableObject, ITabViewModel, IDisposable
{
    private readonly RdpHostStore _hostStore;
    private readonly RdpCredentialStore _creds;
    private readonly IActivityLog _activity;
    private readonly RdpHostTree _tree;
    private readonly RdpFolderNodeViewModel _root;

    public CrossDomainRdpViewModel(RdpHostStore hostStore, RdpCredentialStore creds, IActivityLog activity)
    {
        _hostStore = hostStore;
        _creds = creds;
        _activity = activity;

        RdpHostTree tree;
        try
        {
            tree = _hostStore.Load();
        }
        catch (Exception ex)
        {
            _activity.Error(null, $"Couldn't load the Cross-Domain RDP host tree — starting empty. {ex.Message}");
            tree = new RdpHostTree();
        }

        _tree = tree;
        _root = new RdpFolderNodeViewModel(_tree.Root, null);
        // The root folder is an invisible container — its children are shown as the top level, so the user can
        // create multiple top-level folders rather than nesting everything under one node.
    }

    public string Title => "Cross-Domain RDP";

    public bool CanClose => true;

    /// <summary>Top-level tree items — the (hidden) root folder's children.</summary>
    public ObservableCollection<RdpNodeViewModel> Nodes => _root.Children;

    public ObservableCollection<RdpSessionViewModel> Sessions { get; } = [];

    [ObservableProperty]
    public partial RdpNodeViewModel? SelectedNode { get; set; }

    [ObservableProperty]
    public partial RdpSessionViewModel? SelectedSession { get; set; }

    public bool HasSessions => Sessions.Count > 0;

    /// <summary>Edit applies to any selected node (including the root folder's name).</summary>
    public bool CanEditSelected => SelectedNode is not null;

    public bool CanRemoveSelected => SelectedNode is not null;

    partial void OnSelectedNodeChanged(RdpNodeViewModel? value)
    {
        ConnectCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanEditSelected));
        OnPropertyChanged(nameof(CanRemoveSelected));
    }

    partial void OnSelectedSessionChanged(RdpSessionViewModel? value)
    {
        foreach (RdpSessionViewModel session in Sessions)
        {
            session.IsActive = ReferenceEquals(session, value);
        }

        DisconnectCommand.NotifyCanExecuteChanged();
        FullScreenCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The folder new items are added under: the selected folder, the selected host's folder, or root.</summary>
    public RdpFolderNodeViewModel TargetFolder => SelectedNode switch
    {
        RdpFolderNodeViewModel folder => folder,
        RdpHostNodeViewModel host => host.Parent,
        _ => _root,
    };

    // ---- connect / sessions ----

    private bool CanConnect => SelectedNode is RdpHostNodeViewModel;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private void Connect()
    {
        if (SelectedNode is RdpHostNodeViewModel hostNode)
        {
            ConnectTo(hostNode);
        }
    }

    /// <summary>Resolves the host's credentials (inheriting from ancestor folders) and opens a session tab.</summary>
    public void ConnectTo(RdpHostNodeViewModel hostNode)
    {
        RdpHost host = hostNode.Host;
        if (string.IsNullOrWhiteSpace(host.Server))
        {
            _activity.Warn(null, $"{hostNode.Name} has no server address — edit it first.");
            return;
        }

        RdpConnectionSettings? settings;
        try
        {
            settings = _creds.Resolve(host, RdpTree.AncestorsOf(_tree, host));
        }
        catch (Exception ex)
        {
            _activity.Error(host.Server, $"Couldn't read saved credentials for {hostNode.Name}: {ex.Message}");
            return;
        }

        if (settings is null)
        {
            _activity.Warn(host.Server, $"No saved credentials for {hostNode.Name} (or an ancestor folder). Edit it and set a username + password.");
            return;
        }

        var session = new RdpSessionViewModel(hostNode.Name, settings);
        Sessions.Add(session);
        OnPropertyChanged(nameof(HasSessions));
        SelectedSession = session;
        _activity.Info(host.Server, $"Opening remote desktop to {hostNode.Name} ({host.Server}).");
    }

    private bool HasSelectedSession => SelectedSession is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedSession))]
    private void Disconnect()
    {
        if (SelectedSession is { } session)
        {
            CloseSession(session);
        }
    }

    public void CloseSession(RdpSessionViewModel session)
    {
        int index = Sessions.IndexOf(session);
        bool wasSelected = ReferenceEquals(SelectedSession, session);

        Sessions.Remove(session); // removing unloads the view → RdpSessionView disconnects + disposes the control
        OnPropertyChanged(nameof(HasSessions));

        if (wasSelected)
        {
            SelectedSession = Sessions.Count > 0 ? Sessions[Math.Clamp(index, 0, Sessions.Count - 1)] : null;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedSession))]
    private void FullScreen()
    {
        if (SelectedSession is { } session)
        {
            session.FullScreen = true; // the session view watches this and goes full-screen
        }
    }

    // ---- tree edits (the view calls these after its add/edit dialogs) ----

    public void AddFolder(string name, string? domain, string? userName, string? password)
    {
        var folder = new RdpFolder { Name = name };
        ApplyCredential(id => folder.CredentialId = id, existingId: null, domain, userName, password);

        RdpFolderNodeViewModel target = TargetFolder;
        target.AddFolderNode(new RdpFolderNodeViewModel(folder, target));
        target.IsExpanded = true;
        Save();
    }

    public void AddHost(string name, string server, int port, bool nlaEnabled, string? domain, string? userName, string? password)
    {
        var host = new RdpHost { Name = name, Server = server, Port = port, NlaEnabled = nlaEnabled };
        ApplyCredential(id => host.CredentialId = id, existingId: null, domain, userName, password);

        RdpFolderNodeViewModel target = TargetFolder;
        target.AddHostNode(new RdpHostNodeViewModel(host, target));
        target.IsExpanded = true;
        Save();
    }

    public void UpdateHost(RdpHostNodeViewModel node, string name, string server, int port, bool nlaEnabled, string? domain, string? userName, string? password)
    {
        RdpHost host = node.Host;
        host.Name = name;
        host.Server = server;
        host.Port = port;
        host.NlaEnabled = nlaEnabled;
        ApplyCredential(id => host.CredentialId = id, host.CredentialId, domain, userName, password);
        node.RefreshName();
        Save();
    }

    public void UpdateFolder(RdpFolderNodeViewModel node, string name, string? domain, string? userName, string? password)
    {
        node.Folder.Name = name;
        ApplyCredential(id => node.Folder.CredentialId = id, node.Folder.CredentialId, domain, userName, password);
        node.RefreshName();
        Save();
    }

    /// <summary>The stored credential's non-secret identity (domain/username) for pre-filling the edit dialog;
    /// the password is never returned here. Null if the node has no own credential.</summary>
    public RdpCredential? GetCredential(string? credentialId) => _creds.Get(credentialId);

    /// <summary>Resolves the credential ref after an add/edit: a blank username clears it (inherit from an
    /// ancestor folder); otherwise upsert, keeping the existing password when the box was left blank on edit.</summary>
    private void ApplyCredential(Action<string?> setRef, string? existingId, string? domain, string? userName, string? password)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            setRef(null);
            return;
        }

        string resolvedPassword = !string.IsNullOrEmpty(password)
            ? password!
            : existingId is not null ? _creds.GetPassword(existingId) ?? string.Empty : string.Empty;

        setRef(_creds.Upsert(existingId, domain, userName.Trim(), resolvedPassword));
    }

    public void RemoveNode(RdpNodeViewModel node)
    {
        if (ReferenceEquals(node, _root))
        {
            return;
        }

        RdpFolderNodeViewModel? parent = node switch
        {
            RdpFolderNodeViewModel folder => folder.Parent,
            RdpHostNodeViewModel host => host.Parent,
            _ => null,
        };

        parent?.RemoveChild(node);
        if (ReferenceEquals(SelectedNode, node))
        {
            SelectedNode = ReferenceEquals(parent, _root) ? null : parent;
        }

        Save();
    }

    /// <summary>Whether <paramref name="node"/> can be moved into <paramref name="targetFolder"/> (null = top
    /// level): not already there, and a folder can't move into itself or one of its own descendants.</summary>
    public bool CanMove(RdpNodeViewModel node, RdpFolderNodeViewModel? targetFolder)
    {
        RdpFolderNodeViewModel destination = targetFolder ?? _root;
        RdpFolderNodeViewModel? source = ParentOf(node);
        if (source is null || ReferenceEquals(source, destination))
        {
            return false;
        }

        return node is not RdpFolderNodeViewModel folder
            || (!ReferenceEquals(folder, destination) && !IsDescendantOf(destination, folder));
    }

    /// <summary>Moves a host or folder into <paramref name="targetFolder"/> (null = top level), updating the
    /// model + tree and persisting. The view confirms with the user first.</summary>
    public void MoveNode(RdpNodeViewModel node, RdpFolderNodeViewModel? targetFolder)
    {
        if (!CanMove(node, targetFolder))
        {
            return;
        }

        RdpFolderNodeViewModel destination = targetFolder ?? _root;
        RdpFolderNodeViewModel source = ParentOf(node)!;
        source.RemoveChild(node);

        switch (node)
        {
            case RdpFolderNodeViewModel folder:
                destination.AddFolderNode(new RdpFolderNodeViewModel(folder.Folder, destination));
                break;
            case RdpHostNodeViewModel host:
                destination.AddHostNode(new RdpHostNodeViewModel(host.Host, destination));
                break;
        }

        destination.IsExpanded = true;
        Save();
    }

    private static RdpFolderNodeViewModel? ParentOf(RdpNodeViewModel node) => node switch
    {
        RdpFolderNodeViewModel folder => folder.Parent,
        RdpHostNodeViewModel host => host.Parent,
        _ => null,
    };

    private static bool IsDescendantOf(RdpFolderNodeViewModel candidate, RdpFolderNodeViewModel ancestor)
    {
        for (RdpFolderNodeViewModel? node = candidate; node is not null; node = node.Parent)
        {
            if (ReferenceEquals(node, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private void Save()
    {
        try
        {
            _hostStore.Save(_tree);
        }
        catch (Exception ex)
        {
            _activity.Error(null, $"Couldn't save the Cross-Domain RDP host tree: {ex.Message}");
        }
    }

    public void Dispose()
    {
        // The tab is closing — drop the sessions so their views unload and disconnect/dispose the controls.
        foreach (RdpSessionViewModel session in Sessions.ToArray())
        {
            Sessions.Remove(session);
        }
    }
}
