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

        store.Delete("Temp");

        Assert.False(store.Exists("Temp"));
        Assert.DoesNotContain("Temp", store.List());
    }

    [Fact]
    public void Load_missing_list_returns_empty()
    {
        var store = new ComputerListStore(_dir);

        Assert.Empty(store.Load("nope"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
