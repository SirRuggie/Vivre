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

    public RebootWave(IRebootTrigger reboot, IReachabilityProbe reachability)
    {
        _reboot = reboot ?? throw new ArgumentNullException(nameof(reboot));
        _reach = reachability ?? throw new ArgumentNullException(nameof(reachability));
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

        // 2) Graceful reboot first (let SQL/services flush cleanly). NOTE ON SCOPE: reaching this method at
        // all means the operator already picked THIS specific box and confirmed a Reboot Wave on it — the
        // tool never gets here on its own. So the escalation below is the *completion* of a reboot the
        // operator ordered on a box they selected, never an independent decision to reboot or force anything.
        progress.Report(new HostPatchStatus(PatchPhase.Rebooting, "Rebooting (graceful)…"));
        RebootDispatch graceful = await IssueRebootAsync(forced: false).ConfigureAwait(false);

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
            if (await WaitForOfflineAsync(host, options.GoOfflineWindow, options.PollInterval, cancellationToken).ConfigureAwait(false))
            {
                sawOffline = true;
            }
            else
            {
                progress.Report(new HostPatchStatus(PatchPhase.Rebooting,
                    $"Still up after {options.GoOfflineWindow.TotalMinutes:N0} min — escalating to a forced reboot to complete it…"));
                RebootDispatch forced = await IssueRebootAsync(forced: true).ConfigureAwait(false);

                if (forced == RebootDispatch.AlreadyInProgress)
                {
                    // The forced call confirms a shutdown is already underway — the box IS going down (slowly).
                    // Don't fail it; drop into the commit-watch (which waits for the real offline before verifying).
                    progress.Report(new HostPatchStatus(PatchPhase.Rebooting,
                        "A shutdown is already in progress — still committing (slow, not hung); watching for it to finish…"));
                }
                else if (!await WaitForOfflineAsync(host, options.ForcedGoOfflineWindow, options.PollInterval, cancellationToken).ConfigureAwait(false))
                {
                    // Both windows expired with no positive "going offline" signal — we genuinely can't confirm
                    // it dropped. Be honest (it may be committing very slowly, or it may be stuck), not alarming
                    // ("the reboot isn't taking" wrongly implies the reboot failed).
                    return Fail(progress,
                        $"{host} hasn't gone offline after a forced reboot — it may still be committing updates (slow), or it may be stuck. Check the console/iLO, or use Verify once it's back.");
                }
                else
                {
                    sawOffline = true;
                    progress.Report(new HostPatchStatus(PatchPhase.Rebooting, "Escalated to a forced reboot."));
                }
            }
        }

        // 4) Offline → committing. Poll for return; the clock only FLAGS overdue, the confirmation strategy decides pass/fail.
        var offline = Stopwatch.StartNew();
        bool flaggedOverdue = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (offline.Elapsed > options.HardCap)
            {
                return Fail(progress,
                    $"{host} hasn't returned after {offline.Elapsed.TotalMinutes:N0} min — no longer tracking it live. Use Verify once it's back up.");
            }

            if (!flaggedOverdue && offline.Elapsed > options.OfflineCeiling)
            {
                flaggedOverdue = true;
                progress.Report(new HostPatchStatus(PatchPhase.Rebooting,
                    $"Overdue — offline {offline.Elapsed.TotalMinutes:N0} min (still watching). Check console/iLO if it doesn't return soon."));
            }

            await Task.Delay(options.PollInterval, cancellationToken).ConfigureAwait(false);

            if (!await _reach.IsReachableAsync(host, cancellationToken).ConfigureAwait(false))
            {
                sawOffline = true;
                progress.Report(new HostPatchStatus(PatchPhase.Rebooting,
                    $"Committing (offline) — {offline.Elapsed.TotalMinutes:N0} min…"));
                continue;
            }

            // Reachable but we have NOT yet seen it leave the network (a shutdown was "already in progress"
            // but the box is slow to drop off). Confirming now would read the PRE-reboot build and false-fail
            // the box as "rolled back", so wait until it has actually gone offline first.
            if (!sawOffline)
            {
                progress.Report(new HostPatchStatus(PatchPhase.Rebooting,
                    "Still finishing the restart — waiting for it to drop off the network before verifying…"));
                continue;
            }

            // Back on the network after a confirmed offline — ask the confirmation strategy. NotReady = retry, never a failure.
            RebootConfirmationResult verdict = await confirmation.ConfirmAsync(host, cancellationToken).ConfigureAwait(false);

            if (verdict.Outcome == RebootConfirmationOutcome.Confirmed)
            {
                var done = new HostPatchStatus(PatchPhase.Done,
                    $"{verdict.Message} (committed in ~{offline.Elapsed.TotalMinutes:N0} min)");
                progress.Report(done);
                return done;
            }

            if (verdict.Outcome == RebootConfirmationOutcome.Failed)
            {
                return Fail(progress, verdict.Message);
            }

            progress.Report(new HostPatchStatus(PatchPhase.Rebooting,
                "Back online — waiting for it to finish coming up to verify…"));
        }
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
