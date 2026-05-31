using System.Linq;
using System.Management.Automation;
using System.Text.Json;
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
        Assert.Equal(UpdateScope.Applicable, options.Scope);
    }

    [Fact]
    public void ParseScan_defaults_IsUninstallable_to_true_when_property_absent()
    {
        // Older rows without the IsUninstallable field default to true so the checklist's
        // checkboxes stay enabled (Applicable-scope assumption).
        var rows = new List<PSObject> { ScanRow("Update A", "5037782", downloaded: true, sizeMb: 100) };

        IReadOnlyList<SoftwareUpdate> updates = WuaUpdateLane.ParseScan(rows);

        Assert.True(updates[0].IsUninstallable);
    }

    [Fact]
    public void ParseScan_reads_IsUninstallable_when_emitted_by_the_scan()
    {
        var row = ScanRow("Servicing Stack Update", "5036893", downloaded: true, sizeMb: 30);
        row.Properties.Add(new PSNoteProperty("IsUninstallable", false));
        var rows = new List<PSObject> { row };

        IReadOnlyList<SoftwareUpdate> updates = WuaUpdateLane.ParseScan(rows);

        Assert.False(updates[0].IsUninstallable);
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

    // --- agent config JSON (the settings the on-target Vivre.UpdateAgent.exe reads) ---

    [Fact]
    public void BuildAgentConfigJson_maps_options_to_agent_keys()
    {
        var options = new PatchOptions
        {
            Source = UpdateSource.MicrosoftUpdate,
            IncludeDrivers = true,
            ExcludeNameContains = ["SQL", " Silverlight "],
            IncludeKbArticleIds = ["5037782", " 5040442 "],
            RebootBehavior = RebootBehavior.RebootAndWait,
        };

        using JsonDocument doc = JsonDocument.Parse(
            WuaUpdateLane.BuildAgentConfigJson(options, @"C:\Windows\Temp\p.json", "Install"));
        JsonElement root = doc.RootElement;

        Assert.Equal("Install", root.GetProperty("Mode").GetString());
        Assert.Equal(3, root.GetProperty("ServerSelection").GetInt32());
        Assert.Equal(WuaServerSelection.MicrosoftUpdateServiceId, root.GetProperty("ServiceId").GetString());
        Assert.True(root.GetProperty("IncludeDrivers").GetBoolean());
        Assert.True(root.GetProperty("RebootAfter").GetBoolean());
        Assert.Equal(@"C:\Windows\Temp\p.json", root.GetProperty("ProgressPath").GetString());
        Assert.Equal(["SQL", "Silverlight"], [.. root.GetProperty("Excludes").EnumerateArray().Select(e => e.GetString()!)]);
        Assert.Equal(["5037782", "5040442"], [.. root.GetProperty("IncludeKbs").EnumerateArray().Select(e => e.GetString()!)]);
    }

    [Fact]
    public void BuildAgentConfigJson_emits_empty_arrays_and_null_serviceid_for_windows_update()
    {
        var options = new PatchOptions { Source = UpdateSource.WindowsUpdate, IncludeKbArticleIds = null };

        using JsonDocument doc = JsonDocument.Parse(
            WuaUpdateLane.BuildAgentConfigJson(options, "p", "Uninstall"));
        JsonElement root = doc.RootElement;

        Assert.Equal("Uninstall", root.GetProperty("Mode").GetString());
        Assert.Equal(2, root.GetProperty("ServerSelection").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("ServiceId").ValueKind);
        Assert.Empty(root.GetProperty("IncludeKbs").EnumerateArray());
        Assert.Empty(root.GetProperty("Excludes").EnumerateArray());
        Assert.False(root.GetProperty("RebootAfter").GetBoolean());
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

        public Task<PSExecutionResult> RunRemoteStreamingAsync(
            string host,
            string script,
            Action<PSObject> onOutput,
            PSCredential? credential = null,
            int port = 5985,
            bool useSsl = false,
            CancellationToken cancellationToken = default)
        {
            // Replay the fake result as a synthetic stream so install/uninstall tests can
            // exercise the streaming-controller path the same way they used to for polling.
            LastScript = script;
            foreach (PSObject row in _result.Output)
            {
                onOutput(row);
            }
            return Task.FromResult(_result);
        }
    }
}
