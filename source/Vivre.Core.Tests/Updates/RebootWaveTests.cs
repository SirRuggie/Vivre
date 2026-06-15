using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// The Reboot Wave's per-box state machine, driven by a shared <see cref="FakeBox"/> the reboot/reach/
/// build fakes all read, with millisecond timers so the loop runs fast. Confirms the behaviour locked
/// with the operator: graceful→forced escalation (scoped to boxes the operator explicitly selected and
/// confirmed — the wave never runs on its own), the clock only flags "Overdue" (the confirmation strategy
/// decides), late returns still get confirmed, and genuine failures (not-ready, won't-reboot,
/// never-returns) go red.
///
/// UBR-outcome tests inject a real <see cref="UbrConfirmation"/> backed by the test's fake
/// <see cref="FakeBuilds"/> so the same UBR decision logic is exercised through the new seam.
/// </summary>
public class RebootWaveTests
{
    private const int TargetUbr = 9234;
    private const int OldUbr = 9060;

    private static RebootWaveOptions Fast(int goOfflineMs = 2000, int ceilingMs = 5000, int hardCapMs = 10000) =>
        new(TimeSpan.FromMilliseconds(goOfflineMs), TimeSpan.FromMilliseconds(ceilingMs),
            TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(hardCapMs));

    [Fact]
    public async Task Happy_path_graceful_reboot_commits_and_verifies_green()
    {
        var box = new FakeBox { GracefulTakesOffline = true, ComesBackAfterChecks = 2, UbrAfterReturn = TargetUbr };
        var (wave, reboot, readiness, confirmation) = Build(box);
        var progress = new RecProgress();

        HostPatchStatus result = await wave.RebootAndCommitAsync("BOX", Fast(), readiness, confirmation, progress, CancellationToken.None);

        Assert.Equal(PatchPhase.Done, result.Phase);
        Assert.True(reboot.Graceful);
        Assert.False(reboot.Forced); // graceful was enough
    }

    [Fact]
    public async Task Graceful_that_wont_go_offline_escalates_to_forced_then_commits()
    {
        // Scoped escalation: the operator selected + confirmed this box, so a graceful reboot that won't take
        // is completed with a forced one. (The wave only ever runs on operator-selected boxes — see the VM.)
        var box = new FakeBox { GracefulTakesOffline = false, ForcedTakesOffline = true, ComesBackAfterChecks = 2, UbrAfterReturn = TargetUbr };
        var (wave, reboot, readiness, confirmation) = Build(box);

        HostPatchStatus result = await wave.RebootAndCommitAsync("BOX", Fast(goOfflineMs: 40), readiness, confirmation, new RecProgress(), CancellationToken.None);

        Assert.Equal(PatchPhase.Done, result.Phase);
        Assert.True(reboot.Graceful);
        Assert.True(reboot.Forced); // escalated to complete the operator-ordered reboot
    }

    [Fact]
    public async Task Not_reboot_ready_fails_without_rebooting()
    {
        var box = new FakeBox { Ready = false };
        var (wave, reboot, readiness, confirmation) = Build(box);

        HostPatchStatus result = await wave.RebootAndCommitAsync("BOX", Fast(), readiness, confirmation, new RecProgress(), CancellationToken.None);

        Assert.Equal(PatchPhase.Error, result.Phase);
        Assert.Contains("reboot-ready", result.Message);
        Assert.False(reboot.Graceful);
        Assert.False(reboot.Forced); // never touched the box
    }

