using Vivre.Core.Computers;
using Xunit;

namespace Vivre.Core.Tests.Computers;

public class ComputerListFoldersTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vivre-list-folders-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Existing_lists_stay_at_top_level_until_moved_and_moves_survive_restart()
    {
        var lists = new ComputerListStore(_directory);
        const string name = "October, 10/20/2026 | 09:00 AM";
        lists.Save(name, ["DUMMY-A", "DUMMY-B"]);
        lists.Save("Other", ["DUMMY-C"]);
        string revision = lists.GetRevision(name)!;
        var folders = new ComputerListFolders(lists);
        Assert.Null(folders.Snapshot(lists.List()).FolderFor(name));

        folders.Create("October Updates");
        folders.Move(name, "october updates");
        var reopened = new ComputerListFolders(new ComputerListStore(_directory));
        Assert.Equal("October Updates", reopened.Snapshot(lists.List()).FolderFor(name));
        Assert.Null(reopened.Snapshot(lists.List()).FolderFor("Other"));
        Assert.Equal(revision, lists.GetRevision(name));
        Assert.Equal(["DUMMY-A", "DUMMY-B"], lists.LoadRequired(name));

        reopened.Move(name, null);
        Assert.Null(folders.Snapshot(lists.List()).FolderFor(name));
        Assert.Equal(revision, lists.GetRevision(name));
    }

    [Fact]
    public void Rename_and_remove_folder_keep_list_files_and_return_entries_to_top_level()
    {
        var lists = new ComputerListStore(_directory);
        lists.Save("A", ["DUMMY-A"]);
        var first = new ComputerListFolders(lists);
        var second = new ComputerListFolders(lists);
        first.Create("October");
        second.Create("November"); // second instance merges the current file instead of replacing it
        first.Move("A", "October");
        second.Rename("October", "Monthly Updates");
        Assert.Equal(["Monthly Updates", "November"], first.Snapshot(lists.List()).Folders);
        Assert.Equal("Monthly Updates", first.Snapshot(lists.List()).FolderFor("A"));

        second.Remove("Monthly Updates");
        Assert.Null(first.Snapshot(lists.List()).FolderFor("A"));
        Assert.Equal(["DUMMY-A"], lists.LoadRequired("A"));
        Assert.Contains("November", first.Snapshot(lists.List()).Folders);
    }

    [Fact]
    public void Invalid_or_stale_operations_do_not_change_organization()
    {
        var lists = new ComputerListStore(_directory);
        lists.Save("A", ["DUMMY-A"]);
        var folders = new ComputerListFolders(lists);
        folders.Create("October");
        Assert.Throws<IOException>(() => folders.Create("october"));
        Assert.Throws<ArgumentException>(() => folders.Create("Bad\nName"));
        Assert.Throws<ArgumentException>(() => folders.Create(new string('X', 81)));
        Assert.Throws<IOException>(() => folders.Move("A", "Missing"));
        Assert.Throws<IOException>(() => folders.Move("Missing", "October"));
        Assert.Throws<IOException>(() => folders.Rename("Missing", "New"));
        Assert.Throws<IOException>(() => folders.Remove("Missing"));
        Assert.Equal(["October"], folders.Snapshot(lists.List()).Folders);
        Assert.Null(folders.Snapshot(lists.List()).FolderFor("A"));
    }

    [Fact]
    public void Delete_and_restore_keep_recovery_and_never_hide_unassigned_lists()
    {
        var lists = new ComputerListStore(_directory);
        lists.Save("A", ["DUMMY-A"]);
        var folders = new ComputerListFolders(lists);
        folders.Create("October");
        folders.Move("A", "October");
        lists.Delete("A", lists.GetRevision("A"));
        Assert.Empty(folders.Snapshot(lists.List()).Assignments);
        var deleted = Assert.Single(lists.DeletedLists());
        lists.Restore(deleted.Id, "Restored A");
        Assert.Null(folders.Snapshot(lists.List()).FolderFor("Restored A"));
        Assert.Equal(["DUMMY-A"], lists.LoadRequired("Restored A"));
    }

    [Fact]
    public void Damaged_folder_metadata_fails_open_without_touching_saved_lists()
    {
        var lists = new ComputerListStore(_directory);
        lists.Save("A", ["DUMMY-A"]);
        File.WriteAllText(Path.Combine(_directory, "list-folders.json"), "{broken");
        var folders = new ComputerListFolders(lists);
        Assert.ThrowsAny<Exception>(() => folders.Snapshot(lists.List()));
        Assert.Equal(["DUMMY-A"], lists.LoadRequired("A"));
        Assert.Equal(["A"], lists.List());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
