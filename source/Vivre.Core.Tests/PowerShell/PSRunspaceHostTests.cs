using System.Management.Automation;
using System.Management.Automation.Remoting;
using Vivre.Core.PowerShell;
using Xunit;

namespace Vivre.Core.Tests.PowerShell;

public class PSRunspaceHostTests
{
    private readonly PSRunspaceHost _host = new();

    [Fact]
    public async Task Runs_script_and_returns_pipeline_output()
    {
        PSExecutionResult result = await _host.RunLocalAsync("1 + 1");

        Assert.False(result.HadErrors);
        Assert.Single(result.Output);
        Assert.Equal(2, (int)result.Output[0].BaseObject);
    }

    [Fact]
    public async Task Get_Process_returns_objects()
    {
        // The plan's Session 4 smoke test (REBUILD_PLAN.md §14).
        PSExecutionResult result = await _host.RunLocalAsync("Get-Process | Select-Object -First 1");

        Assert.NotEmpty(result.Output);
        Assert.False(result.HadErrors);
    }

    [Fact]
    public async Task Captures_error_stream_without_throwing()
    {
        PSExecutionResult result = await _host.RunLocalAsync("Write-Error 'boom'");

        Assert.True(result.HadErrors);
        Assert.Contains(result.Errors, e => e.Contains("boom"));
    }

    [Fact]
    public async Task Captures_warning_stream()
    {
        PSExecutionResult result = await _host.RunLocalAsync("Write-Warning 'heads up'");

        Assert.Contains(result.Warnings, w => w.Contains("heads up"));
    }

    [Fact]
    public async Task Cancellation_stops_a_long_running_script()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _host.RunLocalAsync("Start-Sleep -Seconds 30", cts.Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_script_throws(string? script)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _host.RunLocalAsync(script!));
    }

    // Remote argument validation runs before any network I/O, so these are safe to
    // assert without a reachable target. Live WinRM verification is manual via
    // tools/RemoteRun (REBUILD_PLAN.md §0).
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunRemote_blank_host_throws(string? host)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _host.RunRemoteAsync(host!, "hostname"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunRemote_blank_script_throws(string? script)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _host.RunRemoteAsync("NYC-FP1", script!));
    }

    // --- remoting-failure translation (the DCVCOLUMBUS leak: a server-side session death or a
    //     degraded WinRM shell must NOT surface as a raw SDK string). Pure logic, no network. ---

    [Fact]
    public void Translate_pipeline_stopped_without_cancel_becomes_session_lost()
    {
        // PipelineStoppedException.Message is exactly "The pipeline has been stopped." — the string
        // that leaked into the update-message column.
        Exception translated = PSRunspaceHost.TranslateRemotingException(new PipelineStoppedException(), "DCVCOLUMBUS");

        RemoteSessionLostException lost = Assert.IsType<RemoteSessionLostException>(translated);
        Assert.Equal("DCVCOLUMBUS", lost.Host);
        Assert.Contains("DCVCOLUMBUS", lost.Message);
    }

    [Fact]
    public void Translate_transport_exception_becomes_session_lost()
    {
        Exception translated = PSRunspaceHost.TranslateRemotingException(
            new PSRemotingTransportException("Connection to the remote server failed."), "HOST1");

        Assert.IsType<RemoteSessionLostException>(translated);
    }

    [Fact]
    public void Translate_initial_session_state_becomes_shell_init()
    {
        var raw = new Exception(
            "The type initializer for 'System.Management.Automation.Runspaces.InitialSessionState' threw an exception.");

        Exception translated = PSRunspaceHost.TranslateRemotingException(raw, "DCVCOLUMBUS");

        RemoteShellInitException shell = Assert.IsType<RemoteShellInitException>(translated);
        Assert.Equal("DCVCOLUMBUS", shell.Host);
        Assert.Contains("Reboot the target", shell.Message);
    }

    [Fact]
    public void Translate_initial_session_state_in_inner_exception_becomes_shell_init()
    {
        var raw = new InvalidOperationException("wrapper", new Exception("boom: InitialSessionState ctor failed"));

        Assert.IsType<RemoteShellInitException>(PSRunspaceHost.TranslateRemotingException(raw, "HOST1"));
    }

    [Fact]
    public void Translate_shell_init_wins_over_transport_classification()
    {
        // A transport exception can itself carry the InitialSessionState message — that's the
        // degraded-shell case, not a generic lost connection, so it must map to shell-init.
        var raw = new PSRemotingTransportException(
            "Processing data from remote server failed: type initializer for 'InitialSessionState' threw an exception");

        Assert.IsType<RemoteShellInitException>(PSRunspaceHost.TranslateRemotingException(raw, "HOST1"));
    }

    [Fact]
    public void Translate_leaves_cancellation_untouched()
    {
        var oce = new OperationCanceledException();

        // A user Stop must still map to "Cancelled" upstream — never reclassified as a lost session.
        Assert.Same(oce, PSRunspaceHost.TranslateRemotingException(oce, "HOST1"));
    }

    [Fact]
    public void Translate_leaves_unrelated_exception_untouched()
    {
        // A genuine in-script error must not be mislabeled as a connection loss.
        var other = new InvalidOperationException("genuine script error");

        Assert.Same(other, PSRunspaceHost.TranslateRemotingException(other, "HOST1"));
    }
}
