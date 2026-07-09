using System.Management.Automation;
using Vivre.Core.PowerShell;
using Vivre.Core.Sccm;
using Xunit;

namespace Vivre.Core.Tests.Sccm;

public class ConfigMgrClientTests
{
    [Fact]
    public async Task Parses_a_healthy_client()
    {
        var host = new FakeHost(ResultFrom(HealthObject(version: "5.00.9132.1000", site: "PS1")));
        var client = new ConfigMgrClient(host);

        SccmClientInfo info = await client.GetClientHealthAsync("localhost");

        Assert.Equal("5.00.9132.1000", info.ClientVersion);
        Assert.Equal("PS1", info.SiteCode);
        Assert.True(info.IsHealthy);
        Assert.False(info.RebootRequired);
    }

    [Fact]
    public async Task Parses_an_unhealthy_client()
    {
        var obj = HealthObject(version: "5.00.9088.1000", site: "PS2", reboot: true, missing: true);
        var client = new ConfigMgrClient(new FakeHost(ResultFrom(obj)));

        SccmClientInfo info = await client.GetClientHealthAsync("localhost");

        Assert.True(info.RebootRequired);
        Assert.True(info.MissingUpdates);
        Assert.False(info.RunningUpdates);
        Assert.False(info.IsHealthy);
    }

    [Fact]
    public async Task Throws_when_no_client_data()
    {
        // No output + an error stream = target isn't a ConfigMgr client.
        var empty = new PSExecutionResult([], ["Get-CimInstance: Invalid namespace ROOT\\ccm"], [], HadErrors: true);
        var client = new ConfigMgrClient(new FakeHost(empty));

        SccmQueryException ex = await Assert.ThrowsAsync<SccmQueryException>(
            () => client.GetClientHealthAsync("localhost"));

        Assert.Contains("ROOT\\ccm", ex.Message);
    }

    [Fact]
    public async Task Local_host_runs_locally()
    {
        var host = new FakeHost(ResultFrom(HealthObject()));
        var client = new ConfigMgrClient(host);

        await client.GetClientHealthAsync("localhost");

        Assert.True(host.LocalCalled);
        Assert.False(host.RemoteCalled);
    }

    [Fact]
    public async Task Remote_host_runs_over_winrm()
    {
        var host = new FakeHost(ResultFrom(HealthObject()));
        var client = new ConfigMgrClient(host);

        await client.GetClientHealthAsync("NYC-FP1");

        Assert.True(host.RemoteCalled);
        Assert.False(host.LocalCalled);
    }

    [Fact]
    public async Task TriggerSchedule_returns_completion_message_and_sends_the_guid()
    {
        var action = new ScheduleAction("Machine Policy", "{00000000-0000-0000-0000-000000000021}", "Machine policy requested");
        var message = new PSObject("Machine policy requested");
        var host = new FakeHost(new PSExecutionResult([message], [], [], HadErrors: false));
        var client = new ConfigMgrClient(host);

        string result = await client.TriggerScheduleAsync("localhost", action);

        Assert.Equal("Machine policy requested", result);
        Assert.Contains(action.ScheduleId, host.LastScript);
        Assert.Contains("TriggerSchedule", host.LastScript);
    }

    [Fact]
    public async Task TriggerSchedule_throws_on_error()
    {
        var action = ClientActions.All[0];
        var denied = new PSExecutionResult([], ["TriggerSchedule: Access denied"], [], HadErrors: true);
        var client = new ConfigMgrClient(new FakeHost(denied));

        SccmQueryException ex = await Assert.ThrowsAsync<SccmQueryException>(
            () => client.TriggerScheduleAsync("localhost", action));

        Assert.Contains(action.Label, ex.Message);
        Assert.Contains("Access denied", ex.Message);
    }

