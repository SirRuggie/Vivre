# Vivre review decisions and implementation queue

Living decision log for the [October 5, 2026 design review](C:/src/Vivre/docs/vivre-design-review-2026-10-05.md). The original review is a point-in-time record and stays unchanged. This log records Shaun's decisions as each finding is discussed.

Last updated: October 14, 2026.

## Authorization and status

**Implementation authorized October 14, 2026.** Shaun asked to start tackling the agreed findings. The current implementation batch is F01 + F09 (Details > Free up disk space), with the Server 2016 component-cleanup workflow preserved. Other agreed findings remain queued; F12 remains explicitly skipped. No commit, deployment, or live destructive validation is included.

The original report's severity ratings remain review assessments, not an agreed implementation order. Record a priority change only when Shaun makes that decision. Keep unreviewed findings pending rather than treating silence as acceptance.

## Decision register

| Finding | Subject | Discussion status | Implementation authorization |
|---|---|---|---|
| F01 | Details window disk cleanup conflicts and package preservation | Implemented and locally validated | Current batch |
| F02 | Reboot disclosure and approved targets | Direction agreed; scope clarified below | Not authorized |
| F03 | SMB reboot dispatch reporting | Direction agreed; independent red-team required | Not authorized |
| F04 | RDP persistence and recovery | Direction agreed; safe writes, backups, load-failure protection | Not authorized |
| F05 | Consistent and recoverable package staging | Direction agreed; verify before replacement, preserve prior package | Not authorized |
| F06 | Ambiguous WUG device names | Direction agreed; independent red-team required | Not authorized |
| F07 | Recovery from cached Kerberos failure | Direction agreed; per-host retry and fresh attempt after credential change | Not authorized |
| F08 | Software absence versus query failure | Direction agreed; installed, absent, and could-not-check results | Not authorized |
| F09 | Truthful disk cleanup outcomes | Implemented and locally validated | Current batch |
| F10 | Preference write ordering and exit | Direction agreed; ordered saves and bounded normal-exit flush | Not authorized |
| F11 | WinRM staging memory and temporary files | Direction agreed; bounded chunks, integrity checks, owned-file cleanup | Not authorized |
| F12 | Concurrent Server 2016 staging files | Skipped by Shaun; excluded from implementation queue | Not authorized |

## F01 Details window disk cleanup

### Operating context confirmed by Shaun

Shaun uses **Free up disk space in the machine Details window** only when needed, not continuously or as a routine fleet operation. It was initially confused in this discussion with the separate **Server 2016 Clean up action in the patching panel**, which Shaun uses monthly and reports has worked fine.

The Server 2016 component-cleanup action already participates in the shared per-host patch-operation guard within a Vivre instance. F01 concerns the general disk-cleanup action, not a missing guard on Server 2016 component cleanup. Occasional use reduces exposure; the conflict remains possible when the actions overlap. No new severity or implementation priority has been agreed.

### Agreed direction

On October 5, Shaun said he liked the proposed changes after clarifying which action was affected:

1. Retain Free up disk space for use when needed.
2. Make disk cleanup and incompatible operations share execution ownership for the same host across tabs and Details windows. Patching, staging, and component cleanup must not overlap with this action; the protection must work in both directions.
3. Explain clearly when the action is blocked and which operation is responsible.
4. Preserve Vivre staged packages and active working files during general cleanup. Any deletion of staged packages should be a separate explicit choice.
5. Keep the existing Server 2016 component-cleanup workflow unchanged as part of this finding's scope.

**Implementation status: implemented and locally validated; not deployed. Authorization: granted October 14, 2026 for this batch.** The original discussion-only boundary was superseded by Shaun's implementation request.

### Proposed acceptance checks for later implementation

These acceptance checks were proposed during discussion; the October 14 implementation ledger below records their current evidence:

- An active incompatible operation prevents disk cleanup on that host, including from another tab or Details window.
- An active disk cleanup prevents an incompatible operation from starting on the same host.
- Independent hosts can still operate concurrently.
- Blocking is enforced at execution, not solely by disabling a button.
- The operator sees an understandable blocked-action reason.
- Staged packages and Vivre working files survive general cleanup.
- Completion and failure release ownership correctly; cancellation does not falsely mark a still-active remote operation as safely finished.
- The existing Server 2016 Clean up behavior remains intact.

### Related work

