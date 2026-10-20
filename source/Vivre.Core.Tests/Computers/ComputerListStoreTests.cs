using Vivre.Core.Computers;
using Xunit;

namespace Vivre.Core.Tests.Computers;

public class ComputerListStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vivre-lists-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void New_store_has_no_lists()
    {
        var store = new ComputerListStore(_dir);

        Assert.Empty(store.List());
    }

    [Fact]
    public void Save_then_Load_round_trips_and_lists_the_name()
    {
        var store = new ComputerListStore(_dir);

        store.Save("ServerList", ["SRV-01", "SRV-02"]);

        Assert.Contains("ServerList", store.List());
        Assert.Equal(["SRV-01", "SRV-02"], store.Load("ServerList"));
        Assert.True(store.Exists("ServerList"));
    }

    [Fact]
    public void Save_trims_blanks_and_deduplicates()
    {
        var store = new ComputerListStore(_dir);

        store.Save("L", ["  A  ", "", "A", "B", "   "]);

        Assert.Equal(["A", "B"], store.Load("L"));
    }

    [Fact]
    public void Delete_removes_the_list()
    {
        var store = new ComputerListStore(_dir);
        store.Save("Temp", ["X"]);

        store.Delete("Temp", store.GetRevision("Temp"));

        Assert.False(store.Exists("Temp"));
        Assert.DoesNotContain("Temp", store.List());
    }

    [Fact]
    public void Load_missing_list_returns_empty()
    {
        var store = new ComputerListStore(_dir);

        Assert.Empty(store.Load("nope"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("List.")]
    [InlineData("CON")]
    [InlineData("nul.backup")]
    [InlineData("COM1")]
    [InlineData("LPT¹")]
    public void Invalid_names_are_rejected_without_sanitizing(string name)
    {
        var store = new ComputerListStore(_dir);
        Assert.Throws<ArgumentException>(() => store.Save(name, ["DUMMY"]));
        Assert.Empty(store.List());
    }

    [Fact]
    public void Display_names_preserve_punctuation_and_do_not_alias_legacy_files()
    {
        const string name = "Tuesday, 10/20/2026 | 09:00 AM ET";
        var store = new ComputerListStore(_dir);
        string[] names = [name, "A/B", "A:B", "A_B", "A%2FB", "../outside", "A%2F/B"];
        foreach (string entry in names) store.Save(entry, [entry == name ? "DUMMY-A" : "DUMMY-B"]);
        var reopened = new ComputerListStore(_dir);
        Assert.Equal(names.OrderBy(x => x), reopened.List().OrderBy(x => x));
        Assert.Equal(["DUMMY-A"], reopened.LoadRequired(name));
        Assert.ThrowsAny<IOException>(() => reopened.Save(name, ["WRONG"]));
        string revision = reopened.GetRevision(name)!;
        reopened.Save(name, ["DUMMY-NEW"], true, revision);
        Assert.Throws<IOException>(() => reopened.Save(name, ["STALE"], true, revision));
        reopened.Delete(name, reopened.GetRevision(name));
        var deleted = Assert.Single(reopened.DeletedLists());
        Assert.Equal(name, deleted.Name);
        reopened.Restore(deleted.Id, name);
        Assert.Equal(["DUMMY-NEW"], reopened.LoadRequired(name));
        Assert.Equal(names.Length, Directory.GetFiles(_dir).Count(p => p.EndsWith(".txt") || p.EndsWith(".vlist")));
    }

    [Fact]
    public void Existing_list_requires_explicit_overwrite_including_case_aliases()
    {
        var store = new ComputerListStore(_dir);
        store.Save("Prod", ["OLD"]);
        Assert.ThrowsAny<IOException>(() => store.Save("prod", ["NEW"]));
        Assert.Equal(["OLD"], store.Load("Prod"));
        store.Save("Prod", ["APPROVED"], overwrite: true, expectedRevision: store.GetRevision("Prod"));
        Assert.Equal(["APPROVED"], store.Load("Prod"));
        Assert.Single(Directory.GetFiles(_dir, "*.txt"));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task Concurrent_new_saves_have_one_winner_without_overwrite()
    {
        var store = new ComputerListStore(_dir);
        Task<bool> Save(string host) => Task.Run(() =>
        {
            try { store.Save("Same", [host]); return true; }
            catch (IOException) { return false; }
        });
        bool[] results = await Task.WhenAll(Save("A"), Save("B"));
        Assert.Single(results, x => x);
        Assert.Contains(store.Load("Same").Single(), new[] { "A", "B" });
        Assert.Single(Directory.GetFiles(_dir, "*.txt"));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Stale_overwrite_approval_preserves_newer_file()
    {
        var store = new ComputerListStore(_dir);
        store.Save("Saved", ["OLD"]);
        string revision = store.GetRevision("Saved")!;
        new ComputerListStore(_dir).Save("Saved", ["OTHER"], true, revision);
        Assert.Throws<IOException>(() => store.Save("Saved", ["STALE"], true, revision));
        Assert.Equal(["OTHER"], store.Load("Saved"));
        Assert.Throws<IOException>(() => store.Delete("Saved", revision));
        Assert.True(store.Exists("Saved"));
    }

    [Fact]
    public void Missing_required_list_throws_and_legacy_names_remain_readable()
    {
        var store = new ComputerListStore(_dir);
        Assert.Throws<FileNotFoundException>(() => store.LoadRequired("Gone"));
        File.WriteAllText(Path.Combine(_dir, "Legacy..txt"), "DUMMY");
        Assert.Contains("Legacy.", store.List());
        Assert.Equal(["DUMMY"], store.LoadRequired("Legacy."));
        store.Delete("Legacy.", store.GetRevision("Legacy."));
        Assert.False(store.Exists("Legacy."));
    }

    [Fact]
    public void Deleted_list_can_be_restored_after_restart_without_overwriting_newer_list()
    {
        var store = new ComputerListStore(_dir);
        store.Save("Saved", ["DUMMY-OLD"]);
        store.Delete("Saved", store.GetRevision("Saved"));
        Assert.False(store.Exists("Saved"));
        var restarted = new ComputerListStore(_dir);
        var item = Assert.Single(restarted.DeletedLists());
        Assert.Equal("Saved", item.Name);
        restarted.Save("Saved", ["DUMMY-NEW"]);
        Assert.Throws<IOException>(() => restarted.Restore(item.Id, "Saved"));
        Assert.Equal(["DUMMY-NEW"], restarted.LoadRequired("Saved"));
        Assert.Single(restarted.DeletedLists());
        restarted.Restore(item.Id, "Recovered");
        Assert.Equal(["DUMMY-OLD"], restarted.LoadRequired("Recovered"));
        Assert.Empty(restarted.DeletedLists());
        Assert.ThrowsAny<IOException>(() => restarted.Restore(item.Id, "Again"));
    }

    [Fact]
    public void Repeated_deletions_keep_distinct_versions_and_reject_invalid_restore_paths()
    {
        var store = new ComputerListStore(_dir);
        store.Save("Saved", ["DUMMY-A"]);
        store.Delete("Saved", store.GetRevision("Saved"));
        store.Save("Saved", ["DUMMY-B"]);
        store.Delete("Saved", store.GetRevision("Saved"));
        Assert.Equal(2, store.DeletedLists().Count);
        Assert.Throws<ArgumentException>(() => store.Restore("../outside", "Restored"));
        Assert.Throws<ArgumentException>(() => store.Restore(store.DeletedLists()[0].Id, "Invalid\nname"));
        Assert.Equal(2, store.DeletedLists().Count);
        int index = 0;
        foreach (var item in store.DeletedLists()) store.Restore(item.Id, "Copy" + ++index);
        Assert.Equal(new[] { "DUMMY-A", "DUMMY-B" }, store.List().SelectMany(store.LoadRequired).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Failed_recovery_creation_preserves_original_list()
    {
        var store = new ComputerListStore(_dir);
        store.Save("Saved", ["DUMMY"]);
        File.WriteAllText(Path.Combine(_dir, "Deleted"), "Block directory creation");
        Assert.ThrowsAny<IOException>(() => store.Delete("Saved", store.GetRevision("Saved")));
        Assert.Equal(["DUMMY"], store.LoadRequired("Saved"));
    }

    [Fact]
    public void Recovery_rejects_linked_archive_folder()
    {
        var store = new ComputerListStore(_dir);
        string external = Path.Combine(_dir, "Other");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "Saved.txt"), "DUMMY-UNCHANGED");
        string id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(_dir, "Deleted"));
        string link = Path.Combine(_dir, "Deleted", id);
        Vivre.Core.Tests.IO.ReparsePointFixture.CreateDirectoryLink(link, external);
        try
        {
            Assert.Empty(store.DeletedLists());
            Assert.Throws<IOException>(() => store.Restore(id, "Restored"));
            Assert.False(store.Exists("Restored"));
            Assert.Equal("DUMMY-UNCHANGED", File.ReadAllText(Path.Combine(external, "Saved.txt")));
        }
        finally { Directory.Delete(link); }
    }

    [Vivre.Core.Tests.IO.FileSymlinkTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Recovery_rejects_linked_payload_without_changing_target(bool archived)
    {
        var store = new ComputerListStore(_dir);
        string target = Path.Combine(_dir, "target.data");
        File.WriteAllText(target, "DUMMY-UNCHANGED");
        string id = Guid.NewGuid().ToString("N");
        string folder = archived ? Path.Combine(_dir, "Deleted", id) : _dir;
        Directory.CreateDirectory(folder);
        File.CreateSymbolicLink(Path.Combine(folder, "Linked.txt"), target);
        if (archived)
        {
            Assert.Empty(store.DeletedLists());
            Assert.Throws<IOException>(() => store.Restore(id, "Restored"));
            Assert.False(store.Exists("Restored"));
        }
        else Assert.Throws<IOException>(() => store.Delete("Linked", store.GetRevision("Linked")));
        Assert.Equal("DUMMY-UNCHANGED", File.ReadAllText(target));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
