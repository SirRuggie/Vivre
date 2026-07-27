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

    private static RebootWaveOptions Fast(int goOfflineMs = 2000, int ceilingMs = 5000, int hardCapMs = 10000, int settleMs = 300) =>
        new(TimeSpan.FromMilliseconds(goOfflineMs), TimeSpan.FromMilliseconds(ceilingMs),
            TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(hardCapMs))
        {
            // Generous by default so existing tests never accidentally trip the reachable-but-unconfirmed
            // bound; the Unverified/flap tests override it small per-test via a `with` copy.
            PostReturnConfirmWindow = TimeSpan.FromSeconds(5),
            // Pre-reboot servicing/couldn't-confirm settle poll bound. Irrelevant to the ready-on-first-check
            // tests (they never enter the poll); the settle tests pass a small/large value explicitly.
            ServicingSettleWindow = TimeSpan.FromMilliseconds(settleMs),
        };

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

    // ── 1191: the graceful form was refused and the trigger escalated to forced ───
    // Windows refuses a GRACEFUL reboot with 1191 whenever a session exists on the box, so the trigger
    // completes the operator's ordered reboot by re-sending the FORCED form on the same healthy channel and
    // reports EscalatedToForced. The box is ALREADY going down forced: the wave must apply the FORCED
    // go-offline window and must NEVER dispatch again. These four tests are the double-reboot proof.

    [Fact]
    public async Task An_escalated_dispatch_is_never_re_dispatched_exactly_one_send_per_click()
    {
        // THE DOUBLE-REBOOT PROOF. The box is going down forced already, but slowly enough that the graceful
        // window would have expired — the old structure would have fired a SECOND forced reboot at that point.
        // Count the dispatches: exactly one, and it asked for the graceful form (the escalation happened
        // inside the trigger, on one send).
        var box = new FakeBox
        {
            RebootRefusesGracefulAndEscalates = true,
            EscalatedTakesOffline = false, // doesn't drop immediately…
            ChecksBeforeOffline = 6,       // …it holds the network for a while, past the graceful window
            ComesBackAfterChecks = 2,
            UbrAfterReturn = TargetUbr,
        };
        var (wave, reboot, readiness, confirmation) = Build(box);

        RebootWaveOptions opts = Fast(goOfflineMs: 20) with { ForcedGoOfflineWindow = TimeSpan.FromSeconds(5) };
        HostPatchStatus result = await wave.RebootAndCommitAsync("BOX", opts, readiness, confirmation, new RecProgress(), CancellationToken.None);

        Assert.Equal(PatchPhase.Done, result.Phase);
        Assert.Equal(1, reboot.Dispatches);       // ONE send for one operator click — no double reboot
        Assert.Single(reboot.ForcedArgs);
        Assert.False(reboot.ForcedArgs[0]);       // and the wave asked only for the graceful form
        Assert.False(reboot.Forced);              // the wave itself never issued a forced dispatch
    }

    [Fact]
    public async Task An_escalated_dispatch_that_goes_offline_reaches_the_commit_watch_and_verifies_green()
    {
        // The escalated box behaves like any other rebooting box once it drops: normal commit-watch, normal
        // confirmation, green — and the operator is told WHY it was forced (Vivre didn't decide to force it).
        var box = new FakeBox
        {
            RebootRefusesGracefulAndEscalates = true,
            EscalatedTakesOffline = true,
            ComesBackAfterChecks = 3,
            UbrAfterReturn = TargetUbr,
        };
        var (wave, reboot, readiness, confirmation) = Build(box);
        var progress = new RecProgress();

        HostPatchStatus result = await wave.RebootAndCommitAsync("BOX", Fast(), readiness, confirmation, progress, CancellationToken.None);

        Assert.Equal(PatchPhase.Done, result.Phase);
        Assert.Equal(1, reboot.Dispatches);
        Assert.Contains(progress.Reports, r => r.Message.Contains("refused the graceful reboot"));
        Assert.Contains(progress.Reports, r => r.Message.Contains("Committing (offline)"));
    }

    [Fact]
    public async Task An_escalated_dispatch_that_never_goes_offline_is_the_honest_forced_terminal_with_one_send()
    {
        // Window expiry after an escalation returns the SAME honest terminal the post-force failure uses —
        // and still exactly ONE dispatch. It must never "try again" on the way out.
        var box = new FakeBox
        {
            RebootRefusesGracefulAndEscalates = true,
            EscalatedTakesOffline = false, // never drops…
            ForcedTakesOffline = false,    // …and would not drop for a second forced send either
        };
        var (wave, reboot, readiness, confirmation) = Build(box);

        RebootWaveOptions opts = Fast(goOfflineMs: 20) with { ForcedGoOfflineWindow = TimeSpan.FromMilliseconds(60) };
        HostPatchStatus result = await wave.RebootAndCommitAsync("BOX", opts, readiness, confirmation, new RecProgress(), CancellationToken.None);

        Assert.Equal(PatchPhase.Error, result.Phase);
        Assert.Contains("hasn't gone offline after a forced reboot", result.Message);
        Assert.Equal(1, reboot.Dispatches); // no second dispatch on the failure path either
    }

    [Fact]
    public async Task An_escalated_dispatch_waits_the_FORCED_window_not_the_graceful_one()
    {
        // Budget proof: with a 20 ms graceful window and a 600 ms FORCED window, a box that never drops must
        // not be failed until the FORCED window elapses. If the wave used the graceful window it would fail
        // at ~20 ms; the elapsed-time assertion catches that.
        var box = new FakeBox
        {
            RebootRefusesGracefulAndEscalates = true,
            EscalatedTakesOffline = false, // never drops, so only the window decides when it fails
            ForcedTakesOffline = false,
        };
        var (wave, reboot, readiness, confirmation) = Build(box);

        var forcedWindow = TimeSpan.FromMilliseconds(600);
        RebootWaveOptions opts = Fast(goOfflineMs: 20) with { ForcedGoOfflineWindow = forcedWindow };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        HostPatchStatus result = await wave.RebootAndCommitAsync("BOX", opts, readiness, confirmation, new RecProgress(), CancellationToken.None);
        sw.Stop();

        Assert.Equal(PatchPhase.Error, result.Phase);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(500),
            $"escalated box must wait the FORCED window ({forcedWindow.TotalMilliseconds:N0} ms), not the graceful one (20 ms) — failed after {sw.Elapsed.TotalMilliseconds:N0} ms");
        Assert.Equal(1, reboot.Dispatches);
    }

    // ── Pre-reboot readiness settle poll ─────────────────────────────────────────
    // The old pre-reboot hard-fail (a not-ready read → immediate Error) was replaced by the approved design:
    // a not-ready box is WAITED on (never rebooted) and resolves to NothingToCommit (nothing staged) or a
    // needs-action Unverified (never settled). The "never touched the box" cardinal is preserved + strengthened
    // across the tests below (c/d/e/g).

    [Fact]
    public async Task Servicing_active_first_reading_polls_and_does_not_reboot_immediately()
    {
        // (a) A "servicing still running" first read must NOT hard-fail and must NOT reboot on the first read;
        // the wave WAITS (a Rebooting "waiting for servicing" beat) and proceeds only once a fresh Ready arrives.
        var box = new FakeBox
        {
            GracefulTakesOffline = true, ComesBackAfterChecks = 2, UbrAfterReturn = TargetUbr,
            ReadinessScript =
            [
                new RebootReadiness(false, "TrustedInstaller is still Running (online servicing in progress)", RebootReadinessKind.ServicingActive),
                new RebootReadiness(false, "TrustedInstaller is still Running (online servicing in progress)", RebootReadinessKind.ServicingActive),
                new RebootReadiness(true, "ready", RebootReadinessKind.Ready),
            ],
        };
        var (wave, reboot, readiness, confirmation) = Build(box);
        var progress = new RecProgress();

        HostPatchStatus result = await wave.RebootAndCommitAsync("BOX", Fast(), readiness, confirmation, progress, CancellationToken.None);

        Assert.Equal(PatchPhase.Done, result.Phase);                    // eventually proceeded and verified
        Assert.True(box.ReadinessChecks >= 3);                          // it re-checked (polled), not a one-shot fail
        Assert.Contains(progress.Reports, r => r.Phase == PatchPhase.Rebooting && r.Message.Contains("Waiting for servicing"));
        Assert.True(reboot.Graceful);
        Assert.False(reboot.Forced);
    }

    [Fact]
    public async Task Servicing_active_then_ready_proceeds_and_escalation_still_works()
    {
        // (b) After the settle resolves to Ready, the DOWNSTREAM graceful→forced escalation is intact: a
        // graceful reboot that won't take is still completed with a forced one (reuses the escalation shape).
        var box = new FakeBox
        {
            GracefulTakesOffline = false, ForcedTakesOffline = true, ComesBackAfterChecks = 2, UbrAfterReturn = TargetUbr,
            ReadinessScript =
            [
                new RebootReadiness(false, "TiWorker.exe is still running", RebootReadinessKind.ServicingActive),
                new RebootReadiness(true, "ready", RebootReadinessKind.Ready),
            ],
        };
        var (wave, reboot, readiness, confirmation) = Build(box);

        HostPatchStatus result = await wave.RebootAndCommitAsync("BOX", Fast(goOfflineMs: 40), readiness, confirmation, new RecProgress(), CancellationToken.None);

        Assert.Equal(PatchPhase.Done, result.Phase);
        Assert.True(reboot.Graceful);
        Assert.True(reboot.Forced); // settle didn't disturb the escalation path
    }

    [Fact]
    public async Task Servicing_active_that_never_settles_is_unverified_without_rebooting()
    {
        // (c) Replaces the old pre-reboot hard-fail: a box whose servicing never finishes is no longer failed
        // outright — the wave WAITS (never rebooting), and on settle-window expiry returns a needs-action
        // Unverified terminal that points the operator at Force reboot. Cardinal: the box is NEVER touched.
        var box = new FakeBox
        {
            ReadinessScript = [new RebootReadiness(false, "TrustedInstaller is still Running (online servicing in progress)", RebootReadinessKind.ServicingActive)],
        };
        var (wave, reboot, readiness, confirmation) = Build(box);

        HostPatchStatus result = await wave.RebootAndCommitAsync("BOX", Fast(settleMs: 40), readiness, confirmation, new RecProgress(), CancellationToken.None);

        Assert.Equal(PatchPhase.Unverified, result.Phase);
        Assert.Contains("not rebooted", result.Message);
        Assert.Contains("Force reboot", result.Message);
        Assert.False(reboot.Graceful);
        Assert.False(reboot.Forced); // cardinal: never touched the box
    }

    [Fact]
    public async Task Stop_during_the_settle_poll_propagates_cancellation_and_never_reboots()
    {
        // (d) An operator Stop during the settle wait → OperationCanceledException propagates; box never rebooted.
        using var cts = new CancellationTokenSource();
        var box = new FakeBox
        {
            ReadinessScript = [new RebootReadiness(false, "TiWorker.exe is still running", RebootReadinessKind.ServicingActive)],
            CancelSource = cts,
            CancelAfterReadinessChecks = 2, // Stop lands on the 2nd readiness read (mid-poll)
        };
        var (wave, reboot, readiness, confirmation) = Build(box);

        // Large settle window so the CANCEL — not the window — is what ends the poll. ThrowsAny: Task.Delay
        // surfaces the derived TaskCanceledException, which must still satisfy "OperationCanceledException propagates".
        RebootWaveOptions opts = Fast(settleMs: 10000);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            wave.RebootAndCommitAsync("BOX", opts, readiness, confirmation, new RecProgress(), cts.Token));

        Assert.False(reboot.Graceful);
        Assert.False(reboot.Forced);
    }

    [Fact]
    public async Task Cant_confirm_readings_do_not_count_as_cleared_and_end_unverified()
    {
        // (e) A CantConfirm read (probe threw / denied / unreadable) is NOT evidence the box cleared — the wave
        // keeps waiting, never proceeds. Sequence servicing → couldn't-confirm → couldn't-confirm → expiry →
        // the "couldn't confirm reboot-readiness" Unverified variant; the box is never rebooted.
        var box = new FakeBox
        {
            ReadinessScript =
            [
                new RebootReadiness(false, "TrustedInstaller is still Running (online servicing in progress)", RebootReadinessKind.ServicingActive),
                new RebootReadiness(false, "couldn't reach BOX to check reboot-readiness", RebootReadinessKind.CantConfirm),
                new RebootReadiness(false, "couldn't reach BOX to check reboot-readiness", RebootReadinessKind.CantConfirm),
            ],
        };
        var (wave, reboot, readiness, confirmation) = Build(box);

        HostPatchStatus result = await wave.RebootAndCommitAsync("BOX", Fast(settleMs: 40), readiness, confirmation, new RecProgress(), CancellationToken.None);

        Assert.Equal(PatchPhase.Unverified, result.Phase);
        Assert.Contains("couldn't confirm", result.Message);
        Assert.Contains("not rebooted", result.Message);
        Assert.False(reboot.Graceful);
        Assert.False(reboot.Forced);
    }

    [Fact]
    public async Task The_settle_poll_rechecks_readiness_each_iteration()
    {
        // (f) The wave's job here is a FRESH readiness read per iteration (the full-triple property is the
        // probe's, covered by the rv/kind tests). A held not-ready reading + a small window ⇒ several re-checks.
        var box = new FakeBox
        {
            ReadinessScript = [new RebootReadiness(false, "TrustedInstaller is still Running (online servicing in progress)", RebootReadinessKind.ServicingActive)],
        };
        var (wave, _, readiness, confirmation) = Build(box);

        HostPatchStatus result = await wave.RebootAndCommitAsync("BOX", Fast(settleMs: 40), readiness, confirmation, new RecProgress(), CancellationToken.None);

        Assert.Equal(PatchPhase.Unverified, result.Phase);
        Assert.True(box.ReadinessChecks >= 3, $"expected multiple re-checks, saw {box.ReadinessChecks}");
    }

    [Fact]
    public async Task Nothing_staged_on_the_first_check_is_an_immediate_nothing_to_commit_never_rebooting()
    {
        // (g.1) NothingStaged on the FIRST check → immediate NothingToCommit terminal, no poll delay, never rebooted.
        var box = new FakeBox
        {
            ReadinessScript = [new RebootReadiness(false, "no pending reboot — nothing is staged", RebootReadinessKind.NothingStaged)],
        };
        var (wave, reboot, readiness, confirmation) = Build(box);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Large settle window: if the wave wrongly polled instead of returning at once, this would take seconds.
        HostPatchStatus result = await wave.RebootAndCommitAsync("BOX", Fast(settleMs: 10000), readiness, confirmation, new RecProgress(), CancellationToken.None);
        sw.Stop();

        Assert.Equal(PatchPhase.NothingToCommit, result.Phase);
        Assert.Contains("nothing staged to commit", result.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"should return immediately, took {sw.Elapsed}"); // « the 10s window
        Assert.False(reboot.Graceful);
        Assert.False(reboot.Forced);
        Assert.Equal(1, box.ReadinessChecks); // one read, no poll
    }

    [Fact]
    public async Task Nothing_staged_mid_poll_ends_nothing_to_commit_never_rebooting()
    {
        // (g.2) Servicing finishes leaving NO pending reboot (self-rebooted, or nothing was ever staged): the
        // mid-poll NothingStaged read ends the wave with the calm NothingToCommit terminal — not a reboot.
        var box = new FakeBox
        {
            ReadinessScript =
            [
                new RebootReadiness(false, "TrustedInstaller is still Running (online servicing in progress)", RebootReadinessKind.ServicingActive),
                new RebootReadiness(false, "no pending reboot — nothing is staged", RebootReadinessKind.NothingStaged),
            ],
        };
        var (wave, reboot, readiness, confirmation) = Build(box);

        HostPatchStatus result = await wave.RebootAndCommitAsync("BOX", Fast(settleMs: 10000), readiness, confirmation, new RecProgress(), CancellationToken.None);

        Assert.Equal(PatchPhase.NothingToCommit, result.Phase);
        Assert.Contains("nothing to commit", result.Message);
        Assert.False(reboot.Graceful);
        Assert.False(reboot.Forced);
    }

    [Fact]
    public async Task Ready_on_the_first_check_proceeds_straight_to_graceful_with_no_settle_poll()
    {
        // (h) Regression lock: the common case (already reboot-ready) is unchanged — one readiness read, no
        // settle poll, straight to the graceful reboot and a green verify.
        var box = new FakeBox { GracefulTakesOffline = true, ComesBackAfterChecks = 2, UbrAfterReturn = TargetUbr };
        var (wave, reboot, readiness, confirmation) = Build(box);

        HostPatchStatus result = await wave.RebootAndCommitAsync("BOX", Fast(), readiness, confirmation, new RecProgress(), CancellationToken.None);

        Assert.Equal(PatchPhase.Done, result.Phase);
        Assert.True(reboot.Graceful);
        Assert.False(reboot.Forced);
        Assert.Equal(1, box.ReadinessChecks); // exactly one readiness read — no poll
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
    public async Task Already_in_progress_box_slow_to_leave_the_network_is_not_false_failed_as_rolled_back()
    {
        // Regression guard: a box reports "already in progress" (1115) but is SLOW to drop off — it still
        // answers (at its PRE-reboot build) for a couple of checks while shutting down. The wave must NOT
        // read that pre-reboot build and fail it as "rolled back"; it waits for the real offline, then
        // verifies the box that returns at the target build as green.
        var box = new FakeBox
        {
            RebootReportsAlreadyInProgress = true,
            GoesOfflineWhenAlreadyInProgress = false, // does not drop off immediately
            ChecksBeforeOffline = 2,                  // answers at the OLD build for 2 checks, then drops off
            ComesBackAfterChecks = 2,
            UbrAfterReturn = TargetUbr,
        };
        var (wave, reboot, readiness, confirmation) = Build(box);

        HostPatchStatus result = await wave.RebootAndCommitAsync("BOX", Fast(goOfflineMs: 40), readiness, confirmation, new RecProgress(), CancellationToken.None);

        Assert.Equal(PatchPhase.Done, result.Phase);     // verified green, NOT a false "rolled back"
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

    [Fact]
    public async Task Unobserved_reboot_never_seen_offline_is_proven_by_uptime_and_verifies_green()
    {
        // FM1: a 1115 box that is NEVER observed leaving the network but DID reboot (uptime resets after 2
        // reach checks) and returns at the target build. The clock-immune uptime proof unblocks confirmation,
        // so it verifies green WITHOUT a second forced reboot.
        var box = new FakeBox
        {
            RebootReportsAlreadyInProgress = true,
            GoesOfflineWhenAlreadyInProgress = false, // never drops off — the wave can't see it go down
            RebootsUnobservedAfterChecks = 2,         // ...but its uptime resets after 2 reach checks
            UbrAfterReturn = TargetUbr,
        };
        var (wave, reboot, readiness, confirmation) = Build(box);

        HostPatchStatus result = await wave.RebootAndCommitAsync("BOX", Fast(), readiness, confirmation, new RecProgress(), CancellationToken.None);

        Assert.Equal(PatchPhase.Done, result.Phase);
        Assert.True(reboot.Graceful);
        Assert.False(reboot.Forced); // proven by uptime, never force-rebooted a box that already rebooted
    }

    [Fact]
    public async Task Returned_but_confirmation_never_ready_ends_neutral_unverified_not_a_hang()
    {
        // FM2: the box drops and returns (sawOffline the classic way), but its UBR is unreadable forever, so
        // confirmation returns NotReady on every poll. Instead of spinning to the hard cap, the wave bounds the
        // reachable-but-unconfirmed phase and returns a neutral Unverified terminal.
        var box = new FakeBox { GracefulTakesOffline = true, ComesBackAfterChecks = 2, UbrAfterReturn = null };
        var (wave, _, readiness, confirmation) = Build(box);

        RebootWaveOptions opts = Fast() with { PostReturnConfirmWindow = TimeSpan.FromMilliseconds(150) };
        HostPatchStatus result = await wave.RebootAndCommitAsync("BOX", opts, readiness, confirmation, new RecProgress(), CancellationToken.None);

        Assert.Equal(PatchPhase.Unverified, result.Phase); // neutral — NOT Error, NOT a hang
        Assert.Contains("use Verify", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Box_never_seen_offline_and_never_proven_rebooted_ends_unverified()
    {
        // The !sawOffline-forever case: a 1115 box that never drops AND never reboots (uptime never resets, so
        // the proof is always false). It must not park forever — the reachable-but-unconfirmed bound resolves it
        // to a neutral Unverified, and it is never force-rebooted.
        var box = new FakeBox
        {
            RebootReportsAlreadyInProgress = true,
            GoesOfflineWhenAlreadyInProgress = false, // never drops
            // no RebootsUnobservedAfterChecks → it never reboots, so the uptime proof stays false
        };
        var (wave, reboot, readiness, confirmation) = Build(box);

        RebootWaveOptions opts = Fast() with { PostReturnConfirmWindow = TimeSpan.FromMilliseconds(150) };
        HostPatchStatus result = await wave.RebootAndCommitAsync("BOX", opts, readiness, confirmation, new RecProgress(), CancellationToken.None);

        Assert.Equal(PatchPhase.Unverified, result.Phase);
        Assert.False(reboot.Forced);
    }

    [Fact]
    public async Task Forced_escalation_is_suppressed_when_uptime_proves_the_graceful_reboot_completed()
    {
        // The graceful reboot won't be SEEN going offline, but it completes an unobserved reboot within the
        // graceful window (uptime resets after 2 checks). The proof suppresses the forced escalation — we never
        // force a box that provably already rebooted — and it still verifies green.
        var box = new FakeBox
        {
            GracefulTakesOffline = false,     // never observed dropping in the graceful window
            RebootsUnobservedAfterChecks = 2, // ...but it really rebooted (uptime resets)
            UbrAfterReturn = TargetUbr,
        };
        var (wave, reboot, readiness, confirmation) = Build(box);

        HostPatchStatus result = await wave.RebootAndCommitAsync("BOX", Fast(goOfflineMs: 100), readiness, confirmation, new RecProgress(), CancellationToken.None);

        Assert.Equal(PatchPhase.Done, result.Phase);
        Assert.True(reboot.Graceful);
        Assert.False(reboot.Forced); // suppressed by the uptime proof
    }

    [Fact]
    public async Task Forced_escalation_still_fires_when_the_uptime_proof_is_unreadable()
    {
        // Suppress-only: when the proof can't be read (unreadable boot time), a graceful reboot that won't take
        // MUST still escalate to a forced reboot — a genuinely hung graceful reboot is never left un-completed.
        var box = new FakeBox
        {
            GracefulTakesOffline = false,
            ForcedTakesOffline = true,
            BootReaderReturnsNull = true, // proof disabled — must NOT suppress the escalation
            ComesBackAfterChecks = 2,
            UbrAfterReturn = TargetUbr,
        };
        var (wave, reboot, readiness, confirmation) = Build(box);

        HostPatchStatus result = await wave.RebootAndCommitAsync("BOX", Fast(goOfflineMs: 40), readiness, confirmation, new RecProgress(), CancellationToken.None);

        Assert.Equal(PatchPhase.Done, result.Phase);
        Assert.True(reboot.Forced); // escalated, because the proof couldn't suppress it
    }

    [Fact]
    public async Task A_forward_clock_step_does_not_fake_a_reboot_proof_box_ends_unverified()
    {
        // Clock-step pin: a never-rebooted 1115 box, with a large forward clock step applied to BOTH LocalNow
        // and LastBootUpTime mid-watch. Because the uptime is LocalNow − LastBootUpTime, the step cancels and the
        // proof must NOT fire — the box ends neutral Unverified, never a false green.
        var box = new FakeBox
        {
            RebootReportsAlreadyInProgress = true,
            GoesOfflineWhenAlreadyInProgress = false, // never drops, never reboots
            ClockStep = TimeSpan.FromDays(10),        // a big forward step…
            ClockStepAfterReads = 1,                  // …applied after the baseline read
        };
        var (wave, _, readiness, confirmation) = Build(box);

        RebootWaveOptions opts = Fast() with { PostReturnConfirmWindow = TimeSpan.FromMilliseconds(150) };
        HostPatchStatus result = await wave.RebootAndCommitAsync("BOX", opts, readiness, confirmation, new RecProgress(), CancellationToken.None);

        Assert.Equal(PatchPhase.Unverified, result.Phase); // NOT Done — the step never faked a reboot
    }

    [Fact]
    public async Task A_forward_clock_step_does_not_suppress_a_needed_forced_escalation()
    {
        // Clock-step pin (escalation side): a graceful reboot that won't take, never actually reboots, with a big
        // forward clock step. The step must not fake a proof that suppresses the force — the forced escalation
        // STILL fires.
        var box = new FakeBox
        {
            GracefulTakesOffline = false,
            ForcedTakesOffline = false, // never actually reboots
            ClockStep = TimeSpan.FromDays(10),
            ClockStepAfterReads = 1,
        };
        var (wave, reboot, readiness, confirmation) = Build(box);

        HostPatchStatus result = await wave.RebootAndCommitAsync("BOX", Fast(goOfflineMs: 40), readiness, confirmation, new RecProgress(), CancellationToken.None);

        Assert.Equal(PatchPhase.Error, result.Phase);
        Assert.True(reboot.Forced); // the step did not suppress the escalation
    }

    [Fact]
    public async Task The_unverified_window_resets_when_the_box_flaps_offline_again_then_confirms_green()
    {
        // Window-reset guard: the box returns unconfirmed, drops again, and repeats many times before finally
        // returning confirmed. Each reachable stretch is a single beat, and every offline poll RESETS the
        // reachable-but-unconfirmed clock — so the box confirms green even though the COMBINED reachable time
        // across the episodes exceeds the window (which, without the reset, would have tripped Unverified).
        var box = new FakeBox
        {
            RebootReportsAlreadyInProgress = true,
            GoesOfflineWhenAlreadyInProgress = true, // enters the commit-watch already offline
            FlapScenario = true,
            FlapEpisodes = 12,          // 12 unconfirmed return→drop cycles, then a confirmed return
            FlapOfflineChecks = 1,
            UbrAfterReturn = TargetUbr,
        };
        var (wave, _, readiness, confirmation) = Build(box);

        var window = TimeSpan.FromMilliseconds(50);
        RebootWaveOptions opts = Fast() with { PostReturnConfirmWindow = window };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        HostPatchStatus result = await wave.RebootAndCommitAsync("BOX", opts, readiness, confirmation, new RecProgress(), CancellationToken.None);
        sw.Stop();

        Assert.Equal(PatchPhase.Done, result.Phase); // confirmed green — the window reset on each flap
        Assert.True(sw.Elapsed > window, "The flap must span longer than the window, so a no-reset wave would have tripped Unverified.");
    }

    private static (RebootWave wave, FakeReboot reboot, IRebootReadinessProbe readiness, IPostRebootConfirmation confirmation) Build(FakeBox box)
    {
        var reboot = new FakeReboot(box);
        var builds = new FakeBuilds(box);
        // Use a real UbrConfirmation backed by FakeBuilds so the same UBR logic is exercised through the seam.
        var confirmation = new UbrConfirmation(builds, TargetUbr);
        // Wire the clock-immune uptime reader in too, so the proof path is exercised through the real seam.
        var wave = new RebootWave(reboot, new FakeReach(box), new FakeBootTimeReader(box));
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
        public int ChecksBeforeOffline;                 // >0: answers (at the OLD build) this many reach checks, then drops off (a box slow to leave the network)
        public bool RebootRefusesGracefulAndEscalates;  // 1191: Windows refused the graceful form (a session is logged on) and the trigger escalated to forced on the same channel
        public bool EscalatedTakesOffline;              // ...and the box then drops off under that forced reboot

        // --- Uptime-proof fidelity ---
        // A modeled REAL reboot has completed: the boot reader then reports a small (reset) uptime; until then
        // it reports a large, slowly-growing uptime. HasRebooted flips on a real observed offline→return, OR
        // after RebootsUnobservedAfterChecks reachability checks WITHOUT the box ever reporting offline (FM1/FM3).
        public bool HasRebooted;
        public int? RebootsUnobservedAfterChecks;       // flip HasRebooted after this many reach checks, never dropping
        public int ReachChecks;                          // reachability checks seen so far
        public int BootReads;                            // boot-time reads so far (drives the tiny uptime growth)
        public TimeSpan InitialUptime = TimeSpan.FromDays(3); // large pre-reboot uptime
        public bool BootReaderReturnsNull;               // model an unreadable boot time (proof disabled → escalate/spin)
        public TimeSpan ClockStep = TimeSpan.Zero;       // a uniform clock step applied to BOTH LocalNow and LastBoot
        public int ClockStepAfterReads;                  // ...on boot reads AFTER this many (baseline is read #1)
        public static readonly DateTime BootAnchor = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);

        // --- Flap scenario (window-reset test) ---
        public bool FlapScenario;
        public int FlapEpisodes;                         // unconfirmed return→drop episodes before the final confirmed return
        public int FlapOfflineChecks = 1;                // offline checks between episodes
        public int FlapReturns;                          // unconfirmed/confirmed returns so far

        // --- Pre-reboot readiness settle poll (new) ---
        public List<RebootReadiness>? ReadinessScript;   // scripted CheckAsync results in order; HOLDS the last after exhaustion
        public int ReadinessChecks;                       // how many times FakeReadiness.CheckAsync ran (poll vs one-shot proof)
        public CancellationTokenSource? CancelSource;     // an operator Stop to fire mid-settle-poll
        public int CancelAfterReadinessChecks;            // ...once this many readiness reads have happened
    }

    /// <summary>Models the target's own clock: LocalDateTime − LastBootUpTime is the uptime, LARGE (and slowly
    /// growing) before a modeled reboot and SMALL after one. BOTH values are shifted by the same
    /// <see cref="FakeBox.ClockStep"/>, so their difference (the uptime) is invariant under a clock step —
    /// exactly the clock-immune property the wave's proof relies on. Returns null when the box models an
    /// unreadable boot time.</summary>
    private sealed class FakeBootTimeReader(FakeBox box) : IBootTimeReader
    {
        public Task<BootTimeReading?> ReadAsync(string host, CancellationToken cancellationToken)
        {
            if (box.BootReaderReturnsNull)
            {
                // Unreadable — a retry signal, never proof (the wave must NOT read this as a completed reboot).
                return Task.FromResult<BootTimeReading?>(null);
            }

            box.BootReads++;
            TimeSpan uptime = box.HasRebooted
                ? TimeSpan.FromSeconds(5)
                : box.InitialUptime + TimeSpan.FromSeconds(box.BootReads); // grows a little each read
            DateTime lastBoot = box.HasRebooted
                ? FakeBox.BootAnchor + box.InitialUptime + TimeSpan.FromSeconds(30)
                : FakeBox.BootAnchor;
            TimeSpan step = box.BootReads > box.ClockStepAfterReads ? box.ClockStep : TimeSpan.Zero;
            return Task.FromResult<BootTimeReading?>(new BootTimeReading(lastBoot + uptime + step, lastBoot + step));
        }
    }

    private sealed class FakeReboot(FakeBox box) : IRebootTrigger
    {
        public bool Graceful { get; private set; }
        public bool Forced { get; private set; }

        /// <summary>EVERY call to the trigger, graceful or forced — the double-reboot counter. One operator
        /// click on one box must never produce more than one dispatch to that box.</summary>
        public int Dispatches { get; private set; }

        /// <summary>The <c>forced</c> argument of each dispatch, in order — so a test can prove not just how
        /// many sends happened but which form each one asked for.</summary>
        public List<bool> ForcedArgs { get; } = [];

        public Task<RebootDispatch> RebootAsync(string host, bool forced, CancellationToken cancellationToken)
        {
            Dispatches++;
            ForcedArgs.Add(forced);
            if (forced) { Forced = true; } else { Graceful = true; }

            if (box.RebootReportsAlreadyInProgress)
            {
                // A shutdown is already in progress — the box goes offline on its own (no extra reboot needed).
                if (box.GoesOfflineWhenAlreadyInProgress) { box.Online = false; }
                return Task.FromResult(RebootDispatch.AlreadyInProgress);
            }

            if (box.RebootRefusesGracefulAndEscalates && !forced)
            {
                // Models the real trigger: Windows refused the GRACEFUL form because a session is logged on
                // (1191), so the trigger re-sent the FORCED form on the same healthy channel and the OS took
                // it. ONE send from the wave's point of view; the box is now going down FORCED.
                if (box.EscalatedTakesOffline) { box.Online = false; }
                return Task.FromResult(RebootDispatch.EscalatedToForced);
            }

            if (forced) { if (box.ForcedTakesOffline) box.Online = false; }
            else { if (box.GracefulTakesOffline) box.Online = false; }
            return Task.FromResult(RebootDispatch.Issued);
        }
    }

    private sealed class FakeReadiness(FakeBox box) : IRebootReadinessProbe
    {
        public Task<RebootReadiness> CheckAsync(string host, CancellationToken cancellationToken)
        {
            box.ReadinessChecks++;

            // Model an operator Stop landing mid-poll: cancel the shared CTS once enough reads have happened,
            // so the wave's next Task.Delay(PollInterval, token) throws (cancellation "during the wait").
            if (box.CancelSource is { } cts && box.CancelAfterReadinessChecks > 0 && box.ReadinessChecks >= box.CancelAfterReadinessChecks)
            {
                cts.Cancel();
            }

            if (box.ReadinessScript is { Count: > 0 } script)
            {
                int i = Math.Min(box.ReadinessChecks - 1, script.Count - 1); // hold the last reading after the script is exhausted
                return Task.FromResult(script[i]);
            }

            // Default (no script): the simple ready/not-ready bool. A not-ready default models servicing still
            // running (ServicingActive) — matching the harness's single not-ready reason.
            return Task.FromResult(box.Ready
                ? new RebootReadiness(true, "ready", RebootReadinessKind.Ready)
                : new RebootReadiness(false, "TrustedInstaller still running", RebootReadinessKind.ServicingActive));
        }
    }

    private sealed class FakeReach(FakeBox box) : IReachabilityProbe
    {
        public Task<bool> IsReachableAsync(string host, CancellationToken cancellationToken)
        {
            box.ReachChecks++;

            if (box.FlapScenario)
            {
                return Task.FromResult(FlapReach(box));
            }

            // FM1/FM3 fidelity: a box that completes a reboot WITHOUT ever being seen offline. After the
            // configured number of reachability checks its uptime has reset (HasRebooted) — but it stays
            // reachable the whole time, so the wave never observes a drop. It also reads its post-reboot build.
            if (box.RebootsUnobservedAfterChecks is int n && box.ReachChecks >= n && !box.HasRebooted)
            {
                box.HasRebooted = true;
                box.Ubr = box.UbrAfterReturn;
            }

            // A box slow to drop off after an "already in progress" reboot: it answers (still at the OLD
            // build) for a few checks, then leaves the network.
            if (box.Online && box.ChecksBeforeOffline > 0)
            {
                box.ChecksBeforeOffline--;
                if (box.ChecksBeforeOffline == 0) { box.Online = false; }
                return Task.FromResult(box.Online);
            }

            if (!box.Online)
            {
                box.OfflineChecks++;
                if (box.OfflineChecks >= box.ComesBackAfterChecks)
                {
                    box.Online = true;
                    box.Ubr = box.UbrAfterReturn;
                    box.HasRebooted = true; // a real observed drop→return IS a real reboot (uptime resets)
                }
            }

            return Task.FromResult(box.Online);
        }

        // Returns unconfirmed (UBR null), drops, and repeats FlapEpisodes times, THEN returns confirmed
        // (UBR=target). Each unconfirmed reachable stretch is a SINGLE beat before dropping again, so the wave's
        // reachable-but-unconfirmed window (reset on every offline poll) never accumulates past its bound —
        // proving the reset. Without the reset, the combined reachable time across the episodes would trip
        // Unverified.
        private static bool FlapReach(FakeBox box)
        {
            if (!box.Online)
            {
                box.OfflineChecks++;
                if (box.OfflineChecks >= box.FlapOfflineChecks)
                {
                    box.OfflineChecks = 0;
                    box.Online = true;
                    box.HasRebooted = true;
                    box.FlapReturns++;
                    box.Ubr = box.FlapReturns > box.FlapEpisodes ? box.UbrAfterReturn : null;
                }

                return box.Online;
            }

            // Online. An unconfirmed return (UBR null) drops again on the very next check; the final confirmed
            // return (UBR=target) stays up.
            if (box.Ubr is null)
            {
                box.Online = false;
                box.OfflineChecks = 0;
            }

            return box.Online;
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
