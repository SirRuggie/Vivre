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
    private string? _reportedCredentialRecovery;
    private readonly bool _treeEditsAllowed;
    private bool _credentialSavedBeforeTreeSave;

    public CrossDomainRdpViewModel(RdpHostStore hostStore, RdpCredentialStore creds, IActivityLog activity)
    {
        _hostStore = hostStore;
        _creds = creds;
        _activity = activity;

        RdpHostTree tree;
        try
        {
            tree = _hostStore.Load();
            _treeEditsAllowed = true;
            if (_hostStore.RecoveryMessage is { } recovery)
            {
                _activity.Warn(null, recovery);
            }
        }
        catch (Exception ex)
        {
            _activity.Error(null, $"Couldn't load the Cross-Domain RDP host tree. The view is empty and editing is disabled to protect the saved files; restore a valid primary or backup and reopen Vivre. {ex.Message}");
            tree = new RdpHostTree();
            _treeEditsAllowed = false;
        }

        _tree = tree;
        _root = new RdpFolderNodeViewModel(_tree.Root, null);
        // The root folder is an invisible container — its children are shown as the top level, so the user can
        // create multiple top-level folders rather than nesting everything under one node.
        // M16: keep HasNodes live so the empty-state overlay responds to add/remove.
        Nodes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNodes));
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

    /// <summary>M16: true when the host tree has at least one node (folder or host) at the top level.
    /// Drives the "Add a host to get started" empty state in the RDP pane.</summary>
    public bool HasNodes => Nodes.Count > 0;

    /// <summary>Edit applies to any selected node (including the root folder's name).</summary>
    public bool CanEditSelected => SelectedNode is not null;

    public bool CanRemoveSelected => SelectedNode is not null;

    partial void OnSelectedNodeChanged(RdpNodeViewModel? value)
    {
        OnPropertyChanged(nameof(CanConnect));
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

    // Public so the toolbar Connect button can bind IsEnabled to it directly (the button uses a
    // Click handler so the view can show the no-credentials dialog on the UI thread).
    public bool CanConnect => SelectedNode is RdpHostNodeViewModel;

    /// <summary>
    /// Resolves the host's credentials (inheriting from ancestor folders) and opens a session tab.
    /// Returns <see langword="false"/> when no saved credentials were found so the caller (the view)
    /// can surface a visible message — everything else (bad server, cred read error) is already
    /// written to the activity log and returns <see langword="true"/> to suppress a duplicate dialog.
    /// </summary>
    public bool ConnectTo(RdpHostNodeViewModel hostNode)
    {
        RdpHost host = hostNode.Host;
        if (string.IsNullOrWhiteSpace(host.Server))
        {
            _activity.Warn(null, $"{hostNode.Name} has no server address — edit it first.");
            return true;
        }

        RdpConnectionSettings? settings;
        try
        {
            settings = _creds.Resolve(host, RdpTree.AncestorsOf(_tree, host));
            ReportCredentialRecovery();
        }
        catch (Exception ex)
        {
            _activity.Error(host.Server, $"Couldn't read saved credentials for {hostNode.Name}: {ex.Message}");
            return true;
        }

        if (settings is null)
        {
            string? credentialId = RdpCredentialStore.ResolveCredentialId(host, RdpTree.AncestorsOf(_tree, host));
            if (!string.IsNullOrEmpty(credentialId))
            {
                _activity.Error(host.Server, $"The saved login referenced by '{hostNode.Name}' is unavailable. The host tree and encrypted credential backup may be from different saves; edit this host or its credential folder and re-enter the login.");
                return true;
            }
            _activity.Warn(host.Server, $"No saved credentials for '{hostNode.Name}' — right-click the host and choose Edit… to add a login.");
            return false;
        }

        var session = new RdpSessionViewModel(hostNode.Name, settings, _activity);
        session.CloseRequested += () => CloseSession(session); // e.g. the remote user logs off → close the tab
        Sessions.Add(session);
        OnPropertyChanged(nameof(HasSessions));
        SelectedSession = session;
        _activity.Info(host.Server, $"Opening remote desktop to {hostNode.Name} ({host.Server}).");
        return true;
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
        if (index < 0)
        {
            return; // already removed (e.g. a logoff-close raced with a manual close)
        }

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
        if (!CanEditTree()) return;
        var folder = new RdpFolder { Name = name };
        if (!TryApplyCredential(id => folder.CredentialId = id, existingId: null, domain, userName, password)) return;

        RdpFolderNodeViewModel target = TargetFolder;
        target.AddFolderNode(new RdpFolderNodeViewModel(folder, target));
        target.IsExpanded = true;
        Save();
    }

    public void AddHost(string name, string server, int port, bool nlaEnabled, string? domain, string? userName, string? password)
    {
        if (!CanEditTree()) return;
        var host = new RdpHost { Name = name, Server = server, Port = port, NlaEnabled = nlaEnabled };
        if (!TryApplyCredential(id => host.CredentialId = id, existingId: null, domain, userName, password)) return;

        RdpFolderNodeViewModel target = TargetFolder;
        target.AddHostNode(new RdpHostNodeViewModel(host, target));
        target.IsExpanded = true;
        Save();
    }

    public void UpdateHost(RdpHostNodeViewModel node, string name, string server, int port, bool nlaEnabled, string? domain, string? userName, string? password)
    {
        if (!CanEditTree()) return;
        RdpHost host = node.Host;
        if (!TryApplyCredential(id => host.CredentialId = id, host.CredentialId, domain, userName, password)) return;
        host.Name = name;
        host.Server = server;
        host.Port = port;
        host.NlaEnabled = nlaEnabled;
        node.RefreshName();
        Save();
    }

    public void UpdateFolder(RdpFolderNodeViewModel node, string name, string? domain, string? userName, string? password)
    {
        if (!CanEditTree()) return;
        if (!TryApplyCredential(id => node.Folder.CredentialId = id, node.Folder.CredentialId, domain, userName, password)) return;
        node.Folder.Name = name;
        node.RefreshName();
        Save();
    }

    /// <summary>The stored credential's non-secret identity (domain/username) for pre-filling the edit dialog;
    /// the password is never returned here. Null if the node has no own credential.</summary>
    public RdpCredential? GetCredential(string? credentialId)
    {
        RdpCredential? credential;
        try
        {
            credential = _creds.Get(credentialId);
            ReportCredentialRecovery();
        }
        catch (Exception ex)
        {
            _activity.Error(null, $"Couldn't load the saved RDP login for editing. The encrypted credential files were left unchanged: {ex.Message}");
            return null;
        }
        if (!string.IsNullOrEmpty(credentialId) && credential is null)
        {
            _activity.Error(null, "A saved login referenced by the RDP host tree is unavailable. The host tree and encrypted credential backup may be from different saves; re-enter that login before saving the edited item.");
        }
        return credential;
    }

    /// <summary>Resolves the credential ref after an add/edit: a blank username clears it (inherit from an
    /// ancestor folder); otherwise upsert, keeping the existing password when the box was left blank on edit.</summary>
    private bool TryApplyCredential(Action<string?> setRef, string? existingId, string? domain, string? userName, string? password)
    {
        _credentialSavedBeforeTreeSave = false;
        try
        {
            if (string.IsNullOrWhiteSpace(userName))
            {
                if (existingId is not null && _creds.Get(existingId) is null)
                {
                    setRef(existingId);
                    ReportCredentialRecovery();
                    _activity.Error(null, "The referenced saved login is unavailable, so Vivre preserved its reference instead of clearing it. Re-enter both the username and password to replace the missing login.");
                    return true;
                }
                setRef(null);
                return true;
            }

            string resolvedPassword;
            if (!string.IsNullOrEmpty(password)) resolvedPassword = password;
            else if (existingId is not null)
                resolvedPassword = _creds.GetPassword(existingId)
                    ?? throw new InvalidOperationException("The referenced saved login is unavailable. Re-enter its password before saving; Vivre will not replace it with a blank password.");
            else resolvedPassword = string.Empty;

            setRef(_creds.Upsert(existingId, domain, userName.Trim(), resolvedPassword));
            _credentialSavedBeforeTreeSave = true;
            ReportCredentialRecovery();
            return true;
        }
        catch (Exception ex)
        {
            _activity.Error(null, $"Couldn't save the RDP login, so the connection-tree edit was not applied. Resolve the credential storage error and retry; existing saved login data remains available from the primary or recovery backup when possible. {ex.Message}");
            return false;
        }
    }

    private void ReportCredentialRecovery()
    {
        if (_creds.RecoveryMessage is { } recovery && !string.Equals(recovery, _reportedCredentialRecovery, StringComparison.Ordinal))
        {
            _activity.Warn(null, recovery);
            _reportedCredentialRecovery = recovery;
        }
    }

    public void RemoveNode(RdpNodeViewModel node)
    {
        if (!CanEditTree()) return;
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
        if (!CanEditTree()) return;
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
            _credentialSavedBeforeTreeSave = false;
        }
        catch (Exception ex)
        {
            string credentialState = _credentialSavedBeforeTreeSave
                ? " The encrypted login change was saved first, but the host-tree reference/change was not; retry the edit after resolving the storage error. A newly entered login may remain unused until that retry."
                : string.Empty;
            _activity.Error(null, $"Couldn't save the Cross-Domain RDP host tree. The host-tree change remains visible only in the current tab and was not saved; reopen the tab to return to the saved tree.{credentialState} {ex.Message}");
            _credentialSavedBeforeTreeSave = false;
        }
    }

    private bool CanEditTree()
    {
        if (_treeEditsAllowed) return true;
        _activity.Error(null, "RDP host-tree editing is disabled because the saved tree could not be loaded. Restore a valid primary or backup and reopen Vivre.");
        return false;
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
