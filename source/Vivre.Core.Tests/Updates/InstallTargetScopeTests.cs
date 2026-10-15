using Vivre.Core.Models;
using Vivre.Core.Updates;

namespace Vivre.Core.Tests.Updates;

public class InstallTargetScopeTests
{
    [Fact]
    public void Captured_rows_remain_the_scope_when_the_source_selection_changes()
    {
        var approved = new Computer("approved");
        var later = new Computer("later");
        var selection = new List<Computer> { approved };
        InstallTargetScope scope = InstallTargetScope.Capture(selection);

        selection.Clear();
        selection.Add(later);

        Assert.Equal(new[] { approved }, scope.Targets);
        Assert.True(scope.IsUnchanged());
    }

    [Fact]
    public void Renamed_row_invalidates_the_captured_scope()
    {
        var row = new Computer("before");
        InstallTargetScope scope = InstallTargetScope.Capture([row]);

        row.Name = "after";

        Assert.False(scope.IsUnchanged());
    }
}
