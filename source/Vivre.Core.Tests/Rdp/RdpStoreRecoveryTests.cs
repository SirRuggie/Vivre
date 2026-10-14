using System.Runtime.Versioning;
using System.Text.Json;
using Vivre.Core.IO;
using Vivre.Core.Rdp;
using Xunit;

namespace Vivre.Core.Tests.Rdp;

[SupportedOSPlatform("windows")]
public sealed class RdpStoreRecoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vivre-rdp-recovery-" + Guid.NewGuid().ToString("N"));

    public RdpStoreRecoveryTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void Host_first_use_saves_primary_and_backup()
    {
        string path = Path.Combine(_dir, "hosts.json");
        var store = new RdpHostStore(path);
        RdpHostTree tree = store.Load();
        tree.Root.Hosts.Add(new RdpHost { Name = "one", Server = "one.example" });

        store.Save(tree);

        Assert.Equal("one", new RdpHostStore(path).Load().Root.Hosts.Single().Name);
        Assert.Equal("one", new RdpHostStore(path + ".bak").Load().Root.Hosts.Single().Name);
    }

    [Fact]
    public void Corrupt_host_primary_recovers_backup_and_preserves_credential_reference()
    {
        string path = Path.Combine(_dir, "hosts.json");
        string credentialId = "credential-from-good-generation";
        WriteHostTree(path + ".bak", "recovered", credentialId);
        File.WriteAllText(path, "{broken");

        var store = new RdpHostStore(path);
        RdpHostTree recovered = store.Load();

        Assert.Contains("host-tree backup", store.RecoveryMessage);
        Assert.Equal(credentialId, recovered.Root.Hosts.Single().CredentialId);
        recovered.Root.Hosts.Single().Name = "repaired";
        store.Save(recovered);
        Assert.Equal(credentialId, new RdpHostStore(path + ".bak").Load().Root.Hosts.Single().CredentialId);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"Root\":null}")]
    [InlineData("{\"Root\":{\"Id\":\"root\",\"Folders\":null,\"Hosts\":[]}}")]
    public void Invalid_host_schema_without_backup_blocks_save(string json)
    {
        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        var store = new RdpHostStore(path);

        Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Throws<InvalidOperationException>(() => store.Save(new RdpHostTree()));
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public void Missing_primary_with_invalid_host_backup_is_not_first_use()
    {
        string path = Path.Combine(_dir, "missing-hosts.json");
        File.WriteAllText(path + ".bak", "{}");
        var store = new RdpHostStore(path);

        Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Throws<InvalidOperationException>(() => store.Save(new RdpHostTree()));
    }

    [Fact]
    public void Host_save_before_load_cannot_replace_corrupt_existing_file()
    {
        string path = Path.Combine(_dir, "save-before-load.json");
        File.WriteAllText(path, "corrupt");
        var store = new RdpHostStore(path);

        Assert.Throws<InvalidDataException>(() => store.Save(new RdpHostTree()));
        Assert.Equal("corrupt", File.ReadAllText(path));
    }

    [Fact]
    public void Failed_host_replacement_keeps_disk_and_cached_snapshot()
    {
        string path = Path.Combine(_dir, "host-write-failure.json");
        WriteHostTree(path, "old", null);
        var store = new RdpHostStore(path, (_, _, _) => throw new IOException("injected replacement failure"));
        RdpHostTree edited = store.Load();
        edited.Root.Hosts.Single().Name = "new";

        Assert.Throws<IOException>(() => store.Save(edited));
        Assert.Equal("old", JsonSerializer.Deserialize<RdpHostTree>(File.ReadAllText(path))!.Root.Hosts.Single().Name);
        Assert.Equal("old", store.Load().Root.Hosts.Single().Name);
    }

    [Fact]
    public void Locked_primary_replacement_failure_preserves_primary_backup_cache_and_cleans_own_temp()
    {
        string path = Path.Combine(_dir, "locked-host.json");
        var initial = new RdpHostStore(path);
        RdpHostTree first = initial.Load();
        first.Root.Hosts.Add(new RdpHost { Name = "old", Server = "old.example" });
        initial.Save(first);
        string primaryBefore = File.ReadAllText(path);
        string backupBefore = File.ReadAllText(path + ".bak");

        var store = new RdpHostStore(path);
        RdpHostTree edited = store.Load();
        edited.Root.Hosts.Single().Name = "new";
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => store.Save(edited));
        }

        Assert.Equal(primaryBefore, File.ReadAllText(path));
        Assert.Equal(backupBefore, File.ReadAllText(path + ".bak"));
        Assert.Equal("old", store.Load().Root.Hosts.Single().Name);
        Assert.DoesNotContain(Directory.GetFiles(_dir), file => file.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_primary_publish_failure_preserves_existing_backup_bytes()
    {
        string path = Path.Combine(_dir, "publish-failure.json");
        byte[] originalBackup = "last-good-generation"u8.ToArray();
        File.WriteAllBytes(path + ".bak", originalBackup);

        Assert.Throws<IOException>(() => RecoverableFileWriter.WriteForTest(
            path,
            "new-generation",
            preserveExistingBackup: true,
            (_, _) => throw new IOException("injected primary publication failure")));

        Assert.False(File.Exists(path));
        Assert.Equal(originalBackup, File.ReadAllBytes(path + ".bak"));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Host_save_rejects_invalid_caller_tree_before_replacement()
    {
        string path = Path.Combine(_dir, "invalid-save.json");
        WriteHostTree(path, "old", null);
        string original = File.ReadAllText(path);
        var store = new RdpHostStore(path);
        _ = store.Load();
        var invalid = new RdpHostTree { Root = null! };

        Assert.Throws<InvalidDataException>(() => store.Save(invalid));
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void Credential_backup_is_encrypted_and_recovers_without_plaintext()
    {
        string path = Path.Combine(_dir, "credentials.json");
        var store = new RdpCredentialStore(path);
        string id = store.Upsert(null, "DOMAIN", "operator", "PlaintextMustNeverAppear");

        Assert.DoesNotContain("PlaintextMustNeverAppear", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.DoesNotContain("PlaintextMustNeverAppear", File.ReadAllText(path + ".bak"), StringComparison.Ordinal);
        File.WriteAllText(path, "null");

        var recovered = new RdpCredentialStore(path);
        Assert.Equal("PlaintextMustNeverAppear", recovered.GetPassword(id));
        Assert.Contains("encrypted", recovered.RecoveryMessage);
    }

    [Fact]
    public void Base64_but_invalid_dpapi_primary_recovers_and_preserves_decryptable_backup()
    {
        string path = Path.Combine(_dir, "dpapi-corrupt.json");
        var initial = new RdpCredentialStore(path);
        string id = initial.Upsert(null, null, "good-user", "good-password");
        string backupBefore = File.ReadAllText(path + ".bak");
        File.WriteAllText(path, JsonSerializer.Serialize(new[]
        {
            new RdpCredential { Id = id, UserName = "corrupt-user", ProtectedPassword = Convert.ToBase64String(new byte[64]) },
        }));

        var recovered = new RdpCredentialStore(path);
        Assert.Equal("good-password", recovered.GetPassword(id));
        recovered.Upsert(id, null, "repaired-user", "new-password");

        Assert.Equal(backupBefore, File.ReadAllText(path + ".bak"));
        Assert.Equal("good-password", new RdpCredentialStore(path + ".bak").GetPassword(id));
    }

    [Fact]
    public void Failed_credential_save_rolls_back_cache_and_disk()
    {
        string path = Path.Combine(_dir, "credential-write-failure.json");
        var initial = new RdpCredentialStore(path);
        string id = initial.Upsert(null, null, "old-user", "old-password");
        var store = new RdpCredentialStore(path, (_, _, _) => throw new IOException("injected replacement failure"));

        Assert.Equal("old-user", store.Get(id)!.UserName);
        Assert.Throws<IOException>(() => store.Upsert(id, null, "new-user", "new-password"));
        Assert.Equal("old-user", store.Get(id)!.UserName);
        Assert.Equal("old-user", new RdpCredentialStore(path).Get(id)!.UserName);
    }

    [Fact]
    public void Credential_get_returns_detached_snapshot()
    {
        string path = Path.Combine(_dir, "credential-alias.json");
        var store = new RdpCredentialStore(path);
        string id = store.Upsert(null, null, "saved-user", "saved-password");

        store.Get(id)!.UserName = "mutated-without-save";

        Assert.Equal("saved-user", store.Get(id)!.UserName);
        Assert.Equal("saved-user", new RdpCredentialStore(path).Get(id)!.UserName);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    public void Invalid_credential_schema_blocks_upsert(string json)
    {
        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + "-credentials.json");
        File.WriteAllText(path, json);
        var store = new RdpCredentialStore(path);

        Assert.Throws<InvalidDataException>(() => store.Upsert(null, null, "user", "password"));
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public void Mismatched_backup_generations_leave_newer_tree_reference_explicitly_missing()
    {
        string hostPath = Path.Combine(_dir, "mismatch-hosts.json");
        string credentialPath = Path.Combine(_dir, "mismatch-credentials.json");
        WriteHostTree(hostPath + ".bak", "host", "newer-credential-id");
        File.WriteAllText(hostPath, "corrupt");
        File.WriteAllText(credentialPath + ".bak", "[]");
        File.WriteAllText(credentialPath, "corrupt");

        RdpHost host = new RdpHostStore(hostPath).Load().Root.Hosts.Single();
        var credentials = new RdpCredentialStore(credentialPath);

        Assert.Null(credentials.Resolve(host, []));
        Assert.Contains("different saves", credentials.RecoveryMessage);
        Assert.Equal("newer-credential-id", host.CredentialId);
    }

    private static void WriteHostTree(string path, string name, string? credentialId)
    {
        var tree = new RdpHostTree();
        tree.Root.Hosts.Add(new RdpHost { Name = name, Server = "server.example", CredentialId = credentialId });
        File.WriteAllText(path, JsonSerializer.Serialize(tree));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
