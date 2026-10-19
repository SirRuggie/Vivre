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
    [InlineData("A/B")]
    [InlineData("A:B")]
    [InlineData("..")]
    [InlineData("List.")]
    [InlineData("CON")]
    [InlineData("nul.backup")]
    [InlineData("COM1")]
    [InlineData("LPT¹")]
    [InlineData("\\server")]
    public void Invalid_names_are_rejected_without_sanitizing(string name)
    {
        var store = new ComputerListStore(_dir);
        Assert.Throws<ArgumentException>(() => store.Save(name, ["DUMMY"]));
        Assert.Empty(store.List());
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

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