F09 reporting and service-recovery direction was agreed October 8, 2026; see its entry below. It fits alongside F01, but implementation grouping and authorization remain pending. Exact coordinator design, protected-directory handling, and cancellation behavior remain engineering details to resolve when implementation is authorized.

## F02 Reboot disclosure and approved targets

### Decision agreed October 8, 2026

Shaun rejected displaying long machine-name lists during operational fleet patching. He approved concise wording that communicates count, scope, and forced-reboot consequences, together with an internal safeguard binding execution to the approved target set.

1. Do not display a machine-name list in the reboot approval prompt.
2. State the number of machines and the actual scope clearly: selected machines, machines in the tab, or the reboot-pending subset of the install targets.
3. Explicitly say force reboot when that is what the action will perform, and briefly disclose forced application closure and loss of unsaved work.
4. Capture the target set represented by the prompt and pass that same set to execution. Do not re-read the current selection after approval or substitute newly selected machines.
5. Use the existing approval prompt wherever possible; this agreement does not require another confirmation window.

Example wording discussed:

- “Force-reboot 30 selected machines?”
- “Force-reboot 30 machines in this tab?”
- “Force-reboot the 5 reboot-pending machines out of 30 targets?”
- Consequence: “Applications will be forced closed; unsaved work will be lost.”

Wording must remain accurate for the actual action; an action that conditionally escalates must not be described as always forced immediately. Preserve the accepted maintenance-window reboot behavior and existing timing. No countdown, scheduling change, or Server 2016 cleanup change is agreed here.

**Implementation status: not started. Authorization: not granted.** Shaun's “Yes that's good” accepts this design direction only; the discussion-only boundary remains in effect. No implementation priority has been selected.

### Proposed acceptance checks for later implementation

- Small and large target sets use concise count/scope wording without machine-name lists.
- Forced-reboot prompts state force behavior and unsaved-work consequences accurately.
- A selection change while approval is pending cannot add or substitute execution targets.
- The install nudge uses the captured reboot-pending subset described by its prompt.
- Cancelling the prompt issues no reboot.
- Existing dispatch behavior and timing remain unchanged; no extra approval step is added without a demonstrated need and further agreement.

### Remaining scope boundary

The original F02 finding also discusses scheduled reboot and arbitrary script execution. This conversation has not separately settled how the agreed disclosure policy should apply to those entry points; do not infer authorization for script inspection, script restrictions, or additional scheduling dialogs. Resolve that applicability before implementing those paths.

## F03 SMB reboot dispatch reporting

### Decision agreed October 8, 2026

Shaun approved improving classification, operator wording, and production logging while preserving existing reboot dispatch and escalation behavior. He explicitly requires an independent red-team review before this work is accepted.

1. Distinguish an accepted reboot request, a definite failure to start it, and an uncertain outcome after a connection/service error. Service creation alone must not be treated as proof that shutdown executed.
2. Use concise status wording such as “Reboot sent — waiting for restart,” “Reboot failed — [reason],” or “Reboot not confirmed — checking for restart,” according to the evidence actually available.
3. For uncertain dispatch, use the existing offline/return and uptime verification mechanisms as appropriate to the entry point. If restart cannot be established, retain an Unverified outcome; do not turn uncertainty into success or a definite failure without evidence.
4. Log fallback start errors and error codes through the normal Release logging path, not only Debug.WriteLine. Distinguish dispatch diagnostics from cleanup diagnostics.
5. Do not introduce extra reboot attempts because a result is uncertain. Preserve existing authorized reboot and escalation rules, target scope, force flags, and timing.

**Implementation status: not started. Authorization: not granted.** Agreement covers intended behavior and the future review requirement only. No red-team review has been launched or completed for this change.

### Required independent red-team review

This requirement must be carried into any later implementation handoff. The reviewer must try to refute the classification and safety claims, not simply confirm the implementer's summary. For a Claude handoff, use Opus for independent refutation/review; for a Codex worker, use gpt-5.6-sol after checking callable availability, consistent with Shaun's model instructions.

Test or challenge at least these cases:

- A definite service-start failure must not become “reboot sent.”
- A service timeout or broken RPC connection during an actual reboot must not automatically become “reboot failed.” Classify only as strongly as the evidence supports; error code alone may be ambiguous.
- Service creation succeeds but shutdown never executes; later verification must not manufacture success.
- A fast reboot is not observed offline but is established by the existing uptime evidence; delayed shutdown and a host that never restarts must retain truthful outcomes.
- Both Force reboot and Reboot & verify consume the new result correctly, including existing already-in-progress and forced-escalation cases.
- An uncertain result cannot add a second send, widen targets, change force behavior, or alter the existing escalation schedule. Assert dispatch counts and parameters, not just displayed strings.
- Release logs retain the actual start failure/uncertainty and its code without leaking credentials; cleanup failure is not confused with dispatch failure.