    [Fact]
    public async Task A_broken_clientsdk_is_never_healthy()
    {
        // The false-green fix: a corrupt/denied ROOT\ccm\ClientSDK used to yield empty query
        // results -> MissingUpdates=false -> IsHealthy=true. The script now flags the failure,
        // and a flagged result must never read healthy — the update state is unknown, not clean.
        var obj = HealthObject(version: "5.00.9132.1000", site: "PS1", sdkFailed: true);
        var client = new ConfigMgrClient(new FakeHost(ResultFrom(obj)));

        SccmClientInfo info = await client.GetClientHealthAsync("localhost");

        Assert.True(info.ClientSdkFailed);
        Assert.False(info.IsHealthy);
        Assert.Equal("5.00.9132.1000", info.ClientVersion);
        Assert.False(info.MissingUpdates); // fabricated flag parses as-is; the consumer renders it unknown
    }

    [Fact]
    public async Task LastBootTime_round_trips_when_present()
    {
        var when = new DateTime(2026, 7, 1, 8, 30, 0);
        var client = new ConfigMgrClient(new FakeHost(ResultFrom(HealthObject(lastBoot: when))));

        SccmClientInfo info = await client.GetClientHealthAsync("localhost");

        Assert.Equal(when, info.LastBootTime);
    }

    [Fact]
    public async Task LastBootTime_is_null_when_absent()
    {
        // The default fixture omits the property — a missing cosmetic field stays blank, never fabricated.
        var client = new ConfigMgrClient(new FakeHost(ResultFrom(HealthObject())));

        SccmClientInfo info = await client.GetClientHealthAsync("localhost");

        Assert.Null(info.LastBootTime);
    }

    [Fact]
    public async Task UserLoggedOn_is_null_when_the_probe_fails()
    {
        // A failed Win32_Process query emits a present-but-null property — must parse to
        // unknown, never a definite false.
        var client = new ConfigMgrClient(new FakeHost(ResultFrom(HealthObject(user: null))));

        SccmClientInfo info = await client.GetClientHealthAsync("localhost");

        Assert.Null(info.UserLoggedOn);
    }

    [Fact]
    public async Task UserLoggedOn_round_trips_true()
    {
        var client = new ConfigMgrClient(new FakeHost(ResultFrom(HealthObject(user: true))));

        SccmClientInfo info = await client.GetClientHealthAsync("localhost");

        Assert.NotNull(info.UserLoggedOn);
        Assert.True(info.UserLoggedOn.Value);
    }

    [Fact]
    public async Task UserLoggedOn_round_trips_false()
    {
        // The load-bearing boundary — a genuinely user-free box must stay a definite false
        // (a lazy null-everything fix fails here).
        var client = new ConfigMgrClient(new FakeHost(ResultFrom(HealthObject(user: false))));

        SccmClientInfo info = await client.GetClientHealthAsync("localhost");

        Assert.NotNull(info.UserLoggedOn);
        Assert.False(info.UserLoggedOn.Value);
    }

    [Fact]
    public void Health_script_isolates_the_user_probe()
    {
        string script = ConfigMgrClient.HealthScript;

        Assert.Contains("$userLoggedOn = $null", script);
        Assert.Contains("Name = 'explorer.exe'\" -ErrorAction Stop", script);
        // Isolation pin: Stop + the count expression + the wrapping catch in one reformat-tolerant
        // assertion — a dropped catch would let a cimv2 hiccup kill the WHOLE health script.
        Assert.Contains("-ErrorAction Stop).Count -gt 0 } catch { }", script);
        Assert.DoesNotContain("explorer.exe'\" -ErrorAction SilentlyContinue", script); // the old lie
        Assert.DoesNotContain("[bool]$userLoggedOn", script); // cast left on would silently revive the bug
    }

    [Fact]
    public void Health_script_parses_as_valid_powershell()
    {
        // dotnet build can't catch a syntax error in the embedded script — lock parse-validity in.
        System.Management.Automation.Language.Parser.ParseInput(
            ConfigMgrClient.HealthScript, out _, out System.Management.Automation.Language.ParseError[] errors);

        Assert.Empty(errors);
    }

