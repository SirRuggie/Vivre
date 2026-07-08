using System.Management.Automation;
using Vivre.Core.PowerShell;
using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Covers <c>RunWorkerTaskAsync</c> — the install/uninstall streaming controller, the most complex
/// load-bearing path in Core (heartbeat filtering, last-status-wins, the bootstrap-failed case, the
/// liveness watchdog, the typed exception arms, and user-cancel). A <see cref="ScriptedStreamingHost"/>
/// scripts the lines the on-target worker would stream (or throws/hangs), and the watchdog poll
/// interval + agent bytes are injected so the controller runs with no real target and no real agent EXE.
/// </summary>
public class WuaUpdateLaneStreamingTests
{
    private const string Installing =
        """{"phase":"Installing","message":"Installing 1 of 2","percent":50,"available":2,"installed":1,"failed":0,"rebootPending":false}""";
    private const string Heartbeat = """{"phase":"Heartbeat","ts":123}""";
    private const string Done =
        """{"phase":"Done","message":"Installed 2 update(s)","percent":100,"available":2,"installed":2,"failed":0,"rebootPending":false}""";

    private static readonly byte[] StubAgent = [1, 2, 3];

    [Fact]
    public async Task Heartbeat_lines_dont_report_or_regress_the_phase()
    {
        var reports = new List<HostPatchStatus>();
        var lane = new WuaUpdateLane(new ScriptedStreamingHost([Installing, Heartbeat, Done]), agentBytesProvider: () => StubAgent);

        HostPatchStatus final = await lane.InstallAsync("NYC-SRV1", new PatchOptions(), credential: null, Sink(reports), CancellationToken.None);

        // The heartbeat must NOT produce a report (so it can't regress the UI back to "Searching…")
        // — only the initial Scanning, the Installing line, and the terminal Done do. A 4th report
        // would mean the heartbeat leaked through the filter.
        Assert.Equal(3, reports.Count);
        Assert.Equal(PatchPhase.Installing, reports[1].Phase);
        Assert.Equal(PatchPhase.Done, final.Phase);
    }

    [Fact]
    public async Task A_stream_that_ends_without_a_terminal_line_surfaces_an_unknown_outcome_failure()
    {
        // The remote pipeline completed but the last relayed status was still mid-run (the progress
        // log vanished under the watcher, or the agent's final line was dropped) — the lane must
        // never present a mid-run phase as the final result, and the honest failure must never be
        // transient-classified (that would silently re-dispatch the whole install).
        var lane = new WuaUpdateLane(new ScriptedStreamingHost([Installing]), agentBytesProvider: () => StubAgent);

        HostPatchStatus final = await lane.InstallAsync("NYC-SRV1", new PatchOptions(), credential: null, Sink([]), CancellationToken.None);

        Assert.Equal(PatchPhase.Error, final.Phase);
        Assert.Contains("without a final result", final.Message);
        Assert.False(TransientWuaError.IsTransient(final.Message));
    }

    [Fact]
    public async Task A_terminal_done_line_is_returned_as_the_final_status()
    {
        var reports = new List<HostPatchStatus>();
        var lane = new WuaUpdateLane(new ScriptedStreamingHost([Installing, Done]), agentBytesProvider: () => StubAgent);

        HostPatchStatus final = await lane.InstallAsync("NYC-SRV1", new PatchOptions(), credential: null, Sink(reports), CancellationToken.None);

        Assert.Equal(PatchPhase.Done, final.Phase);
        Assert.Equal(2, final.InstalledCount);
    }

    [Fact]
    public async Task No_progress_lines_surfaces_the_captured_error()
    {
        // Bootstrap returned without any progress line (e.g. Register-ScheduledTask threw) — the
        // controller must surface the captured PS error, not a silent zero.
        var result = new PSExecutionResult([], ["Register-ScheduledTask : Access is denied 0x80070005"], [], HadErrors: true);
        var lane = new WuaUpdateLane(new ScriptedStreamingHost([], result), agentBytesProvider: () => StubAgent);

        HostPatchStatus final = await lane.InstallAsync("NYC-SRV1", new PatchOptions(), credential: null, Sink([]), CancellationToken.None);

        Assert.Equal(PatchPhase.Error, final.Phase);
        Assert.Contains("Access is denied", final.Message);
    }

    [Fact]
    public async Task A_lost_session_maps_to_a_host_named_message_and_runs_cleanup()
    {
        var host = new ScriptedStreamingHost([], toThrow: new RemoteSessionLostException("NYC-SRV1", new Exception("dropped")));
        var lane = new WuaUpdateLane(host, agentBytesProvider: () => StubAgent);

        HostPatchStatus final = await lane.InstallAsync("NYC-SRV1", new PatchOptions(), credential: null, Sink([]), CancellationToken.None);

        Assert.Equal(PatchPhase.Error, final.Phase);
        Assert.Contains("Lost connection", final.Message);
        Assert.True(host.CleanupCalls >= 1, "SafetyCleanupAsync should have run on the lost-session path.");
    }

