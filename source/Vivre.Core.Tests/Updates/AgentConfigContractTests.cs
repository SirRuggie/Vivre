using System.Text.Json;
using Vivre.Core.Updates;
using Vivre.UpdateAgent;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// The agent-config JSON crosses a framework boundary: <see cref="WuaUpdateLane.BuildAgentConfigJson"/>
/// (net10) serializes it with System.Text.Json; the on-target agent (net48) deserializes it with
/// JavaScriptSerializer. The shape tests assert the produced keys; these assert the produced JSON
/// actually <em>binds back</em> onto the <see cref="AgentConfig"/> POCO (linked from the agent), so a
/// property rename or key-casing drift on either side breaks here instead of silently failing install
/// on every target. System.Text.Json is case-sensitive, so a green test means the JSON keys exactly
/// equal the property names — which the agent's case-insensitive JavaScriptSerializer also binds.
/// </summary>
public class AgentConfigContractTests
{
    [Fact]
    public void Install_config_round_trips_onto_the_agent_poco()
    {
        var options = new PatchOptions
        {
            Source = UpdateSource.MicrosoftUpdate,
            IncludeDrivers = true,
            ExcludeNameContains = ["SQL", " Silverlight "],
            IncludeKbArticleIds = ["5037782", " 5040442 "],
        };

        string json = WuaUpdateLane.BuildAgentConfigJson(options, @"C:\Windows\Temp\p.json", "Install");
        AgentConfig cfg = JsonSerializer.Deserialize<AgentConfig>(json)!;

        Assert.Equal("Install", cfg.Mode);
        Assert.Equal(3, cfg.ServerSelection);
        Assert.Equal(WuaServerSelection.MicrosoftUpdateServiceId, cfg.ServiceId);
        Assert.True(cfg.IncludeDrivers);
        Assert.Equal(@"C:\Windows\Temp\p.json", cfg.ProgressPath);
        Assert.Equal(new[] { "SQL", "Silverlight" }, cfg.Excludes);
        Assert.Equal(new[] { "5037782", "5040442" }, cfg.IncludeKbs);
    }

    [Fact]
    public void Windows_update_config_round_trips_with_null_serviceid_and_empty_arrays()
    {
        var options = new PatchOptions { Source = UpdateSource.WindowsUpdate, IncludeKbArticleIds = null };

        string json = WuaUpdateLane.BuildAgentConfigJson(options, "p", "Uninstall");
        AgentConfig cfg = JsonSerializer.Deserialize<AgentConfig>(json)!;

        Assert.Equal("Uninstall", cfg.Mode);
        Assert.Equal(2, cfg.ServerSelection);
        Assert.Null(cfg.ServiceId);
        Assert.Empty(cfg.Excludes);
        Assert.Empty(cfg.IncludeKbs);
    }

    [Fact]
    public void Scan_config_round_trips_with_scope_and_result_path()
    {
        // The SMB lane's Scan mode carries two extra keys (Scope + ResultPath) the WinRM lane omits;
        // assert they bind onto the agent POCO so the agent reads the right scope and result target.
        var options = new PatchOptions { Source = UpdateSource.WindowsUpdate, Scope = UpdateScope.Installed };

        string json = WuaUpdateLane.BuildAgentConfigJson(
            options, @"C:\ProgramData\Vivre\agent\p.json", "Scan", "Installed", @"C:\ProgramData\Vivre\agent\s.json");
        AgentConfig cfg = JsonSerializer.Deserialize<AgentConfig>(json)!;

        Assert.Equal("Scan", cfg.Mode);
        Assert.Equal("Installed", cfg.Scope);
        Assert.Equal(@"C:\ProgramData\Vivre\agent\s.json", cfg.ResultPath);
        Assert.Equal(@"C:\ProgramData\Vivre\agent\p.json", cfg.ProgressPath);
    }

    [Fact]
    public void Non_scan_config_leaves_scope_and_result_path_null()
    {
        // The three-arg callers (the WinRM lane) must keep emitting null Scope/ResultPath so nothing in
        // the install/uninstall path changes shape.
        string json = WuaUpdateLane.BuildAgentConfigJson(new PatchOptions(), "p", "Install");
        AgentConfig cfg = JsonSerializer.Deserialize<AgentConfig>(json)!;

        Assert.Null(cfg.Scope);
        Assert.Null(cfg.ResultPath);
    }
}
