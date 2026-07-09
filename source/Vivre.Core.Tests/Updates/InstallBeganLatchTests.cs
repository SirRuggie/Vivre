using System;
using System.Collections.Generic;
using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// The producer-side "install has begun" latch: it must set <see cref="InstallBeganLatch.Began"/>
/// synchronously on the reporting thread (BEFORE forwarding), so the retry re-entry guard reading it
/// on a thread-pool continuation can never see a stale false. It latches only on a real beginning
/// signal (Installing/PendingReboot/Done phase, or a non-zero installed count), stays false for
/// pre-install phases, is monotonic, and forwards every report unchanged to the inner sink.
/// </summary>
public class InstallBeganLatchTests
{
    // A recording inner sink — collects every forwarded report so tests can assert forwarding + order.
    private sealed class RecordingProgress(List<HostPatchStatus> sink) : IProgress<HostPatchStatus>
    {
        public void Report(HostPatchStatus value) => sink.Add(value);
    }

    private static InstallBeganLatch NewLatch() => new(new RecordingProgress([]));

    [Theory]
    [InlineData(PatchPhase.Installing, 0)]
    [InlineData(PatchPhase.PendingReboot, 0)]
    [InlineData(PatchPhase.Done, 0)]
    [InlineData(PatchPhase.Error, 3)]   // a non-zero installed count is itself a "began" signal
    public void Began_latches_on_each_beginning_signal(PatchPhase phase, int installed)
    {
        InstallBeganLatch latch = NewLatch();

        latch.Report(new HostPatchStatus(phase, "reported", InstalledCount: installed));

        Assert.True(latch.Began);
    }

    [Fact]
    public void Began_stays_false_for_pre_install_phases()
    {
        InstallBeganLatch latch = NewLatch();

        latch.Report(new HostPatchStatus(PatchPhase.Scanning, "scanning"));
        latch.Report(new HostPatchStatus(PatchPhase.Downloading, "downloading"));
        // A transient reach error BEFORE install began — nothing installed, so the latch must stay closed.
        latch.Report(new HostPatchStatus(PatchPhase.Error, "Exception from HRESULT: 0x80072EE2", InstalledCount: 0));

        Assert.False(latch.Began);
    }

    // Records the thread it was reported on and the latch state it observed DURING its own Report.
    private sealed class ThreadCapturingProbe : IProgress<HostPatchStatus>
    {
        public InstallBeganLatch? Latch { get; set; }
        public int ReportThreadId { get; private set; }
        public bool BeganDuringReport { get; private set; }

        public void Report(HostPatchStatus value)
        {
            ReportThreadId = Environment.CurrentManagedThreadId;
            BeganDuringReport = Latch!.Began;
        }
    }

    [Fact]
    public void Began_is_set_synchronously_on_the_reporting_thread()
    {
        // The latch sets Began BEFORE forwarding, so the inner sink must observe true during its own
        // Report call — mechanical proof the write is synchronous (inline), not posted to another context.
        var probe = new ThreadCapturingProbe();
        var latch = new InstallBeganLatch(probe);
        probe.Latch = latch;
        int testThreadId = Environment.CurrentManagedThreadId;

        latch.Report(new HostPatchStatus(PatchPhase.Installing, "installing"));

        Assert.True(probe.BeganDuringReport);              // set before the forward, on the same call
        Assert.Equal(testThreadId, probe.ReportThreadId);  // reported on the test thread, not a posted one
    }

    [Fact]
    public void Every_report_forwards_to_the_inner_sink_in_order()
    {
        var recorded = new List<HostPatchStatus>();
        var latch = new InstallBeganLatch(new RecordingProgress(recorded));
        var scanning = new HostPatchStatus(PatchPhase.Scanning, "scanning");
        var installing = new HostPatchStatus(PatchPhase.Installing, "installing");
        var transient = new HostPatchStatus(PatchPhase.Error, "Exception from HRESULT: 0x80072EE2");
        var done = new HostPatchStatus(PatchPhase.Done, "done");

        latch.Report(scanning);
        latch.Report(installing);
        latch.Report(transient);
        latch.Report(done);

        Assert.Equal(new[] { scanning, installing, transient, done }, recorded);
    }

    [Fact]
    public void Began_is_monotonic()
    {
        InstallBeganLatch latch = NewLatch();

        latch.Report(new HostPatchStatus(PatchPhase.Installing, "installing"));
        latch.Report(new HostPatchStatus(PatchPhase.Scanning, "scanning"));   // a later pre-install report...

        Assert.True(latch.Began);   // ...must not reset the latch
    }
}
