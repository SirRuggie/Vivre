# Vivre 1.20.2 — CU transfer load safeguard — 2026-10-23

This records the one-slot baseline delivered in `f701ff7`. Shaun subsequently requested a configurable default of 2; see [the follow-up acceptance ledger](cu-transfer-settings-validation.md) for the current implementation. Counts and one-slot statements below describe that original checkpoint.

## Acceptance ledger

| Requested capability | Status |
| --- | --- |
| Reduce simultaneous large CU file traffic from the controller across tabs | Implemented and locally tested: one process-wide slot for per-host stage checks and CU drop/copy/verification; nine real dummy-file copies completed without overlap |
| Keep target-side installation, routing, reboot choices and integrity checks intact | Verified by source review and focused regressions; the file slot is released before remote install/SCM launch; existing size/SHA-256 checks remain |
| Explain queued work and record transfer timing | Implemented: queued row messages and transfer start/verified-duration activity entries; timeout text now identifies an unknown install outcome |
| Independent review and local validation | Completed within the scope below; two review corrections applied and rechecked, no remaining review finding |
| Current version and documentation | Release build metadata reports 1.20.2.0 / 1.20.2+build; README, Help, changelog, architecture notes, index and living backlogs updated |
| Commit and push to master | Included in the mitigation/release commit containing this ledger; remote master verification is required at handoff |
| Signed ZIP and deployment | Not performed here: Shaun publishes with the existing signing certificate/private key and deploys after current work is reconciled |
| Confirm APVPatching no longer loses RDP during mixed installs | Not validated: requires the operator's next authorized production run after deployment |

## Evidence and limits

The October 23 screenshot shows 100% total CPU, System at 72.3%, Vivre at 14.3%, and multiple CU copies alongside normal WUA installs; a later screenshot shows widespread 90-second heartbeat failures. The earlier October 20 display-driver dumps explain the display/RDP failure mechanism, but not its initiating cause. Security drivers, SMB and other kernel work cannot be distinguished from these screenshots alone.

Source inspection of the 1.20.1 baseline confirmed there was no separate process-wide limit on large CU copies and verification reads. The per-workspace install limit defaults to 50 and does not protect controller file I/O across tabs. This change bounds that specific workload; it is a mitigation, not proof of the production root cause or a guarantee that all host freezes are resolved.

A read of the October 23 controller log showed widespread lost-heartbeat errors and an aggregate result of 0 installed / 34 failed at 14:07:29. That is the controller's reported result, not proof that all remote installations failed or stopped. Check target results before retrying. Private incident logs/dumps and generated validation artifacts are excluded from the commit.

Validation uses dummy files and inert remote-operation doubles. No production installation, reboot, security-agent change, host setting change or production workload replay is authorized as part of local validation.

## Implementation boundary

- `CuPackageIo` reuses the existing cancellation-aware `KeyedOperationGate` with a shared process-wide key; even an immediately available slot sends synchronous file work to a worker thread.
- `FullPackageLcuLane.StageAsync` queues per-host package resolution/optional hash checks. `SmbAgentLane` separately queues the CU agent/config drop, package copy and verification, releasing the slot before SCM creation and remote installation/tailing. Remote installs may overlap each other and the next transfer.
- A canceled waiter never started a CU drop, so it skips remote file cleanup; regular payload-null SMB/WUA cleanup keeps its prior behavior. Failure/cancellation releases the file slot.
- Existing `File.Copy` and source/destination integrity checks remain. An already-running synchronous copy cannot be interrupted immediately; cancellation is checked before launch and after the copy/verification path, so canceled work does not launch the agent.
- No bandwidth cap, separate-process coordination, global WUA throttle, source-hash cache, changed install selection or reboot behavior is added. The initial package-choice precheck remains separate. A single file job or another controller workload may still saturate the host.
- WUA's 90-second watchdog and cleanup behavior are unchanged; only its user-facing diagnosis now states the observed loss of monitoring and unknown installation outcome.

## Validation and independent review

- Release solution build with locked restore passed, zero errors. The configured agent-signing certificate is unavailable to this local account, producing one existing warning; no signed deployment ZIP was produced.
- Update/gate regression selection: **766 passed, 0 failed, 0 skipped** before the final text/cleanup review corrections. Final focused CU gate/full-package/integrity/SMB/WUA-streaming selection after those corrections: **39 passed, 0 failed, 0 skipped**. These overlap; they are not 805 distinct tests.
- New checks cover nine real 2 MiB dummy copies with byte preservation, canceled waiters not executing, failure/cancellation releasing the slot, a canceled actual SMB-lane waiter returning before any drop, and the CU slot being available while simulated remote installation remains pending.
- Existing WPF CU responsiveness probe used a **128 MiB local dummy file**, the actual resolver/stage code, inert SMB operations, and `DOTNET_PROCESSOR_COUNT=2`. Eight stage requests took 1.21 ms to launch, with a queued dispatcher callback at 2.44 ms; an injected blocked preflight canceled in 4.60 ms, cleared busy state, and did not accumulate readers on retry. This changes .NET's processor-count view, not VM hardware; it is not a live SMB/EDR/RDP reproduction or a latency guarantee.
- Independent read-only review by a separate `gpt-5.6-sol` agent found no substantive gate/release/integrity defect. It requested preserving payload-null cleanup and narrowing documentation to per-host checks; both corrections were applied and the reviewer verified the final diff. Its separate 29-test focused run also passed; this overlaps the counts above.
- The reviewer also assessed the revised timeout wording as safer for uncertain controller/network/target failures. No timeout extension or suppressed error was introduced.
- `git diff --check` passed. Historical acceptance records and deferred S-1/U-40/U-42/F12 decisions are preserved.

## Packaging and operational follow-up

On the publishing workstation in `C:\src\Vivre`, after pulling the committed source:

```powershell
.\publish.ps1 -Zip
```

The existing signing certificate/private key is still required. Output remains `publish\Vivre.zip` with app folder `publish\Vivre`; shortcut paths and publish safety/signature checks are unchanged. Do not substitute the unsigned local build for the signed publish output.

This source change does not affect an already-running Vivre instance. Reconcile the current target outcomes and existing work before replacing the controller. During the next operator-authorized run, verify queued CU copies, controller CPU/RDP responsiveness and remote results; if freezing continues, capture host CPU/driver/I/O evidence rather than treating this mitigation as a confirmed root-cause fix. No production replay or reboot was performed as part of this release.
