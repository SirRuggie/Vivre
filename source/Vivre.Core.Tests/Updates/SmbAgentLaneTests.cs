using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Host-free coverage of the SMB lane's pure helpers (admin-share UNC mapping, scan-result parsing)
/// and the no-network guards (ScheduleAt isn't supported on this lane; a local host is rejected). The
/// drop / SCM / tail mechanics need a real target and are exercised on the pilot box, not here.
/// </summary>
public class SmbAgentLaneTests
{
    private static readonly Func<byte[]> StubAgent = () => [1, 2, 3];

    // --- ToAdminShareUnc ---------------------------------------------------

    [Theory]
    [InlineData("HOST", @"C:\ProgramData\Vivre\agent\Vivre_WUA_x.exe", @"\\HOST\C$\ProgramData\Vivre\agent\Vivre_WUA_x.exe")]
    [InlineData("box1", @"D:\Tools\a.json", @"\\box1\D$\Tools\a.json")]
    public void ToAdminShareUnc_maps_a_drive_rooted_path_to_the_admin_share(string host, string local, string expected) =>
        Assert.Equal(expected, SmbAgentLane.ToAdminShareUnc(host, local));

    [Fact]
    public void ToAdminShareUnc_rejects_a_non_drive_rooted_path() =>
        Assert.Throws<ArgumentException>(() => SmbAgentLane.ToAdminShareUnc("HOST", @"\\server\share\x"));

    // --- ParseScanResultJson ----------------------------------------------

    [Fact]
    public void ParseScanResultJson_reads_applicable_rows()
    {
        const string json =
            """[{"Title":"2026-01 Cumulative Update","KB":"5000001","IsDownloaded":true,"SizeMb":512.5,"IsUninstallable":true,"InstalledAt":null}]""";

        IReadOnlyList<SoftwareUpdate> updates = SmbAgentLane.ParseScanResultJson(json);

        SoftwareUpdate u = Assert.Single(updates);
        Assert.Equal("2026-01 Cumulative Update", u.Title);
        Assert.Equal("5000001", u.ArticleId);
        Assert.True(u.IsDownloaded);
        Assert.Equal(512.5, u.SizeMb);
        Assert.True(u.IsUninstallable);
        Assert.Null(u.InstalledAt);
    }

    [Fact]
    public void ParseScanResultJson_reads_installed_rows_with_dates()
    {
        const string json =
            """[{"Title":"Security Update KB5000002","KB":"5000002","IsDownloaded":true,"SizeMb":0,"IsUninstallable":false,"InstalledAt":"2026-01-15T08:30:00.0000000Z"}]""";

        SoftwareUpdate u = Assert.Single(SmbAgentLane.ParseScanResultJson(json));

        Assert.False(u.IsUninstallable);
        Assert.NotNull(u.InstalledAt);
        Assert.Equal(2026, u.InstalledAt!.Value.Year);
        Assert.Equal(1, u.InstalledAt.Value.Month);
    }

    [Fact]
    public void ParseScanResultJson_skips_rows_with_no_title()
    {
        const string json =
            """[{"Title":"","KB":"1"},{"Title":"Real Update","KB":"2"}]""";

        SoftwareUpdate u = Assert.Single(SmbAgentLane.ParseScanResultJson(json));
        Assert.Equal("Real Update", u.Title);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("   ")]
    [InlineData("")]
    public void ParseScanResultJson_handles_empty_input(string json) =>
        Assert.Empty(SmbAgentLane.ParseScanResultJson(json));

    // --- no-network guards -------------------------------------------------

    [Fact]
    public async Task Install_with_a_schedule_is_reported_as_not_available_without_a_network_call()
    {
        var lane = new SmbAgentLane(StubAgent);
        var reports = new List<HostPatchStatus>();
        var options = new PatchOptions { RunBehavior = RunBehavior.ScheduleAt, ScheduleAt = DateTime.UtcNow.AddHours(1) };

        HostPatchStatus result = await lane.InstallAsync("REMOTE-BOX", options, new SyncProgress(reports), CancellationToken.None);

        Assert.Equal(PatchPhase.Error, result.Phase);
        Assert.Contains("Scheduling isn't available", result.Message);
        // The message must not reveal the transport (no "SMB" / "Kerberos" leak on an operation result).
        Assert.DoesNotContain("SMB", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Kerberos", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SyncProgress(List<HostPatchStatus> sink) : IProgress<HostPatchStatus>
    {
        public void Report(HostPatchStatus value)
        {
            lock (sink)
            {
                sink.Add(value);
            }
        }
    }
}
