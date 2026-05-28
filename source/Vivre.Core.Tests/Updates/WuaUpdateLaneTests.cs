using System.Management.Automation;
using Vivre.Core.PowerShell;
using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

public class WuaUpdateLaneTests
{
    // --- source → WUA ServerSelection mapping ---

    [Theory]
    [InlineData(UpdateSource.WindowsUpdate, 2, null)]
    [InlineData(UpdateSource.Managed, 1, null)]
    public void Source_maps_to_server_selection(UpdateSource source, int expected, string? expectedServiceId)
    {
        WuaServerSelection sel = WuaServerSelection.For(source);

        Assert.Equal(expected, sel.ServerSelection);
        Assert.Equal(expectedServiceId, sel.ServiceId);
    }

    [Fact]
    public void Microsoft_update_uses_server_selection_3_and_the_mu_service_id()
    {
        WuaServerSelection sel = WuaServerSelection.For(UpdateSource.MicrosoftUpdate);

        Assert.Equal(3, sel.ServerSelection);
        Assert.Equal(WuaServerSelection.MicrosoftUpdateServiceId, sel.ServiceId);
    }

    // --- exclude filter ---

    [Fact]
    public void ApplyExclude_drops_titles_containing_any_term_case_insensitively()
    {
        IReadOnlyList<SoftwareUpdate> updates =
        [
            Update("2024-05 Cumulative Update for Windows Server"),
            Update("Security Update for SQL Server 2019"),
            Update("Microsoft Silverlight"),
        ];

        IReadOnlyList<SoftwareUpdate> kept = WuaUpdateLane.ApplyExclude(updates, ["sql", "Silverlight"]);

        Assert.Single(kept);
        Assert.StartsWith("2024-05 Cumulative", kept[0].Title);
    }

    [Fact]
    public void ApplyExclude_with_no_terms_returns_everything()
    {
        IReadOnlyList<SoftwareUpdate> updates = [Update("A"), Update("B")];

        Assert.Equal(2, WuaUpdateLane.ApplyExclude(updates, []).Count);
        Assert.Equal(2, WuaUpdateLane.ApplyExclude(updates, ["   "]).Count);
    }

    // --- scan output parsing ---

    [Fact]
    public void ParseScan_reads_typed_updates_and_skips_blank_titles()
    {
        var rows = new List<PSObject>
        {
            ScanRow("Update A", "5037782", downloaded: true, sizeMb: 512.5),
            ScanRow("", null, false, 0),
        };

        IReadOnlyList<SoftwareUpdate> updates = WuaUpdateLane.ParseScan(rows);

        Assert.Single(updates);
        Assert.Equal("Update A", updates[0].Title);
        Assert.Equal("5037782", updates[0].ArticleId);
        Assert.True(updates[0].IsDownloaded);
        Assert.Equal(512.5, updates[0].SizeMb);
    }

    // --- progress JSON parsing ---

    [Fact]
    public void TryParseProgress_parses_an_installing_snapshot()
    {
        const string json = """
            {"phase":"Installing","message":"Installing 3 of 8","percent":42,"available":8,"installed":3,"failed":0,"rebootPending":false}
            """;

        Assert.True(WuaUpdateLane.TryParseProgress(json, out HostPatchStatus status));
        Assert.Equal(PatchPhase.Installing, status.Phase);
        Assert.Equal(42, status.Percent);
        Assert.Equal(8, status.AvailableCount);
        Assert.Equal(3, status.InstalledCount);
        Assert.Equal("Installing 3 of 8", status.Message);
    }

    [Fact]
    public void TryParseProgress_maps_pendingreboot_and_reboot_flag()
    {
        const string json = """{"phase":"PendingReboot","message":"Installed 5, reboot required","installed":5,"rebootPending":true}""";

        Assert.True(WuaUpdateLane.TryParseProgress(json, out HostPatchStatus status));
        Assert.Equal(PatchPhase.PendingReboot, status.Phase);
        Assert.True(status.RebootPending);
    }

    [Theory]
    [InlineData("")]
    [InlineData("__VIVRE_TASK_GONE__")]
    [InlineData("not json")]
    public void TryParseProgress_rejects_non_json(string raw)
    {
        Assert.False(WuaUpdateLane.TryParseProgress(raw, out _));
    }

    // --- scan over the (fake) host ---