    [Fact]
    public async Task A_shell_init_failure_surfaces_its_actionable_message_and_runs_cleanup()
    {
        var host = new ScriptedStreamingHost([], toThrow: new RemoteShellInitException("NYC-SRV1", new Exception("init")));
        var lane = new WuaUpdateLane(host, agentBytesProvider: () => StubAgent);

        HostPatchStatus final = await lane.InstallAsync("NYC-SRV1", new PatchOptions(), credential: null, Sink([]), CancellationToken.None);

        Assert.Equal(PatchPhase.Error, final.Phase);
        Assert.Contains("temporarily unavailable", final.Message);
        Assert.DoesNotContain("Reboot the target", final.Message); // no needless reboot advice — transient hiccup
        Assert.True(host.CleanupCalls >= 1);
    }

    [Fact]
    public async Task A_user_cancel_reports_cancelled_rethrows_and_runs_cleanup()
    {
        var reports = new List<HostPatchStatus>();
        var host = new ScriptedStreamingHost([]); // honours the token at entry
        var lane = new WuaUpdateLane(host, agentBytesProvider: () => StubAgent);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => lane.InstallAsync("NYC-SRV1", new PatchOptions(), credential: null, Sink(reports), cts.Token));

        Assert.Contains(reports, r => r.Message == "Cancelled");
        Assert.True(host.CleanupCalls >= 1);
    }

    [Fact]
    public async Task Total_silence_trips_the_watchdog_and_reports_no_response()
    {
        // The host stays connected but emits nothing and no heartbeat — the liveness watchdog must
        // cancel the pipeline and surface a "No response" failure (not a user cancel).
        var host = new ScriptedStreamingHost([], hangUntilCancelled: true);
        var options = new PatchOptions { NoResponseTimeout = TimeSpan.FromMilliseconds(50) };
        var lane = new WuaUpdateLane(host, watchdogPollInterval: TimeSpan.FromMilliseconds(10), agentBytesProvider: () => StubAgent);

        HostPatchStatus final = await lane.InstallAsync("NYC-SRV1", options, credential: null, Sink([]), CancellationToken.None);

        Assert.Equal(PatchPhase.Error, final.Phase);
        Assert.Contains("No response", final.Message);
        Assert.True(host.CleanupCalls >= 1);
    }

    private static SyncProgress Sink(List<HostPatchStatus> into) => new(into);

    /// <summary>An <see cref="IProgress{T}"/> that records synchronously, so tests can assert on the
    /// reports the controller emitted without racing the default thread-pool Progress callback.</summary>
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

    /// <summary>Scripts the streaming controller's input: emits the given JSON lines via onOutput,
    /// then optionally hangs until cancelled (watchdog/user-cancel), throws a chosen exception, or
    /// returns the given result. Honours a pre-cancelled token at entry (the user-Stop case) and
    /// counts cleanup calls (the fresh non-streaming call SafetyCleanupAsync makes).</summary>
    private sealed class ScriptedStreamingHost(
        string[] lines,
        PSExecutionResult? result = null,
        Exception? toThrow = null,
        bool hangUntilCancelled = false) : IPowerShellHost
    {
        private readonly PSExecutionResult _result = result ?? new PSExecutionResult([], [], [], HadErrors: false);
        private int _cleanupCalls;

        public int CleanupCalls => _cleanupCalls;

        public Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken cancellationToken = default) =>
            Task.FromResult(_result);

        public Task<PSExecutionResult> RunRemoteAsync(
            string host, string script, PSCredential? credential = null, int port = 5985, bool useSsl = false,
            CancellationToken cancellationToken = default, bool background = false)
        {
            // The only non-streaming remote call the controller makes is SafetyCleanupAsync's fresh call.
            Interlocked.Increment(ref _cleanupCalls);
            return Task.FromResult(new PSExecutionResult([], [], [], HadErrors: false));
        }

        public async Task<PSExecutionResult> RunRemoteStreamingAsync(
            string host, string script, Action<PSObject> onOutput, PSCredential? credential = null, int port = 5985,
            bool useSsl = false, CancellationToken cancellationToken = default, bool background = false)
        {
            cancellationToken.ThrowIfCancellationRequested(); // user Stop before any line

            foreach (string line in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                onOutput(new PSObject(line));
            }

            if (hangUntilCancelled)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            if (toThrow is not null)
            {
                throw toThrow;
            }

            return _result;
        }
    }
}
