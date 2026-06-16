using Vivre.Core.Models;
using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Models;

/// <summary>
/// <see cref="Computer.SelectAllApplicableUpdates"/> — the reboot-and-verify post-reboot rescan calls this
/// so a surfaced "still applicable" update is one-click-installable even though the scan path's
/// preserve-prior selection would otherwise inherit a just-completed install's untick.
/// </summary>
public class ComputerSelectUpdatesTests
{
    private static SelectableUpdate Update(string title, string kb, bool isSelected) =>
        new(new SoftwareUpdate(title, kb, IsDownloaded: false, MinDownloadSizeBytes: 0, MaxDownloadSizeBytes: 0), isSelected);

    [Fact]
    public void Ticks_a_previously_unticked_applicable_update_and_keeps_ticked_ones()
    {
        // Simulates the rescan's list: a still-applicable update a prior install had UNTICKED, plus a
        // genuinely-new post-reboot update that the scan default left ticked.
        var c = new Computer("HOST");
        c.ApplicableUpdates.Add(Update("Re-found, install had unticked it", "5000001", isSelected: false));
        c.ApplicableUpdates.Add(Update("New post-reboot update", "5000002", isSelected: true));

        c.SelectAllApplicableUpdates();

        // Both end up selected → the checked-updates-only Install can now target them.
        Assert.All(c.ApplicableUpdates, u => Assert.True(u.IsSelected));
    }

    [Fact]
    public void Touches_only_ApplicableUpdates_not_InstalledUpdates()
    {
        var c = new Computer("HOST");
        c.ApplicableUpdates.Add(Update("Applicable", "5000001", isSelected: false));
        c.InstalledUpdates.Add(Update("Installed (uninstall scope)", "4000000", isSelected: false));

        c.SelectAllApplicableUpdates();

        Assert.True(c.ApplicableUpdates[0].IsSelected);
        Assert.False(c.InstalledUpdates[0].IsSelected); // uninstall scope is opt-in — never auto-ticked
    }

    [Fact]
    public void Empty_applicable_list_is_a_no_op()
    {
        var c = new Computer("HOST");

        c.SelectAllApplicableUpdates(); // must not throw

        Assert.Empty(c.ApplicableUpdates);
    }
}