Expected red-team output: attempted refutations, evidence and tests actually run, confirmed defects, unresolved uncertainties, and a scoped acceptance recommendation. The implementing agent's own review is not a substitute. Pure/fake tests cannot prove Windows SCM/RPC lifecycle behavior; any runtime behavior still unverified must be explicitly reported. Live reboot/canary testing requires separate explicit authorization and must not be inferred from this decision.

### Acceptance conditions for later implementation

- The three outcome classes reflect the evidence and remain distinct through the UI and normal logging.
- Focused tests cover definite start failure, ambiguous disconnect, eventual verified restart, and unverified completion for both reboot consumers.
- Existing reboot safety tests pass, with no weakened assertions or unapproved primitive-inventory changes; Desktop and solution builds are checked as appropriate.
- Independent red-team findings are resolved or explicitly deferred by Shaun, and remaining runtime validation limits are stated before any completion claim.

## F04 RDP persistence and recovery

### Decision agreed October 8, 2026

Shaun approved the following protections for saved Cross-Domain RDP connections and credentials:

1. Write new contents to a temporary file and replace the existing store only after the write succeeds, so an interrupted save does not truncate the existing file.
2. Retain a recoverable last-known-good backup of the connection tree and encrypted credential store.
3. Clearly report load failures and prevent an empty fallback tree from overwriting existing data after a failed read.
4. Preserve normal RDP operation and the existing Windows DPAPI password protection. Backups must retain encrypted credentials, not introduce plaintext passwords.

This is protection against data loss and failed recovery, not a finding that passwords are stored in plaintext. No production data-loss incident was reproduced during the review.

**Implementation status: not started. Authorization: not granted.** Shaun's agreement accepts this design direction only. No implementation priority has been selected.

### Proposed acceptance checks for later implementation

Use isolated disposable stores rather than Shaun's real connection or credential files:

- Simulated interrupted writes, disk/write failures, and replacement failures leave the previous valid store intact or recoverable.
- An existing unreadable or corrupt host-tree file cannot be overwritten by the empty UI fallback after an edit.
- Last-known-good backups remain usable and are not replaced with corrupt contents; a recoverable tree retains valid credential references.
- Credential files and backups remain DPAPI encrypted, with no passwords exposed in logs or recovery messages.
- Missing files on genuine first use are distinguished from failed reads of existing stores, allowing normal initial setup.
- Routine save/load and RDP connection behavior remain unchanged, and failures provide a clear recovery explanation.

These checks are proposed validation requirements and have not been run. Recovery interaction and consistency between the two stores must be resolved during authorized implementation. Removal of orphaned credentials is a separate watch item and is not added to F04's agreed scope by this decision.

## F05 Consistent and recoverable software package staging

### Decision agreed October 8, 2026

Shaun approved copying and verifying a replacement package separately before replacing the staged package, with the previous usable package preserved if copying fails.

1. Scope this change to **Stage software**, which copies an installer or folder. This is separate from the Server 2016 CU staging action.
2. Stage the incoming package into a unique temporary location and verify its intended contents before replacing the current package.
3. Successful folder replacement must contain the intended new package without obsolete files left from an earlier version. Apply consistent semantics across SMB, WinRM fallback, and local copy.
4. Preserve the previous usable package when transfer or verification fails, and clearly report the failure. Replacement failures must also have a defined recovery path rather than silently losing the old package.
5. Confine replacement and cleanup to the selected package's destination. Do not replace or delete the surrounding shared staging directory or unrelated packages. A single-file package must not cause replacement of its parent directory.
6. Preserve staging as a copy-only operation; do not automatically install or execute the package.

**Implementation status: not started. Authorization: not granted.** Shaun's agreement accepts this design direction only. No implementation priority has been selected.

### Proposed acceptance checks for later implementation

- Restage a folder version that removes or renames files; obsolete files are absent after successful replacement on each supported transport.
- Inject transfer interruption, cancellation, verification failure, and replacement failure using disposable destinations; the prior package remains usable or recoverable and the result accurately states what happened.
- A success result requires verification of the intended package contents, not just a transferred ZIP or aggregate file count.
- Unrelated files/packages outside the exact package destination remain unchanged; single-file staging preserves siblings.
- First-time staging still succeeds without an existing package, and staging never executes an installer.

