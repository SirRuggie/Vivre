using System.Runtime.Versioning;
using Vivre.Core.Rdp;
using Xunit;

namespace Vivre.Core.Tests.Rdp;

[SupportedOSPlatform("windows")] // DPAPI tests are Windows-only (the whole app is)
public class RdpCredentialTests
{
    // --- DPAPI round-trip (CurrentUser; runs as the test user on Windows) ---

    [Fact]
    public void Dpapi_round_trips_a_password()
    {
        string blob = DpapiSecret.Protect("P@ssw0rd! cross-domain");

        Assert.NotEqual("P@ssw0rd! cross-domain", blob);          // not stored in the clear
        Assert.Equal("P@ssw0rd! cross-domain", DpapiSecret.Unprotect(blob));
    }

    [Fact]
    public void Dpapi_protect_is_non_deterministic_but_both_decrypt()
    {
        // DPAPI salts each call, so two blobs of the same plaintext differ yet both decrypt back.
        string a = DpapiSecret.Protect("same");
        string b = DpapiSecret.Protect("same");

        Assert.NotEqual(a, b);
        Assert.Equal("same", DpapiSecret.Unprotect(a));
        Assert.Equal("same", DpapiSecret.Unprotect(b));
    }

    // --- Credential inheritance (host → nearest ancestor folder) ---

    [Fact]
    public void Host_own_credential_wins_over_ancestors()
    {
        var host = new RdpHost { CredentialId = "own" };
        var ancestors = new[] { new RdpFolder { CredentialId = "folder" } };

        Assert.Equal("own", RdpCredentialStore.ResolveCredentialId(host, ancestors));
    }

    [Fact]
    public void Host_inherits_nearest_ancestor_with_a_credential()
    {
        var host = new RdpHost { CredentialId = null };
        var ancestors = new[]
        {
            new RdpFolder { CredentialId = null },   // nearest (no creds)
            new RdpFolder { CredentialId = "mid" },  // nearest WITH creds — wins
            new RdpFolder { CredentialId = "root" }, // further away
        };

        Assert.Equal("mid", RdpCredentialStore.ResolveCredentialId(host, ancestors));
    }

    [Fact]
    public void No_credential_anywhere_resolves_to_null()
    {
        var host = new RdpHost { CredentialId = null };
        var ancestors = new[] { new RdpFolder { CredentialId = null } };

        Assert.Null(RdpCredentialStore.ResolveCredentialId(host, ancestors));
        Assert.Null(RdpCredentialStore.ResolveCredentialId(host, []));
    }

    // --- Tree ancestor walk (nearest first) ---

    [Fact]
    public void AncestorsOf_returns_parent_up_to_root_nearest_first()
    {
        var hostC = new RdpHost { Name = "C" };
        var sub2 = new RdpFolder { Name = "sub2", Hosts = { hostC } };
        var sub1 = new RdpFolder { Name = "sub1", Folders = { sub2 } };
        var tree = new RdpHostTree { Root = new RdpFolder { Name = "Root", Folders = { sub1 } } };

        var ancestors = RdpTree.AncestorsOf(tree, hostC);

        Assert.Equal(["sub2", "sub1", "Root"], ancestors.Select(f => f.Name));
    }

    [Fact]
    public void AncestorsOf_host_directly_under_root_is_just_root()
    {
        var hostA = new RdpHost { Name = "A" };
        var tree = new RdpHostTree { Root = new RdpFolder { Name = "Root", Hosts = { hostA } } };

        Assert.Equal(["Root"], RdpTree.AncestorsOf(tree, hostA).Select(f => f.Name));
    }

    [Fact]
    public void AncestorsOf_unknown_host_is_empty()
    {
        var tree = new RdpHostTree();

        Assert.Empty(RdpTree.AncestorsOf(tree, new RdpHost { Name = "ghost" }));
    }
}
