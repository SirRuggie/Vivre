using System.Text.Json;
using Vivre.Core.Deploy;
using Xunit;

namespace Vivre.Core.Tests.Deploy;

public sealed class StagingTransferJournalTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "VivreJournalTests_" + Guid.NewGuid().ToString("N"));
    private StagingTransferJournal Journal => new(root);
    private string Target => Path.Combine(root, "packages", "dummy.bin");

    [Fact]
    public void Active_lease_is_excluded_and_released_record_survives_a_new_instance()
    {
        string id;
        using (var active = Journal.Create("DUMMY-SERVER", Target))
        {
            id = active.Record.Id;
            Assert.True(File.Exists(Path.Combine(root, id + ".json")));
            Assert.Empty(Journal.AcquirePending("dummy-server"));
        }
        using var pending = Assert.Single(Journal.AcquirePending("dummy-server"));
        Assert.Equal(id, pending.Record.Id);
        pending.Complete();
        Assert.False(File.Exists(Path.Combine(root, id + ".json")));
    }

    [Fact]
    public void Initialization_acknowledgement_is_durable()
    {
        using (var active = Journal.Create("DUMMY", Target))
        {
            Assert.False(active.Record.InitializationConfirmed);
            active.MarkInitialized();
        }
        using var recovered = Assert.Single(Journal.AcquirePending("DUMMY"));
        Assert.True(recovered.Record.InitializationConfirmed);
        recovered.Complete();
    }

    [Fact]
    public void Transfers_are_distinct_and_other_hosts_are_excluded()
    {
        using (var first = Journal.Create("DUMMY-A", Target))
        using (var second = Journal.Create("DUMMY-B", Target))
        {
            Assert.NotEqual(first.Record.Directory, second.Record.Directory);
            Assert.NotEqual(first.Record.OwnerToken, second.Record.OwnerToken);
        }
        using var pending = Assert.Single(Journal.AcquirePending("DUMMY-A"));
        Assert.Equal("DUMMY-A", pending.Record.Host);
        pending.Complete();
        using var other = Assert.Single(Journal.AcquirePending("DUMMY-B"));
        other.Complete();
    }

    [Fact]
    public void Concurrent_retry_owners_cannot_acquire_the_same_record()
    {
        using (Journal.Create("DUMMY", Target)) { }
        using (var retry = Assert.Single(Journal.AcquirePending("DUMMY")))
            Assert.Empty(Journal.AcquirePending("DUMMY"));
        using var later = Assert.Single(Journal.AcquirePending("DUMMY"));
        later.Complete();
    }

    [Fact]
    public void Tampered_directory_refuses_cleanup_and_retains_record_and_package()
    {
        StagingTransferRecord record;
        using (var active = Journal.Create("DUMMY", Target)) record = active.Record;
        Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
        File.WriteAllText(Target, "usable package");
        string path = Path.Combine(root, record.Id + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(record with { Directory = Target }));
        Assert.Throws<InvalidDataException>(() => Journal.AcquirePending("DUMMY").ToList());
        Assert.True(File.Exists(path));
        Assert.Equal("usable package", File.ReadAllText(Target));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