These checks have not been run. Replacement/rollback mechanics, temporary-file cleanup, and handling of locked destination files remain engineering details to resolve when implementation is authorized. F11's memory/resource behavior is not automatically added to this implementation scope.

## F06 Unambiguous WhatsUp Gold device matching

### Decision agreed October 8, 2026

Shaun approved requiring an unambiguous device match and explicitly requires independent red-team review before acceptance.

1. Proceed with a WUG lookup or maintenance target only when one device identity matches unambiguously. Do not select a target merely because it is the first search result.
2. If multiple distinct devices qualify, skip that machine's maintenance change, clearly report Ambiguous, and let the operator specify a full name to resolve the identity. A full name must still be checked for uniqueness; it is not automatic permission to choose the first result.
3. Preserve the shared resolver for state reads and maintenance changes so they use the same identity rules.
4. Keep ambiguity separate from No device and Lookup error. Report partial fleet coverage honestly while permitting independently resolved targets to proceed under the existing workflow.

No evidence establishes that Shaun's current WUG inventory contains duplicate short names. The decision addresses the code's conditional targeting defect, not a confirmed wrong-device incident.

**Implementation status: not started. Authorization: not granted.** No implementation priority has been selected. The required red-team review has not started.

### Required independent red-team review

Carry this requirement into the future implementation handoff. Ask a reviewer independent of the implementer to try to refute the identity and mutation-safety claims. Use Claude Opus for independent review in a Claude handoff, or gpt-5.6-sol for a Codex review worker after verifying availability, consistent with Shaun's model instructions.

Challenge at least these cases using controlled fixtures:

- Two distinct devices share a short name across domains; reversing search-result order must not change which device is selected, and an ambiguous input must cause no maintenance mutation for that input.
- Duplicate display names, conflicting name/hostName fields, and multiple distinct device IDs matching a full name must not silently pick the first result.
- Repeated rows for the same device ID must be distinguished from genuinely different devices; invalid or missing identity fields must not result in a guessed mutation target.
- A unique fully qualified name resolves correctly where the short name is ambiguous. Ordinary unique short-name matching, case handling, trailing-dot normalization, and prefix-collision protection must keep working.
- IP fallback must preserve its exact-match ambiguity protection and must not silently override an unresolved name conflict without establishing a unique intended identity.
- Lookup failures remain errors, not absent devices. State reads and maintenance writes agree on the resolved identity.
- Mixed batches report ambiguous/skipped machines and actual coverage accurately; capture the exact device IDs sent to the mutation API and verify that no ambiguous device is changed.

Expected output: attempted refutations, tests/evidence actually obtained, defects and uncertainties, and a scoped acceptance recommendation. Do not mutate live WUG maintenance state without separate explicit authorization. Fixture success is not evidence that the current production inventory is duplicate-free.

### Acceptance conditions for later implementation

- No maintenance mutation occurs for unresolved ambiguous inputs, regardless of result ordering.
- Unique valid matches and established fallback behavior retain appropriate regression coverage; read and write paths use consistent resolution.
- Operator results clearly distinguish ambiguity, absence, lookup failure, and successful coverage, with a usable full-name retry path.
- Independent red-team findings are resolved or explicitly deferred by Shaun before acceptance, and untested live behavior is identified.

## F07 Recovery from cached Kerberos failure

### Decision agreed October 8, 2026

Shaun approved recovering from stale Kerberos failure decisions without restarting Vivre:

1. Provide a per-machine “Retry WinRM” action that performs a fresh, bounded connection check using the current connection settings and credentials rather than immediately rejecting the attempt from the cache.
2. Clear the cached failure for the relevant connection after successful verification so subsequent operations can use WinRM again. If the retry fails, report the actual failure and retain an honest degraded state.
3. Changing credentials must allow a fresh WinRM attempt rather than inheriting the previous identity's permanent rejection.
4. Preserve the avoidance of repeated slow attempts during routine monitoring; this agreement does not add continuous automatic retries on every poll.

**Implementation status: not started. Authorization: not granted.** No implementation priority has been selected. The action's precise UI placement and safe handling of concurrent cache updates remain engineering details for authorized implementation.

### Proposed acceptance checks for later implementation