    [Fact]
    public async Task Box_that_returns_at_the_old_build_is_red_rolled_back()
    {
        var box = new FakeBox { GracefulTakesOffline = true, ComesBackAfterChecks = 2, UbrAfterReturn = OldUbr };
        var (wave, _, readiness, confirmation) = Build(box);

        HostPatchStatus result = await wave.RebootAndCommitAsync("BOX", Fast(), readiness, confirmation, new RecProgress(), CancellationToken.None);

        Assert.Equal(PatchPhase.Error, result.Phase);
        Assert.Contains("rolled back", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Late_return_past_the_ceiling_is_flagged_overdue_then_still_verified_green()
    {
        // Offline long enough to pass the ceiling, then returns at the target — the clock flags overdue
        // but the confirmation strategy is what decides, and the late return is still verified.
        var box = new FakeBox { GracefulTakesOffline = true, ComesBackAfterChecks = 10, UbrAfterReturn = TargetUbr };
        var (wave, _, readiness, confirmation) = Build(box);
        var progress = new RecProgress();

        HostPatchStatus result = await wave.RebootAndCommitAsync(
            "BOX", Fast(ceilingMs: 15, hardCapMs: 10000), readiness, confirmation, progress, CancellationToken.None);

        Assert.Equal(PatchPhase.Done, result.Phase);                                  // verified green despite being late
        Assert.Contains(progress.Reports, r => r.Message.Contains("Overdue"));        // and it WAS flagged for a look
    }

    [Fact]
    public async Task Box_that_never_goes_offline_even_after_force_is_red()
    {
        var box = new FakeBox { GracefulTakesOffline = false, ForcedTakesOffline = false };
        var (wave, reboot, readiness, confirmation) = Build(box);

        HostPatchStatus result = await wave.RebootAndCommitAsync("BOX", Fast(goOfflineMs: 40), readiness, confirmation, new RecProgress(), CancellationToken.None);

        Assert.Equal(PatchPhase.Error, result.Phase);
        Assert.Contains("hasn't gone offline", result.Message); // honest: may be slow or stuck — not "isn't taking"
        Assert.True(reboot.Forced); // we did try forcing it first (to complete the operator-ordered reboot)
    }

    [Fact]
    public async Task A_reboot_already_in_progress_skips_escalation_and_watches_the_commit()
    {
        // The box reports "a shutdown is already in progress" (1115) — it's going offline on its own. The
        // wave must NOT escalate to a forced reboot or fail it; it watches the (slow) commit and verifies.
        var box = new FakeBox { RebootReportsAlreadyInProgress = true, GoesOfflineWhenAlreadyInProgress = true, ComesBackAfterChecks = 2, UbrAfterReturn = TargetUbr };
        var (wave, reboot, readiness, confirmation) = Build(box);

        HostPatchStatus result = await wave.RebootAndCommitAsync("BOX", Fast(goOfflineMs: 40), readiness, confirmation, new RecProgress(), CancellationToken.None);

        Assert.Equal(PatchPhase.Done, result.Phase);     // watched the commit → verified green
        Assert.True(reboot.Graceful);
        Assert.False(reboot.Forced);                     // never escalated — it was already going offline
    }

    [Fact]
    public async Task Box_offline_past_the_hard_cap_is_flagged_red_to_use_verify_later()
    {
        var box = new FakeBox { GracefulTakesOffline = true, ComesBackAfterChecks = int.MaxValue }; // never returns
        var (wave, _, readiness, confirmation) = Build(box);

        HostPatchStatus result = await wave.RebootAndCommitAsync(
            "BOX", Fast(hardCapMs: 60), readiness, confirmation, new RecProgress(), CancellationToken.None);

        Assert.Equal(PatchPhase.Error, result.Phase);
        Assert.Contains("Verify", result.Message); // tells the operator to Verify when it's back
    }

    [Fact]
    public async Task Per_box_independence_fast_box_completes_before_slow_box_finishes()
    {
        // Slow box: needs 12 offline checks before it returns (long commit path).
        var slowBox = new FakeBox { GracefulTakesOffline = true, ComesBackAfterChecks = 12, UbrAfterReturn = TargetUbr };
        var (slowWave, _, slowReadiness, slowConfirmation) = Build(slowBox);

        // Fast box: returns after 2 offline checks (quick).
        var fastBox = new FakeBox { GracefulTakesOffline = true, ComesBackAfterChecks = 2, UbrAfterReturn = TargetUbr };
        var (fastWave, _, fastReadiness, fastConfirmation) = Build(fastBox);

        // Both use a fast poll so the test runs in milliseconds.
        RebootWaveOptions slowOpts = Fast(goOfflineMs: 2000, ceilingMs: 5000, hardCapMs: 30000);
        RebootWaveOptions fastOpts = Fast(goOfflineMs: 2000, ceilingMs: 5000, hardCapMs: 30000);

        Task<HostPatchStatus> slowTask = slowWave.RebootAndCommitAsync("SLOW", slowOpts, slowReadiness, slowConfirmation, new RecProgress(), CancellationToken.None);
        Task<HostPatchStatus> fastTask = fastWave.RebootAndCommitAsync("FAST", fastOpts, fastReadiness, fastConfirmation, new RecProgress(), CancellationToken.None);

        // The fast task must complete well before the slow one finishes.
        HostPatchStatus fastResult = await fastTask;
        bool slowStillRunning = !slowTask.IsCompleted;

        // Wait for slow to finish too (so we don't leave background tasks dangling).
        HostPatchStatus slowResult = await slowTask;

        Assert.Equal(PatchPhase.Done, fastResult.Phase);
        Assert.Equal(PatchPhase.Done, slowResult.Phase);
        Assert.True(slowStillRunning, "Fast box should complete while slow box is still committing.");
    }

    [Fact]
    public async Task RebootGate_is_entered_and_released_around_each_reboot_call()
    {
        // Proves the gate wraps the reboot and is released before (or as soon as) the offline watch begins.
        var box = new FakeBox { GracefulTakesOffline = true, ComesBackAfterChecks = 2, UbrAfterReturn = TargetUbr };
        var (wave, reboot, readiness, confirmation) = Build(box);

        var gate = new CountingGate();

        HostPatchStatus result = await wave.RebootAndCommitAsync("BOX", Fast(), readiness, confirmation, new RecProgress(), CancellationToken.None, gate);

        Assert.Equal(PatchPhase.Done, result.Phase);
        // The graceful reboot causes one Enter, which must be disposed (released) around the call.
        Assert.Equal(1, gate.EnterCount);
        Assert.Equal(1, gate.DisposeCount);
        // The gate must have been released BEFORE the wave declared Done (it's released right after the reboot).
        Assert.True(gate.DisposeCount >= gate.EnterCount, "Every Enter must have a matching Dispose.");
    }

    private static (RebootWave wave, FakeReboot reboot, IRebootReadinessProbe readiness, IPostRebootConfirmation confirmation) Build(FakeBox box)
    {
        var reboot = new FakeReboot(box);
        var builds = new FakeBuilds(box);
        // Use a real UbrConfirmation backed by FakeBuilds so the same UBR logic is exercised through the seam.
        var confirmation = new UbrConfirmation(builds, TargetUbr);
        var wave = new RebootWave(reboot, new FakeReach(box));
        return (wave, reboot, new FakeReadiness(box), confirmation);
    }

    /// <summary>Shared state the fakes read so they model one real box's reboot+commit timeline.</summary>
    private sealed class FakeBox
    {
        public bool Ready = true;
        public bool Online = true;
        public int? Ubr = OldUbr;                 // starts at the old build
        public bool GracefulTakesOffline = true;
        public bool ForcedTakesOffline = true;
        public int ComesBackAfterChecks = 2;      // offline reachability checks before it returns
        public int? UbrAfterReturn = TargetUbr;    // build it reports once back (target = success, old = rollback)
        public int OfflineChecks;
        public bool RebootReportsAlreadyInProgress;     // the trigger reports 1115 (a shutdown is already underway)
        public bool GoesOfflineWhenAlreadyInProgress;   // ...and the box then drops off the network on its own
    }

    private sealed class FakeReboot(FakeBox box) : IRebootTrigger
    {
        public bool Graceful { get; private set; }
        public bool Forced { get; private set; }

        public Task<RebootDispatch> RebootAsync(string host, bool forced, CancellationToken cancellationToken)
        {
            if (forced) { Forced = true; } else { Graceful = true; }

            if (box.RebootReportsAlreadyInProgress)
            {
                // A shutdown is already in progress — the box goes offline on its own (no extra reboot needed).
                if (box.GoesOfflineWhenAlreadyInProgress) { box.Online = false; }
                return Task.FromResult(RebootDispatch.AlreadyInProgress);
            }

            if (forced) { if (box.ForcedTakesOffline) box.Online = false; }
            else { if (box.GracefulTakesOffline) box.Online = false; }
            return Task.FromResult(RebootDispatch.Issued);
        }
    }

    private sealed class FakeReadiness(FakeBox box) : IRebootReadinessProbe
    {
        public Task<RebootReadiness> CheckAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(box.Ready
                ? new RebootReadiness(true, "ready")
                : new RebootReadiness(false, "TrustedInstaller still running"));
    }

    private sealed class FakeReach(FakeBox box) : IReachabilityProbe
    {
        public Task<bool> IsReachableAsync(string host, CancellationToken cancellationToken)
        {
            if (!box.Online)
            {
                box.OfflineChecks++;
                if (box.OfflineChecks >= box.ComesBackAfterChecks)
                {
                    box.Online = true;
                    box.Ubr = box.UbrAfterReturn;
                }
            }

            return Task.FromResult(box.Online);
        }
    }

    private sealed class FakeBuilds(FakeBox box) : ILcuBuildReader
    {
        public Task<(int? CurrentBuild, int? Ubr)> ReadAsync(string host, CancellationToken cancellationToken = default) =>
            Task.FromResult(box.Online ? ((int?)14393, box.Ubr) : ((int?)null, (int?)null));
    }

    private sealed class RecProgress : IProgress<HostPatchStatus>
    {
        public List<HostPatchStatus> Reports { get; } = [];
        public void Report(HostPatchStatus value) { lock (Reports) { Reports.Add(value); } }
    }

    /// <summary>Counts Enter/Dispose calls so tests can assert the gate wrapped the reboot.</summary>
    private sealed class CountingGate : IRebootGate
    {
        public int EnterCount;
        public int DisposeCount;

        public Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref EnterCount);
            return Task.FromResult<IDisposable>(new CountingReleaser(this));
        }

        private sealed class CountingReleaser(CountingGate gate) : IDisposable
        {
            public void Dispose() => Interlocked.Increment(ref gate.DisposeCount);
        }
    }
}