    [Fact]
    public async Task ScanAsync_returns_available_count_after_exclude()
    {
        var result = new PSExecutionResult(
            [ScanRow("Cumulative Update", "5037782", false, 100), ScanRow("SQL Server patch", "1234567", false, 50)],
            [], [], HadErrors: false);
        var service = new PatchService(new FakeHost(result));
        var options = new PatchOptions { ExcludeNameContains = ["SQL"] };

        HostPatchStatus status = await service.ScanAsync("NYC-SRV1", options, credential: null);

        Assert.Equal(PatchPhase.Available, status.Phase);
        Assert.Equal(1, status.AvailableCount);
        Assert.Single(status.Updates);
        Assert.Equal("Cumulative Update", status.Updates[0].Title);
    }

    [Fact]
    public async Task ScanAsync_surfaces_an_error_when_the_host_fails()
    {
        var result = new PSExecutionResult([], ["Search failed: 0x80240438"], [], HadErrors: true);
        var service = new PatchService(new FakeHost(result));

        HostPatchStatus status = await service.ScanAsync("NYC-SRV1", new PatchOptions(), credential: null);

        Assert.Equal(PatchPhase.Error, status.Phase);
        Assert.Contains("0x80240438", status.Message);
    }

    // --- PatchOptions defaults ---

    [Fact]
    public void PatchOptions_defaults_are_safe()
    {
        var options = new PatchOptions();

        Assert.Equal(UpdateSource.WindowsUpdate, options.Source);
        Assert.Equal(RunBehavior.InstallNow, options.RunBehavior);
        Assert.Equal(RebootBehavior.ReportOnly, options.RebootBehavior);
        Assert.Empty(options.ExcludeNameContains);
        Assert.True(options.MaxConcurrentHosts >= 1);
        Assert.True(options.PerHostTimeout > TimeSpan.Zero);
        Assert.Null(options.IncludeKbArticleIds);
        Assert.False(options.IncludeDrivers);
    }

    [Fact]
    public void PatchOptions_clone_copies_fields_and_is_independent()
    {
        var original = new PatchOptions
        {
            Source = UpdateSource.MicrosoftUpdate,
            ExcludeNameContains = ["SQL"],
            MaxConcurrentHosts = 9,
        };

        PatchOptions clone = original.Clone();
        clone.IncludeKbArticleIds = ["5037782"];

        Assert.Equal(UpdateSource.MicrosoftUpdate, clone.Source);
        Assert.Equal(9, clone.MaxConcurrentHosts);
        Assert.Equal(new[] { "SQL" }, clone.ExcludeNameContains);

        // A per-host include list set on the clone must not leak back to the shared original.
        Assert.Null(original.IncludeKbArticleIds);
    }

    // --- include-KB PowerShell array (per-machine checklist) ---

    [Fact]
    public void BuildIncludeKbPsArray_is_empty_for_null_or_blank()
    {
        Assert.Equal("@()", WuaUpdateLane.BuildIncludeKbPsArray(null));
        Assert.Equal("@()", WuaUpdateLane.BuildIncludeKbPsArray([]));
        Assert.Equal("@()", WuaUpdateLane.BuildIncludeKbPsArray(["   "]));
    }

    [Fact]
    public void BuildIncludeKbPsArray_quotes_trims_and_escapes_each_kb()
    {
        Assert.Equal("@('5037782', '5040442')", WuaUpdateLane.BuildIncludeKbPsArray(["5037782", " 5040442 "]));
        Assert.Equal("@('a''b')", WuaUpdateLane.BuildIncludeKbPsArray(["a'b"]));
    }

    // --- helpers ---

    private static SoftwareUpdate Update(string title) => new(title, null, false, 0);

    private static PSObject ScanRow(string title, string? kb, bool downloaded, double sizeMb)
    {
        var o = new PSObject();
        o.Properties.Add(new PSNoteProperty("Title", title));
        o.Properties.Add(new PSNoteProperty("KB", kb));
        o.Properties.Add(new PSNoteProperty("IsDownloaded", downloaded));
        o.Properties.Add(new PSNoteProperty("SizeMb", sizeMb));
        return o;
    }

    private sealed class FakeHost : IPowerShellHost
    {
        private readonly PSExecutionResult _result;

        public FakeHost(PSExecutionResult result) => _result = result;

        public string LastScript { get; private set; } = string.Empty;

        public Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken cancellationToken = default)
        {
            LastScript = script;
            return Task.FromResult(_result);
        }

        public Task<PSExecutionResult> RunRemoteAsync(
            string host,
            string script,
            PSCredential? credential = null,
            int port = 5985,
            bool useSsl = false,
            CancellationToken cancellationToken = default)
        {
            LastScript = script;
            return Task.FromResult(_result);
        }
    }
}