- After a simulated Kerberos rejection followed by a repaired connection, Retry WinRM reaches the actual connection layer and restores WinRM use without an application restart.
- A failed, timed-out, or cancelled retry does not falsely mark the connection healthy; the operator receives an accurate result.
- A credential change permits a fresh attempt, without storing passwords or other secrets in cache keys or logs.
- A late result from an older connection attempt cannot overwrite the newer verified state; unrelated hosts and identities remain unaffected.
- Routine monitoring retains its existing protection against repeated slow connection failures.
- The retry is a connection check, not a replay of a prior script, install, or reboot operation.

These checks have not been run. No additional independent red-team requirement was explicitly requested for F07; the requirements already recorded for F03 and F06 remain in force.

## F08 Software absence versus query failure

### Decision agreed October 8, 2026

Shaun approved three distinct software-check results:

1. **Installed** — a matching product was found.
2. **Not installed** — the supported search completed successfully without finding a match.
3. **Could not check — [reason]** — the query failed or was incomplete, so absence cannot be established.

Do not suppress registry/provider/access failures and convert them into a negative installation result. Distinguish a legitimately absent registry path from a failed read. Preserve evidence of a product that was actually found; an unrelated incomplete read must not turn a verified match into “not installed.” These labels apply within the existing registry-based check's supported scope, not a claim to detect every possible installation method.

**Implementation status: not started. Authorization: not granted.** No implementation priority has been selected. This decision does not authorize automatic installation or remediation based on any result.

### Proposed acceptance checks for later implementation

- A matching product produces Installed with its available details.
- A successful empty search produces Not installed.
- Access denied, provider failure, and an incomplete relevant search without a match produce Could not check with a useful reason.
- A missing optional uninstall registry path is distinguished from an unreadable path; supported 32-bit and 64-bit searches retain appropriate behavior.
- WinRM and existing DCOM fallback preserve the same distinction between absence and failure.
- UI results and logging retain the distinction rather than collapsing unknown into false. Any reported service-state lookup failure must also remain distinguishable from a verified missing service.

These checks have not been run. Existing matching scope and transport fallback should be preserved while correcting result honesty.

## F09 Truthful disk cleanup outcomes

### Decision agreed October 8, 2026

Shaun approved accurate outcomes for **Free up disk space in the machine Details window**, separate from Server 2016 component cleanup:

1. Report **Completed**, **Partially completed**, or **Failed** according to the actual results, with a concise explanation of failed steps.
2. Track the component results for temporary-file cleanup, update-cache cleanup, recycle-bin cleanup, and Windows Update service handling rather than returning unconditional success.
3. Explicitly verify Windows Update service recovery when cleanup stops it. A restart failure must be visible and must not be reported as a fully successful cleanup.
4. Treat ordinary locked temporary files as skipped items with proportionate reporting rather than presenting every skipped file as a catastrophic operation failure.
5. Report reclaimed space only when measured reliably; identify unavailable measurements instead of inventing a successful result.

**Implementation status: implemented and locally validated; not deployed. Authorization: granted October 14, 2026 for this batch.** Implemented together with F01. The Server 2016 component-cleanup implementation is unchanged.

### Proposed acceptance checks for later implementation

- Successful component actions and verified service recovery produce a truthful completed result.
- A mix of successful cleanup and substantive failures produces a partial result naming the failed steps; no useful completion produces an appropriate failed result.
- Simulated service-stop or restart failures are surfaced distinctly, with the observed final service state when it can be established. An unknown state is not described as verified recovery.
- Ordinary locked files are reported as skipped without hiding broader access-denied, provider, or service errors.
- Failure and cancellation paths attempt appropriate service recovery and do not silently leave a service stopped by cleanup; original service-state handling must be explicitly resolved during implementation.
- UI and normal logs retain the outcome distinctions, and the F01 coordination and protected-package behavior remain intact.

Current verification is recorded in the October 14 ledger below. Tests use controlled fixtures; live service manipulation or deletion remains outside this batch.

## F10 Preference write ordering and normal shutdown

### Decision agreed October 8, 2026

Shaun approved reliable persistence of the latest personal preferences:

1. Use one ordered save queue, optionally coalescing superseded pending snapshots, so an older save cannot overwrite a newer preference change.
2. During normal shutdown, briefly finish pending saves with a bounded wait rather than discarding outstanding background work or hanging indefinitely.
3. Clearly report save failures or an incomplete shutdown flush instead of silently implying the latest settings were persisted.
4. Preserve existing atomic replacement and unrelated/future settings keys. Keep ordinary interaction responsive.

