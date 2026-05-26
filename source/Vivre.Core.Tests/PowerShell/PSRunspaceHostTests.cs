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
}
