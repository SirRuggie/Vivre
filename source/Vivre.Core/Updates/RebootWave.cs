using System.Diagnostics;

namespace Vivre.Core.Updates;

/// <summary>
/// The night step of the 2016 LCU lane: reboot a staged box and watch it commit. Per-box flow, built to
/// the rules locked with the operator:
/// <list type="number">
///   <item>Re-check readiness in the instant before rebooting (TrustedInstaller stopped + RebootPending) —
///   reboot only when the online phase is genuinely done, so we never hit the 2-hour Stopping hang.</item>
///   <item>Reboot graceful first (let SQL/services flush); if the box won't drop off within the go-offline
///   window, escalate to a forced reboot to complete it. This escalation is scoped strictly: the wave only
///   runs on boxes the operator explicitly SELECTED and CONFIRMED, so the force is the tail of a reboot they
///   ordered on a specific box — never the tool deciding on its own to reboot or force anything.</item>
///   <item>While offline, the clock only ever FLAGS "Overdue — check console" past the ceiling; it never
///   stops watching and never fails the box.</item>
///   <item>When the box returns, confirm via the injected <see cref="IPostRebootConfirmation"/> — a
///   NotReady is a retry, never a failure. Only the confirmation strategy decides pass/fail. A late
///   return still gets confirmed.</item>
/// </list>
/// The standalone Verify action is the durable net beyond this live loop, so no box is ever abandoned by
/// a timer. The caller (PatchService) serialises this against Stage/Cleanup on the same host.
///
/// <para>The readiness probe and post-reboot confirmation are passed per-call so the wave is reusable
/// across box types: 2016 boxes get <see cref="DcomRebootReadinessProbe"/> + <see cref="UbrConfirmation"/>;
/// other boxes get <see cref="BasicReachabilityReadinessProbe"/> + <see cref="ReadyConfirmation"/>.</para>
/// </summary>
public sealed class RebootWave
{
    private readonly IRebootTrigger _reboot;
    private readonly IReachabilityProbe _reach;
    private readonly IBootTimeReader? _bootTime;
    private readonly Vivre.Core.Logging.IActivityLog? _trace;

    // The slack the clock-immune uptime proof allows before it calls a reboot "proven". Read latency and
    // jitter can make two uptime samples disagree by a few seconds; 2 minutes swamps that noise while staying
    // far below the smallest REAL signal — a genuine reboot drops the expected uptime by the entire pre-reboot
    // session uptime plus the downtime, always minutes to days. See ProvenRebootedAsync.
    private static readonly TimeSpan UptimeProofMargin = TimeSpan.FromMinutes(2);

    /// <param name="bootTime">Optional clock-immune uptime reader. When supplied, the wave can PROVE a reboot
    /// completed even if it never saw the box drop off the network (a dead window between polls, or a fast
    /// reboot) — a reset uptime is the proof. Null disables the proof (the wave falls back to the observed-drop
    /// gating exactly as before); an unreadable read is never a false success.</param>
    /// <param name="trace">Optional high-volume diagnostic breadcrumb sink. File-only in the Desktop
    /// implementation — never mirrored to the UI panel. Null = no tracing.</param>
    public RebootWave(
        IRebootTrigger reboot,
        IReachabilityProbe reachability,
        IBootTimeReader? bootTime = null,
        Vivre.Core.Logging.IActivityLog? trace = null)
    {
        _reboot = reboot ?? throw new ArgumentNullException(nameof(reboot));
        _reach = reachability ?? throw new ArgumentNullException(nameof(reachability));
        _bootTime = bootTime;
        _trace = trace;
    }