Scope is personal app preferences. The separate shared operational settings concurrency issue is not included. A normal-exit flush cannot guarantee persistence after forced process termination or power loss; retain the existing protection against corrupting the previous valid file.

**Implementation status: not started. Authorization: not granted.** No implementation priority has been selected. Exact flush timeout and failure interaction remain engineering details for authorized implementation.

### Proposed acceptance checks for later implementation

- Deliberately delay an earlier save while later changes arrive; after completion, disk contains the latest accepted settings snapshot.
- Rapid changes from different preference callers preserve their latest intended values and do not create competing write queues.
- Change a preference and immediately exit normally; the final value is restored at the next launch when storage is available.
- Simulate write failure or a stalled writer; shutdown remains bounded and failure is reported through a usable path before logging/UI teardown.
- Atomic file integrity and preservation of unknown settings keys remain intact; UI work does not block on routine disk writes.

These checks have not been run. This decision does not authorize changes to remote operations or their shutdown/cancellation behavior.

## F11 Bounded WinRM staging and temporary file cleanup

### Decision agreed October 8, 2026

Shaun approved limiting memory use and cleaning up temporary transfer files in **Stage software's WinRM fallback**, separate from Server 2016 CU staging:

1. Read, encode, and send the package in bounded chunks instead of holding the entire compressed package and encoded copy in memory simultaneously.
2. Preserve transfer integrity verification; bounded memory must not weaken detection of missing or corrupted data.
3. Clean up that transfer's temporary files after success, failure, or cancellation when the destination can be reached safely.
4. If the destination is unreachable, retain enough transfer ownership/path information for safe later cleanup, and report that cleanup remains pending rather than claiming completion.
5. Cleanup must target only verified files owned by that transfer, never another active transfer, the usable staged package, or unrelated files.

**Implementation status: not started. Authorization: not granted.** No implementation priority has been selected. Coordination with F05's verified package replacement is required if both are implemented; this decision does not authorize changes to the Server 2016 CU lane or automatic package execution.

### Proposed acceptance checks for later implementation

- Measure peak memory with progressively larger, poorly compressible payloads; buffers remain bounded rather than scaling with the full payload or encoded string.
- Corrupted, missing, or incomplete chunks fail integrity verification and cannot produce a successful staging result.
- Success, transfer failure, finalization failure, and cancellation clean up owned temporary files where reachable, without deleting the prior usable package protected by F05.
- Unreachable-host cleanup is retained as pending and can be retried later against the exact transfer-owned paths; active or unrelated transfers are preserved.
- Concurrent transfers use distinct ownership identifiers and do not overwrite or clean up each other's files.
- Existing SMB-first behavior and copy-only staging remain intact.

These checks have not been run. Retry scheduling, persistence of pending cleanup, and concurrency limits remain engineering details for authorized implementation; no background automation or live cleanup has been started by this decision.

## F12 Concurrent Server 2016 staging files

### Decision October 8, 2026

**Skipped at Shaun's request. Excluded from the current implementation queue.** After clarification that this finding concerns separate Vivre instances staging against the same server, Shaun said “skip.”

Do not implement the proposed target-wide ownership guard or per-run CU extraction changes under this review's agreed work. This decision does not establish that the finding is fixed, disproven, or impossible in the environment; no statement about actual multiple-instance use was provided. Revisit only if Shaun explicitly requests it.

**Implementation status: not started. Authorization: not granted.** The original review remains unchanged as the evidence record.

## How to use this log

For each later discussion, record the finding ID, operating context, accepted or rejected direction, scope exclusions, agreed priority if any, and explicit implementation authorization. When work is authorized and completed, add the actual changes and verification evidence; do not mark an item fixed based only on discussion or proposed tests.

Discussion handoff preference confirmed October 8, 2026: after recording each decision, concisely introduce the next finding, the recommended approach, and why it matters.

## F01/F09 capability and acceptance ledger — October 14, 2026

Live implementation checkpoint; this is not a release or deployment claim.