    [Fact]
    public void Health_script_keeps_the_sentinel_shape()
    {
        string script = ConfigMgrClient.HealthScript;

        // The authoritative CCM_SoftwareUpdate query must FAIL LOUDLY (the broken-namespace
        // sentinel); the optional CCM_Application/CCM_Program stay silent (the legacy class can
        // be absent on healthy clients); the reboot method keeps its own isolated catch so its
        // independent failure never blanks update state.
        Assert.Contains("CCM_SoftwareUpdate -ErrorAction Stop", script);
        Assert.Contains("CCM_Application -ErrorAction SilentlyContinue", script);
        Assert.Contains("CCM_Program -ErrorAction SilentlyContinue", script);
        Assert.Contains("ClientSdkFailed = [bool]$clientSdkFailed", script);
        Assert.Contains("DetermineIfRebootPending -ErrorAction Stop", script);

        // Pin the ISOLATION, not just presence: the reboot probe's own bare catch must sit fully
        // BEFORE the update-sentinel block. Folding the reboot method into the CCM_SoftwareUpdate
        // try would make an independent reboot-method failure blank the real update state — under
        // a fold-in, the first bare catch in the script would move past the sentinel declaration
        // and this ordering fails.
        int reboot = script.IndexOf("DetermineIfRebootPending", StringComparison.Ordinal);
        int isolatedCatch = script.IndexOf("} catch { }", StringComparison.Ordinal);
        int sentinelDecl = script.IndexOf("$clientSdkFailed = $false", StringComparison.Ordinal);
        Assert.True(reboot >= 0 && isolatedCatch > reboot && sentinelDecl > isolatedCatch,
            "the reboot probe must keep its own bare catch, fully before the update-sentinel block");
    }

    private static PSExecutionResult ResultFrom(PSObject row) =>
        new([row], [], [], HadErrors: false);

    private static PSObject HealthObject(
        string version = "5.00.9132.1000",
        string? site = "PS1",
        bool reboot = false,
        bool missing = false,
        bool running = false,
        bool? user = false,
        bool sdkFailed = false,
        DateTime? lastBoot = null)
    {
        var o = new PSObject();
        o.Properties.Add(new PSNoteProperty("ClientVersion", version));
        o.Properties.Add(new PSNoteProperty("SiteCode", site));
        o.Properties.Add(new PSNoteProperty("RebootRequired", reboot));
        o.Properties.Add(new PSNoteProperty("MissingUpdates", missing));
        o.Properties.Add(new PSNoteProperty("RunningUpdates", running));
        o.Properties.Add(new PSNoteProperty("UserLoggedOn", user));
        o.Properties.Add(new PSNoteProperty("ClientSdkFailed", sdkFailed));
        if (lastBoot is not null) { o.Properties.Add(new PSNoteProperty("LastBootTime", lastBoot.Value)); }
        return o;
    }

    private sealed class FakeHost : IPowerShellHost
    {
        private readonly PSExecutionResult _result;

        public FakeHost(PSExecutionResult result) => _result = result;

        public bool LocalCalled { get; private set; }

        public bool RemoteCalled { get; private set; }

        public string LastScript { get; private set; } = string.Empty;

        public Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken cancellationToken = default)
        {
            LocalCalled = true;
            LastScript = script;
            return Task.FromResult(_result);
        }

        public Task<PSExecutionResult> RunRemoteAsync(
            string host,
            string script,
            PSCredential? credential = null,
            int port = 5985,
            bool useSsl = false,
            CancellationToken cancellationToken = default,
            bool background = false)
        {
            RemoteCalled = true;
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
            CancellationToken cancellationToken = default,
            bool background = false) =>
            throw new NotSupportedException("ConfigMgr tests don't exercise the streaming path.");
    }
}