    /// <summary>Reboots <paramref name="host"/> and tracks the commit until the
    /// <paramref name="confirmation"/> strategy confirms success
    /// (<see cref="PatchPhase.Done"/>), a rollback/no-take is detected, the reboot won't take, or the
    /// box stays offline past the hard cap (red — use Verify when it returns).</summary>
    /// <param name="rebootGate">Optional rate-limiter for reboot issuance. Acquired only around the
    /// actual reboot trigger and released immediately after — never held through the offline watch.</param>
    public async Task<HostPatchStatus> RebootAndCommitAsync(
        string host,
        RebootWaveOptions options,
        IRebootReadinessProbe readiness,
        IPostRebootConfirmation confirmation,
        IProgress<HostPatchStatus> progress,
        CancellationToken cancellationToken,
        IRebootGate? rebootGate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(readiness);
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(progress);

        // Issues the reboot, acquiring the gate (if any) only around this call and releasing it
        // immediately after — never held through the offline watch so it never serializes per-box verify.
        async Task<RebootDispatch> IssueRebootAsync(bool forced)
        {
            if (rebootGate is null)
            {
                return await _reboot.RebootAsync(host, forced, cancellationToken).ConfigureAwait(false);
            }

            using IDisposable _ = await rebootGate.EnterAsync(cancellationToken).ConfigureAwait(false);
            return await _reboot.RebootAsync(host, forced, cancellationToken).ConfigureAwait(false);
        }

        // 1) Re-check readiness right now — TI must already be stopped, or we'd reboot into the hang.
        progress.Report(new HostPatchStatus(PatchPhase.Scanning, "Checking reboot-readiness…"));
        RebootReadiness ready = await readiness.CheckAsync(host, cancellationToken).ConfigureAwait(false);
        if (!ready.IsReady)
        {
            return Fail(progress, $"{host} isn't reboot-ready — {ready.Reason}. Stage it (and let it finish) first.");
        }

        // Capture the pre-reboot baseline (e.g. LastBootUpTime) BEFORE issuing the reboot, so confirmation
        // can tell a REAL reboot from a brief reachability flicker during reboot-prep — a box that merely
        // flickered and came back on the SAME boot has not rebooted. No-op for strategies that don't need a
        // baseline (e.g. the 2016 UBR check). Read-only — captures a value, never reboots anything.
        await confirmation.CaptureBaselineAsync(host, cancellationToken).ConfigureAwait(false);

        // Clock-immune uptime baseline (best-effort): the target's own LocalDateTime − LastBootUpTime, in one
        // query. It lets us PROVE a reboot completed even if we never see the box drop off the network (a dead
        // window between polls, or a reboot that came and went fast). Null when there's no reader or it couldn't
        // be read — the proof is then simply disabled for this box (we fall back to the observed-drop gating).
        BootTimeReading? uptimeBaseline = _bootTime is null
            ? null
            : await _bootTime.ReadAsync(host, cancellationToken).ConfigureAwait(false);
        _trace?.Trace(host, uptimeBaseline is null
            ? "uptime baseline: unavailable (no reader or unreadable) — proof disabled for this box"
            : $"uptime baseline: LocalNow={uptimeBaseline.LocalNow:o} LastBoot={uptimeBaseline.LastBootUpTime:o} Uptime={uptimeBaseline.Uptime}");

        // 2) Graceful reboot first (let SQL/services flush cleanly). NOTE ON SCOPE: reaching this method at
        // all means the operator already picked THIS specific box and confirmed a Reboot Wave on it — the
        // tool never gets here on its own. So the escalation below is the *completion* of a reboot the
        // operator ordered on a box they selected, never an independent decision to reboot or force anything.
        progress.Report(new HostPatchStatus(PatchPhase.Rebooting, "Rebooting (graceful)…"));
        RebootDispatch graceful = await IssueRebootAsync(forced: false).ConfigureAwait(false);

        // Single since-reboot-ordered clock, started the instant the graceful dispatch returns. It measures
        // the HardCap / Overdue bounds and the "N min since the reboot was ordered" messages (the old per-entry
        // "offline" stopwatch mislabelled a still-reachable box as "offline N min"), AND supplies the elapsed
        // for the uptime proof. The sub-second gap between capturing uptimeBaseline just above and starting this
        // stopwatch is far inside UptimeProofMargin, so reusing it as the proof's elapsed is safe.
        var sinceOrdered = Stopwatch.StartNew();
        _trace?.Trace(host, $"reboot dispatched forced=false: {graceful}");

        // A shutdown ALREADY in progress means the box is going offline on its own — don't escalate or
        // wait-then-fail; drop straight into the commit-watch below ("slow, not hung").
        bool alreadyGoingOffline = graceful == RebootDispatch.AlreadyInProgress;
        if (alreadyGoingOffline)
        {
            progress.Report(new HostPatchStatus(PatchPhase.Rebooting,
                "A shutdown is already in progress — still committing (slow, not hung); watching for it to finish…"));
        }

        // 3) Wait for it to drop off the network; if the graceful reboot the operator ordered won't take
        // within the window, escalate to a forced reboot to make THAT ordered reboot actually complete. The
        // FORCED wait uses a strictly-longer window (ForcedGoOfflineWindow) — a box mid-CBS-commit can hold
        // the network up well past the graceful window, and re-using the same window would false-fail it.
        // sawOffline gates the commit-watch below: we must SEE the box leave the network before reading its
        // post-reboot build, or a box that merely answers slowly during shutdown reads its OLD build and
        // false-fails as "rolled back". The "already in progress" paths leave it false (we never observed the
        // drop), so the watch waits for the real offline before confirming.
        bool sawOffline = false;
        if (!alreadyGoingOffline)
        {
            _trace?.Trace(host, $"WaitForOffline(graceful) window={options.GoOfflineWindow}");
            bool droppedGraceful = await WaitForOfflineAsync(host, options.GoOfflineWindow, options.PollInterval, cancellationToken).ConfigureAwait(false);
            _trace?.Trace(host, $"WaitForOffline(graceful) result={(droppedGraceful ? "observed-offline" : "window-expired")} sinceOrdered={sinceOrdered.Elapsed}");

            if (droppedGraceful)
            {
                sawOffline = true;
            }
            else if (await ProvenRebootedAsync(host, uptimeBaseline, sinceOrdered.Elapsed, cancellationToken).ConfigureAwait(false))
            {
                // The graceful reboot already COMPLETED — we just never saw the box leave the network (the dead
                // window fell between polls, or it came and went fast). The clock-immune uptime proof confirms
                // it, so SUPPRESS the forced escalation: we never force a box that provably already rebooted.
                // Drop into the commit-watch to verify, exactly as after an observed drop. (Suppress-only — an
                // unreadable or not-proven box below STILL escalates, so a genuinely hung graceful reboot is
                // still completed.)
                sawOffline = true;
                _trace?.Trace(host, "escalation suppressed: uptime proof shows the graceful reboot already completed");
                progress.Report(new HostPatchStatus(PatchPhase.Rebooting,
                    "Rebooted already — the graceful reboot completed without being seen going down; verifying…"));
            }
            else
            {
                progress.Report(new HostPatchStatus(PatchPhase.Rebooting,
                    $"Still up after {options.GoOfflineWindow.TotalMinutes:N0} min — escalating to a forced reboot to complete it…"));
                RebootDispatch forced = await IssueRebootAsync(forced: true).ConfigureAwait(false);
                _trace?.Trace(host, $"reboot dispatched forced=true: {forced}");

                if (forced == RebootDispatch.AlreadyInProgress)
                {
                    // The forced call confirms a shutdown is already underway — the box IS going down (slowly).
                    // Don't fail it; drop into the commit-watch (which waits for the real offline before verifying).
                    progress.Report(new HostPatchStatus(PatchPhase.Rebooting,
                        "A shutdown is already in progress — still committing (slow, not hung); watching for it to finish…"));
                }
                else
                {
                    _trace?.Trace(host, $"WaitForOffline(forced) window={options.ForcedGoOfflineWindow}");
                    bool droppedForced = await WaitForOfflineAsync(host, options.ForcedGoOfflineWindow, options.PollInterval, cancellationToken).ConfigureAwait(false);
                    _trace?.Trace(host, $"WaitForOffline(forced) result={(droppedForced ? "observed-offline" : "window-expired")} sinceOrdered={sinceOrdered.Elapsed}");

                    if (!droppedForced)
                    {
                        // Both windows expired with no positive "going offline" signal — we genuinely can't confirm
                        // it dropped. Be honest (it may be committing very slowly, or it may be stuck), not alarming
                        // ("the reboot isn't taking" wrongly implies the reboot failed).
                        _trace?.Trace(host, "terminal: hasn't gone offline after forced reboot");
                        return Fail(progress,
                            $"{host} hasn't gone offline after a forced reboot — it may still be committing updates (slow), or it may be stuck. Check the console/iLO, or use Verify once it's back.");
                    }

                    sawOffline = true;
                    progress.Report(new HostPatchStatus(PatchPhase.Rebooting, "Escalated to a forced reboot."));
                }
            }
        }

        // 4) Offline → committing. Poll for return; the clock only FLAGS overdue, the confirmation strategy
        // decides pass/fail. sinceOrdered (started at the graceful dispatch) is the honest since-reboot-ordered
        // clock now — the old per-entry "offline" stopwatch mislabelled a still-reachable box as "offline N min".
        bool flaggedOverdue = false;

        // Bounds the CONTINUOUSLY-reachable-but-unconfirmed phase: once the box has been reachable this long
        // without a confirmation, we stop spinning and return a neutral Unverified (covers BOTH an endless
        // NotReady confirmation — e.g. an unreadable UBR — and a box we never saw drop and can't prove rebooted).
        // Reset to null every time a poll sees the box offline, so a box that flaps (returns, drops again,
        // returns) re-arms the window each time.
        Stopwatch? reachableSince = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (sinceOrdered.Elapsed > options.HardCap)
            {
                _trace?.Trace(host, $"terminal: hard cap {options.HardCap} exceeded ({sinceOrdered.Elapsed} since ordered)");
                return Fail(progress,
                    $"{host} hasn't returned after {sinceOrdered.Elapsed.TotalMinutes:N0} min — no longer tracking it live. Use Verify once it's back up.");
            }

            if (!flaggedOverdue && sinceOrdered.Elapsed > options.OfflineCeiling)
            {
                flaggedOverdue = true;
                progress.Report(new HostPatchStatus(PatchPhase.Rebooting,
                    $"Overdue — {sinceOrdered.Elapsed.TotalMinutes:N0} min since the reboot was ordered (still watching). Check console/iLO if it doesn't return soon."));
            }

            await Task.Delay(options.PollInterval, cancellationToken).ConfigureAwait(false);

            bool reachable = await _reach.IsReachableAsync(host, cancellationToken).ConfigureAwait(false);
            _trace?.Trace(host,
                $"commit-watch beat: sinceOrdered={sinceOrdered.Elapsed} reachable={reachable} sawOffline={sawOffline} flaggedOverdue={flaggedOverdue} reachableSince={(reachableSince is null ? "null" : reachableSince.Elapsed.ToString())}");

            if (!reachable)
            {
                sawOffline = true;
                reachableSince = null; // re-arm the reachable-but-unconfirmed window if the box returns again
                progress.Report(new HostPatchStatus(PatchPhase.Rebooting,
                    $"Committing (offline) — {sinceOrdered.Elapsed.TotalMinutes:N0} min since the reboot was ordered…"));
                continue;
            }

            // Reachable. Start (or keep) the reachable-but-unconfirmed clock and bail to a neutral Unverified
            // terminal if it runs past the bound — a VISIBLE, honest "couldn't confirm" that never reads green.
            reachableSince ??= Stopwatch.StartNew();
            if (reachableSince.Elapsed > options.PostReturnConfirmWindow)
            {
                var unv = new HostPatchStatus(PatchPhase.Unverified,
                    $"{host} is back on the network but the reboot couldn't be confirmed within {options.PostReturnConfirmWindow.TotalMinutes:N0} min — use Verify to check it.");
                _trace?.Trace(host, $"terminal: Unverified — reachable {reachableSince.Elapsed} without confirmation (bound {options.PostReturnConfirmWindow})");
                progress.Report(unv);
                return unv;
            }

            // Reachable but we have NOT yet seen it leave the network. Confirming now could read the PRE-reboot
            // build and false-fail the box as "rolled back", so first try to PROVE it rebooted via the
            // clock-immune uptime check. Proven → treat as sawOffline (confirmation then runs on the next
            // iteration, exactly as after a real drop). Not proven / unreadable → keep waiting for the real
            // drop, exactly as before — the 2016 false-rollback guard is preserved and STRENGTHENED (the proof
            // is clock-immune, so a clock step can't fake it).
            if (!sawOffline)
            {
                if (await ProvenRebootedAsync(host, uptimeBaseline, sinceOrdered.Elapsed, cancellationToken).ConfigureAwait(false))
                {
                    sawOffline = true;
                    _trace?.Trace(host, "commit-watch: uptime proof shows the reboot completed (never seen offline) — confirming");
                    progress.Report(new HostPatchStatus(PatchPhase.Rebooting,
                        "Reboot confirmed (uptime reset) — verifying…"));
                    continue;
                }

                progress.Report(new HostPatchStatus(PatchPhase.Rebooting,
                    "Still finishing the restart — waiting for it to drop off the network before verifying…"));
                continue;
            }

            // Back on the network after a confirmed offline (or a clock-immune proof) — ask the confirmation
            // strategy. NotReady = retry, never a failure.
            RebootConfirmationResult verdict = await confirmation.ConfirmAsync(host, cancellationToken).ConfigureAwait(false);
            _trace?.Trace(host, $"confirmation: {verdict.Outcome} — {verdict.Message}");

            if (verdict.Outcome == RebootConfirmationOutcome.Confirmed)
            {
                var done = new HostPatchStatus(PatchPhase.Done,
                    $"{verdict.Message} (committed in ~{sinceOrdered.Elapsed.TotalMinutes:N0} min)");
                _trace?.Trace(host, "terminal: Done");
                progress.Report(done);
                return done;
            }

            if (verdict.Outcome == RebootConfirmationOutcome.Failed)
            {
                _trace?.Trace(host, "terminal: Failed (confirmation)");
                return Fail(progress, verdict.Message);
            }

            progress.Report(new HostPatchStatus(PatchPhase.Rebooting,
                "Back online — waiting for it to finish coming up to verify…"));
        }
    }

    /// <summary>
    /// PROVES a reboot completed via a clock-immune uptime check — used to unblock confirmation on a box whose
    /// drop off the network was never observed. Returns <c>false</c> (never <c>true</c>) when there's no reader
    /// or no baseline, or when the current uptime can't be read — an unreadable proof is NEVER a false green;
    /// the box then keeps waiting (and is ultimately bounded by
    /// <see cref="RebootWaveOptions.PostReturnConfirmWindow"/>).
    ///
    /// <para><b>Why it's clock-immune:</b> both <see cref="BootTimeReading.LocalNow"/> and
    /// <see cref="BootTimeReading.LastBootUpTime"/> are read from the TARGET's own clock in one query, so a
    /// uniform clock shift (NTP correction, manual set) moves both equally and leaves the derived
    /// <see cref="BootTimeReading.Uptime"/> invariant. The <paramref name="elapsedSinceBaseline"/> comes from a
    /// monotonic <see cref="Stopwatch"/>, immune to wall-clock steps too. So the ONLY thing that shrinks
    /// (baseline.Uptime + elapsed) − current.Uptime past <see cref="UptimeProofMargin"/> is a genuine reboot
    /// resetting the uptime — a box that merely answered slowly (same boot) reads the same uptime, plus the
    /// elapsed we've been waiting, so the difference stays near zero.</para>
    /// </summary>
    private async Task<bool> ProvenRebootedAsync(string host, BootTimeReading? baseline, TimeSpan elapsedSinceBaseline, CancellationToken cancellationToken)
    {
        if (_bootTime is null || baseline is null)
        {
            return false;
        }

        BootTimeReading? current = await _bootTime.ReadAsync(host, cancellationToken).ConfigureAwait(false);
        _trace?.Trace(host,
            $"uptime proof: baseline LocalNow={baseline.LocalNow:o} LastBoot={baseline.LastBootUpTime:o} Uptime={baseline.Uptime}; " +
            $"current LocalNow={current?.LocalNow.ToString("o") ?? "null"} LastBoot={current?.LastBootUpTime.ToString("o") ?? "null"} Uptime={(current is null ? "null" : current.Uptime.ToString())}; " +
            $"elapsedSinceBaseline={elapsedSinceBaseline}");

        if (current is null)
        {
            return false;
        }

        // If the box never rebooted, current.Uptime ≈ baseline.Uptime + elapsed, so the difference is near zero.
        // A real reboot collapses current.Uptime to seconds/minutes, so the difference jumps to the whole
        // pre-reboot session uptime — always far past the 2-minute margin (which only guards read jitter).
        return (baseline.Uptime + elapsedSinceBaseline) - current.Uptime > UptimeProofMargin;
    }

    /// <summary>Polls reachability until the host drops off the network (reboot started) or the window
    /// elapses. Returns true once it's offline.</summary>
    private async Task<bool> WaitForOfflineAsync(string host, TimeSpan window, TimeSpan poll, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < window)
        {
            ct.ThrowIfCancellationRequested();
            if (!await _reach.IsReachableAsync(host, ct).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(poll, ct).ConfigureAwait(false);
        }

        return false;
    }

    private static HostPatchStatus Fail(IProgress<HostPatchStatus> progress, string message)
    {
        var failed = HostPatchStatus.Failed(message);
        progress.Report(failed);
        return failed;
    }
}