| Capability | Current status | Required evidence |
|---|---|---|
| Shared same-host conflict guard across tabs and Details windows, both directions | Implemented and tested locally | Concurrent service-entry tests, different-host independence |
| Keep ownership while work remains active or uncertain after cancellation | Implemented and tested locally | Delayed work and cancellation tests |
| Preserve Vivre packages and custom staged destinations across restart | Implemented and tested locally | Protected-path fixtures and persistent registry failure tests |
| Completed / partially completed / failed with useful component reasons | Implemented and tested locally | Executed cleanup script with mocked Windows boundaries |
| Restore and verify original Windows Update service state | Implemented and tested locally | Stop/restart/query failure fixtures; no live service mutation |
| Locked files proportionate; missing measurements remain unknown | Implemented and tested locally | Locked-file, access-denied, measurement fixtures |
| UI/help/logs preserve truthful results | Implemented and tested locally | Source review and Desktop compilation |
| Server 2016 component cleanup behavior preserved | Unchanged; diff reviewed and regression suite passed | Diff review and existing regression tests |
| Production-server validation | Not performed; live destructive operations not authorized | Separate explicit authorization and disposable target required |

F03 and F06 retain their required independent red-team review when their implementation batches are reached. F12 remains excluded.

### Review corrections and validation scope

Independent source review caught and corrected a stop-success/status-query-failure recovery hole,
missing uncertainty propagation on partial cleanup, unsafe registry read fallback, and a PowerShell
5.1-incompatible path API. Main-agent integration tests exercise both conflict directions at real
service entry points with fake transports, independent Details callers, cancellation, same-host
aliases, different-host independence, and uncertain recovery. No assertion or production gate was
weakened.

The first broad test run was invalidated by a new fixture mutating process environment variables,
which disrupted unrelated child PowerShell tests. Fixtures now substitute script-local paths and
never change the test process environment. The final fresh-process regression suite passed (1,237 tests).

One earlier deployment-test pass created `C:\ProgramData\Vivre\protected-staging-paths.json` before
its registry dependency was redirected to disposable test storage. Main-agent inspection found only
synthetic fixture paths (NYC-FP1 and VivreStageTest/VivreStageDest paths). Automatic approval review
rejected guarded deletion because the machine-wide location can contain operational protection
state. The file remains untouched pending user approval. All current deployment fixtures use
isolated, self-cleaning temporary registries.

Operational limits: guards are local to this Vivre process, normalize case/trailing dots/local aliases,
and cannot unify arbitrary DNS aliases or control other applications/schedulers. Custom destinations
from older builds or other controller machines are not discoverable automatically. Some patch/stage
errors conservatively retain a blocker until the operator checks the server is idle and restarts Vivre.
No live server testing, release, or deployment has occurred. Commit and push were subsequently authorized by Shaun on October 14, 2026.

### Final local verification — October 14, 2026

- Main agent independently ran `dotnet test source/Vivre.slnx --no-restore --verbosity minimal`:
  **1,237 passed, zero failed, zero skipped** (2m14s).
- After final cleanup refinements, main agent ran the affected Remediation, Operations, and
  DeploymentServiceTests against the final build: **47 passed, zero failed, zero skipped**.
  This includes real temporary-file deletion and a real exclusive file lock, injected access/service
  failures, persistence and concurrency checks, and a Windows PowerShell 5.1 child-process fixture.
- Main agent ran `dotnet build source/Vivre.slnx --no-restore --verbosity minimal` on the final sources:
  **success, zero errors, one warning**: the configured agent-signing certificate is absent from
  CurrentUser\My, so the local build's agent is unsigned. No release or deployment was attempted.
- `git diff --check` passed. Main agent read all changed implementation files, reviewed changed tests,
  and confirmed no changes to FullPackageLcuLane, WuaUpdateLane, or the UpdateAgent source.
- Independent review corrections were verified in code; the remaining reparse-skip disclosure concern
  was corrected by reporting protected/reparse skip counts. Reparse traversal boundaries were reviewed
  in source; no live filesystem links or live server services were used to validate them.
- Interactive WPF operation on a live server remains untested. Local compilation and mocked service
  fixtures do not establish a production canary result.

The F01/F09 implementation batch is locally complete. The synthetic ProgramData registry cleanup
remains pending approval because automatic review rejected its deletion. Shaun subsequently authorized committing and pushing this batch, with short conventional commit subjects and no attribution trailers. See Git history for delivery.

**Recommended next batch: F02.** Show concise target count/scope and force consequences while retaining
exactly the approved target snapshot. Avoid listing 30 machine names; preserve existing timing and
reboot behavior. F03 follows with its required independent red-team review. F12 remains skipped.
