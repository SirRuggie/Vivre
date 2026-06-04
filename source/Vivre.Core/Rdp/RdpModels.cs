namespace Vivre.Core.Rdp;

/// <summary>
/// One remote-desktop target in the Cross-Domain RDP tree. Persisted to <c>rdphosts.json</c> — it holds NO secret:
/// the saved password lives in <see cref="RdpCredentialStore"/>, referenced by <see cref="CredentialId"/>.
/// </summary>
public sealed class RdpHost
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Display name in the tree (defaults to the server if left blank).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Hostname or IP to connect to.</summary>
    public string Server { get; set; } = string.Empty;

    public int Port { get; set; } = 3389;

    /// <summary>Network Level Authentication. On by default; turn it OFF for a server on another domain that
    /// rejects delegated/saved credentials (the cross-domain rejection, disconnect reason 0x2107).</summary>
    public bool NlaEnabled { get; set; } = true;

    /// <summary>Credential id for THIS host's saved login, or null to inherit the nearest ancestor folder's.</summary>
    public string? CredentialId { get; set; }
}

/// <summary>A folder in the Cross-Domain RDP tree. Folders nest arbitrarily and hold hosts; a folder's
/// <see cref="CredentialId"/> is inherited by descendant hosts/folders that don't set their own.</summary>
public sealed class RdpFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public List<RdpFolder> Folders { get; set; } = [];

    public List<RdpHost> Hosts { get; set; } = [];

    /// <summary>Credential inherited by descendants that don't set their own, or null for none.</summary>
    public string? CredentialId { get; set; }
}

/// <summary>The persisted document root for the host tree — an invisible container whose children are shown as
/// the user's top-level folders/hosts.</summary>
public sealed class RdpHostTree
{
    public RdpFolder Root { get; set; } = new() { Name = "Root" };
}

/// <summary>
/// The fully-resolved bundle handed to a session right before <c>Connect()</c>. <see cref="Password"/> is
/// plaintext here because the RDP control's <c>ClearTextPassword</c> needs a string — build this at connect
/// time and don't park it on a long-lived field.
/// </summary>
public sealed record RdpConnectionSettings(
    string Server,
    int Port,
    string? Domain,
    string UserName,
    string Password,
    bool NlaEnabled);

/// <summary>Pure tree navigation helpers (no I/O) — separated so the credential-inheritance walk is unit-testable.</summary>
public static class RdpTree
{
    /// <summary>The folders from a host's immediate parent up to the root, NEAREST FIRST (parent, …, root).
    /// Empty if the host isn't in the tree. Used to resolve inherited credentials.</summary>
    public static IReadOnlyList<RdpFolder> AncestorsOf(RdpHostTree tree, RdpHost host)
    {
        var rootToParent = new List<RdpFolder>();
        if (!TryFindPath(tree.Root, host, rootToParent))
        {
            return [];
        }

        rootToParent.Reverse(); // root→parent becomes parent→root (nearest first)
        return rootToParent;
    }

    private static bool TryFindPath(RdpFolder folder, RdpHost host, List<RdpFolder> path)
    {
        path.Add(folder);

        if (folder.Hosts.Any(h => h.Id == host.Id))
        {
            return true; // 'folder' is the host's direct parent
        }

        foreach (RdpFolder child in folder.Folders)
        {
            if (TryFindPath(child, host, path))
            {
                return true;
            }
        }

        path.RemoveAt(path.Count - 1);
        return false;
    }
}
