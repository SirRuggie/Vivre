# Vivre review decisions and implementation queue

Living decision log for the [October 5, 2026 design review](C:/src/Vivre/docs/vivre-design-review-2026-10-05.md). The original review is a point-in-time record and stays unchanged. This log records Shaun's decisions as each finding is discussed.

Implementation decisions recorded October 14, 2026; status reconciliation October 20, 2026.

**Reading order:** the decision register and later capability/acceptance ledgers supersede earlier proposal/authorization wording. In particular, F02's original exclusion of scheduled/script reboot approval was superseded by F38 in the [audit implementation ledger](vivre-audit-implementation-2026-10-14.md). F07 is transport-cache recovery; F10 is preference write ordering. F12 remains explicitly skipped. See the [current backlog](vivre-backlog.md) for remaining gaps and deferred work.

## Authorization and status

**Implementation authorized October 14, 2026.** Shaun asked to start tackling the agreed findings. F01 + F09 (Details > Free up disk space) are implemented, tested, committed, and pushed; the Server 2016 component-cleanup workflow is preserved. F02 is also implemented, locally validated, committed, and pushed. F03 is implemented and locally validated, including independent red-team review; delivery is recorded in Git history. F04 through F11 are now implemented and locally validated with delivery recorded in Git history; F12 remains explicitly skipped. Shaun authorized commits and pushes with short conventional messages. Deployment and live destructive validation remain outside scope.

The original report's severity ratings remain review assessments, not an agreed implementation order. Record a priority change only when Shaun makes that decision. Keep unreviewed findings pending rather than treating silence as acceptance.

## Decision register

| Finding | Subject | Discussion status | Implementation authorization |
|---|---|---|---|
| F01 | Details window disk cleanup conflicts and package preservation | Implemented and locally validated | Committed and pushed |
| F02 | Reboot disclosure and approved targets | Implemented and locally validated | Committed and pushed |
| F03 | SMB reboot dispatch reporting | Implemented and locally validated; independent red-team accepted | Authorized commit and push |
| F04 | RDP persistence and recovery | Implemented; independent review accepted | Authorized October 14, 2026 |
| F05 | Consistent and recoverable package staging | Implemented; independent review accepted | Authorized October 14, 2026 |
| F06 | Ambiguous WUG device names | Implemented; independent red-team accepted; 1,340 tests passed | Authorized October 14, 2026 |
| F07 | Recovery from cached Kerberos failure | Implemented and locally verified; 1,353 tests passed | Authorized October 14, 2026 |
| F08 | Software absence versus query failure | Implemented; independent red-team accepted; 1,392 tests passed | Authorized October 14, 2026 |
| F09 | Truthful disk cleanup outcomes | Implemented and locally validated | Committed and pushed |
| F10 | Preference write ordering and exit | Implemented; independent review accepted; 1,406 tests passed | Authorized October 14, 2026 |
| F11 | WinRM staging memory and temporary files | Implemented; red-team fixes integrated; 1,441 tests passed (validation limits below) | Authorized commit and push |
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

**Implementation status: implemented and locally validated. Authorization: Shaun's request to tackle the agreed findings, followed by “ok next?”.** Scope: Force reboot, Reboot & verify, and the install pending-reboot nudge; scheduling and scripts remain outside this batch.

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

**Implementation status: implemented and locally validated; not deployed. Authorization: Shaun said “Start” on October 14, 2026.** Independent red-team review completed with scoped acceptance after corrections; no live reboot or deployment was performed.

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

**Implementation status: implemented and reviewed locally. Authorization: Shaun said “next” after F03 delivery on October 14, 2026.** Validation uses isolated dummy stores; existing user connection and credential files are not test fixtures.

### Proposed acceptance checks for later implementation

Use isolated disposable stores rather than Shaun's real connection or credential files:

- Simulated interrupted writes, disk/write failures, and replacement failures leave the previous valid store intact or recoverable.
- An existing unreadable or corrupt host-tree file cannot be overwritten by the empty UI fallback after an edit.
- Last-known-good backups remain usable and are not replaced with corrupt contents; a recoverable tree retains valid credential references.
- Credential files and backups remain DPAPI encrypted, with no passwords exposed in logs or recovery messages.
- Missing files on genuine first use are distinguished from failed reads of existing stores, allowing normal initial setup.
- Routine save/load and RDP connection behavior remain unchanged, and failures provide a clear recovery explanation.

Local acceptance evidence is recorded in the F04 ledger below. The stores recover separately and may represent different saves; missing credential references are reported explicitly. Removal of orphaned credentials remains outside F04. Live RDP and physical power-loss behavior were not exercised.

## F05 Consistent and recoverable software package staging

### Decision agreed October 8, 2026

Shaun approved copying and verifying a replacement package separately before replacing the staged package, with the previous usable package preserved if copying fails.

1. Scope this change to **Stage software**, which copies an installer or folder. This is separate from the Server 2016 CU staging action.
2. Stage the incoming package into a unique temporary location and verify its intended contents before replacing the current package.
3. Successful folder replacement must contain the intended new package without obsolete files left from an earlier version. Apply consistent semantics across SMB, WinRM fallback, and local copy.
4. Preserve the previous usable package when transfer or verification fails, and clearly report the failure. Replacement failures must also have a defined recovery path rather than silently losing the old package.
5. Confine replacement and cleanup to the selected package's destination. Do not replace or delete the surrounding shared staging directory or unrelated packages. A single-file package must not cause replacement of its parent directory.
6. Preserve staging as a copy-only operation; do not automatically install or execute the package.

**Implementation status: implemented and independently reviewed locally. Authorization: Shaun said “ok continue to next” after F04 delivery on October 14, 2026.** Validation uses disposable dummy packages and destinations; no live server staging or package execution is authorized.

### Proposed acceptance checks for later implementation

- Restage a folder version that removes or renames files; obsolete files are absent after successful replacement on each supported transport.
- Inject transfer interruption, cancellation, verification failure, and replacement failure using disposable destinations; the prior package remains usable or recoverable and the result accurately states what happened.
- A success result requires verification of the intended package contents, not just a transferred ZIP or aggregate file count.
- Unrelated files/packages outside the exact package destination remain unchanged; single-file staging preserves siblings.
- First-time staging still succeeds without an existing package, and staging never executes an installer.

Local acceptance evidence appears in the F05 ledger below. Live transport, power-loss, and multi-process behavior remain unverified. F11's bounded-memory transfer and interrupted-chunk cleanup remain separate; F05 restores normal finalized-ZIP cleanup and owns its new candidate/extraction paths.

## F06 Unambiguous WhatsUp Gold device matching

### Decision agreed October 8, 2026

Shaun approved requiring an unambiguous device match and explicitly requires independent red-team review before acceptance.

1. Proceed with a WUG lookup or maintenance target only when one device identity matches unambiguously. Do not select a target merely because it is the first search result.
2. If multiple distinct devices qualify, skip that machine's maintenance change, clearly report Ambiguous, and let the operator specify a full name to resolve the identity. A full name must still be checked for uniqueness; it is not automatic permission to choose the first result.
3. Preserve the shared resolver for state reads and maintenance changes so they use the same identity rules.
4. Keep ambiguity separate from No device and Lookup error. Report partial fleet coverage honestly while permitting independently resolved targets to proceed under the existing workflow.

No evidence establishes that Shaun's current WUG inventory contains duplicate short names. The decision addresses the code's conditional targeting defect, not a confirmed wrong-device incident.

**Implementation status: implemented and independently reviewed locally. Authorization: Shaun explicitly said “start F06” on October 14, 2026.** Independent refutation accepted the final implementation. Validation used controlled dummy inventory and mutation stubs; no live WUG maintenance changes or deployment.

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

**Implementation status: implemented and locally verified. Authorization: Shaun said “Next” after F06 delivery on October 14, 2026.** Includes the per-machine Retry WinRM action, connection-aware rejection caching, credential-change recovery, and stale-result protection. Validation used controlled test doubles; no live server operation or deployment was performed.

### Proposed acceptance checks for later implementation

- After a simulated Kerberos rejection followed by a repaired connection, Retry WinRM reaches the actual connection layer and restores WinRM use without an application restart.
- A failed, timed-out, or cancelled retry does not falsely mark the connection healthy; the operator receives an accurate result.
- A credential change permits a fresh attempt, without storing passwords or other secrets in cache keys or logs.
- A late result from an older connection attempt cannot overwrite the newer verified state; unrelated hosts and identities remain unaffected.
- Routine monitoring retains its existing protection against repeated slow connection failures.
- The retry is a connection check, not a replay of a prior script, install, or reboot operation.

Local acceptance checks passed in focused routing/recovery tests and the full regression suite. Main reviewed the implementation and corrected concurrency/cancellation handling. No separate independent red-team acceptance or live Kerberos validation is claimed for F07.

## F08 Software absence versus query failure

### Decision agreed October 8, 2026

Shaun approved three distinct software-check results:

1. **Installed** — a matching product was found.
2. **Not installed** — the supported search completed successfully without finding a match.
3. **Could not check — [reason]** — the query failed or was incomplete, so absence cannot be established.

Do not suppress registry/provider/access failures and convert them into a negative installation result. Distinguish a legitimately absent registry path from a failed read. Preserve evidence of a product that was actually found; an unrelated incomplete read must not turn a verified match into “not installed.” These labels apply within the existing registry-based check's supported scope, not a claim to detect every possible installation method.

**Implementation status: implemented and locally validated; not deployed. Authorization: granted October 14, 2026 by Shaun’s “next”.** This decision does not authorize automatic installation or remediation based on any result.

### Acceptance checks

- A matching product produces Installed with its available details.
- A successful empty search produces Not installed.
- Access denied, provider failure, and an incomplete relevant search without a match produce Could not check with a useful reason.
- A missing optional uninstall registry path is distinguished from an unreadable path; supported 32-bit and 64-bit searches retain appropriate behavior.
- WinRM and existing DCOM fallback preserve the same distinction between absence and failure.
- UI results and logging retain the distinction rather than collapsing unknown into false. Any reported service-state lookup failure must also remain distinguishable from a verified missing service.

Local validation and independent review results are recorded in the ledger below. Existing matching scope and transport fallback should be preserved while correcting result honesty.

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

**Implementation status: implemented and locally validated; not deployed. Authorization: granted October 14, 2026 by Shaun’s “Next”.** Normal close waits up to three seconds; failure or timeout is logged and displayed before the window closes.

### Acceptance checks

- Deliberately delay an earlier save while later changes arrive; after completion, disk contains the latest accepted settings snapshot.
- Rapid changes from different preference callers preserve their latest intended values and do not create competing write queues.
- Change a preference and immediately exit normally; the final value is restored at the next launch when storage is available.
- Simulate write failure or a stalled writer; shutdown remains bounded and failure is reported through a usable path before logging/UI teardown.
- Atomic file integrity and preservation of unknown settings keys remain intact; UI work does not block on routine disk writes.

Local acceptance evidence and the interactive UI validation limit appear in the F10 ledger below. This decision does not authorize changes to remote operations or their shutdown/cancellation behavior.

## F11 Bounded WinRM staging and temporary file cleanup

### Decision agreed October 8, 2026

Shaun approved limiting memory use and cleaning up temporary transfer files in **Stage software's WinRM fallback**, separate from Server 2016 CU staging:

1. Read, encode, and send the package in bounded chunks instead of holding the entire compressed package and encoded copy in memory simultaneously.
2. Preserve transfer integrity verification; bounded memory must not weaken detection of missing or corrupted data.
3. Clean up that transfer's temporary files after success, failure, or cancellation when the destination can be reached safely.
4. If the destination is unreachable, retain enough transfer ownership/path information for safe later cleanup, and report that cleanup remains pending rather than claiming completion.
5. Cleanup must target only verified files owned by that transfer, never another active transfer, the usable staged package, or unrelated files.

**Implementation status: implemented and locally validated October 14, 2026. Authorization: granted by Shaun’s “continue”.** This implementation integrates with F05's verified package replacement; this decision does not authorize changes to the Server 2016 CU lane or automatic package execution.

### Proposed acceptance checks for later implementation

- Measure peak memory with progressively larger, poorly compressible payloads; buffers remain bounded rather than scaling with the full payload or encoded string.
- Corrupted, missing, or incomplete chunks fail integrity verification and cannot produce a successful staging result.
- Success, transfer failure, finalization failure, and cancellation clean up owned temporary files where reachable, without deleting the prior usable package protected by F05.
- Unreachable-host cleanup is retained as pending and can be retried later against the exact transfer-owned paths; active or unrelated transfers are preserved.
- Concurrent transfers use distinct ownership identifiers and do not overwrite or clean up each other's files.
- Existing SMB-first behavior and copy-only staging remain intact.

Implementation evidence and remaining limitations are recorded in the F11 ledger below. Pending remote cleanup retries on a later explicit staging request; no background automation or live cleanup has been started.

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
state. Shaun subsequently approved removal conditional on the file containing only dummy data. Every host/path entry was rechecked against the exact observed test entries, and only the JSON registry file was deleted. No listed package or directory was deleted. All current deployment fixtures use
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

The F01/F09 implementation batch is locally complete. The synthetic ProgramData registry was removed after Shaun approved cleanup and every entry was verified as dummy test data. Shaun authorized committing and pushing this batch, with short conventional commit subjects and no attribution trailers. See Git history for delivery.

**Recommended next batch: F02.** Show concise target count/scope and force consequences while retaining
exactly the approved target snapshot. Avoid listing 30 machine names; preserve existing timing and
reboot behavior. F03 follows with its required independent red-team review. F12 remains skipped.

## F02 capability and acceptance ledger

| Capability | Status | Required evidence |
|---|---|---|
| Concise count/scope, no machine-name list | Implemented and verified locally | Small/large prompt tests; three UI call sites reviewed |
| Explicit force consequences; conditional wave escalation accurate | Implemented and verified locally | Prompt tests and unchanged dispatch/timing diff |
| Same captured targets after approval; cancellation dispatches nothing | Implemented and verified locally | Delayed-approval tests with selection and pending-state changes |
| Help reflects confirmed behavior | Implemented and verified locally | Source review and Desktop build |
| Live reboot validation | Not performed; outside authorized local validation | No real reboot needed for approval tests |

### F02 verification and delivery

- Main agent built the full solution: **zero errors, one existing missing agent-signing certificate
  warning**. No deployment or live reboot occurred.
- Main agent ran the full built solution test suite: **1,248 passed, zero failed, zero skipped**
  (2m15s). The focused approval/inventory run also passed **12/12**.
- Behavioral tests cover 1/30/300-target prompts without name lists, selected/tab pending scope,
  selection changes during delayed approval, pending-state changes, cancellation, empty sets,
  renamed-host rejection, local-host warning capture, and existing conditional-wave wording.
- Main source review and independent read-only review confirmed that all three entry points dispatch
  captured rows. Force and wave methods require explicit targets; neither can reread selection.
- Dispatch/wave implementations and the pinned reboot manifest are unchanged. The primitive-inventory
  test passes without changing expected counts. Help and repository guidance reflect the new prompts.
- Interactive WPF dialog operation and live server rebooting were not exercised; approval tests use
  fake dispatch callbacks. This validation does not claim a production canary.

**Next: F03 — truthful SMB reboot dispatch reporting.** Distinguish accepted, failed, and uncertain
requests so a service-creation success is not mistaken for a confirmed reboot. Preserve dispatch,
escalation, and timing. Independent red-team review remains mandatory for F03.


## F03 capability and acceptance ledger

Authorized October 14, 2026. This batch changes dispatch reporting and verification integration, not reboot targets, primitives, force flags, or the existing escalation schedule.

| Capability | Status | Required evidence |
|---|---|---|
| Accepted, definite failure, uncertain start outcomes | Implemented and tested locally | Fake SCM start/error cases; creation alone cannot prove a restart |
| Release diagnostics distinguish start from cleanup | Implemented and tested locally | Numeric error assertions and trace review |
| Wave and Force use existing restart evidence | Implemented and tested locally | Offline/return, uptime collapse, and no-restart tests |
| Existing sends, force flags, timings, approvals preserved | Verified locally | Dispatch count/parameter tests and unchanged inventory gate |
| Independent refutation | Completed; scoped acceptance | Separate reviewer rebuilt and passed 75 focused tests; confirmed defects corrected |
| Real Windows SCM/RPC lifecycle and live reboot | Not performed; outside authorized local validation | Requires separate authorization for a live canary |

### F03 review checkpoint

Main-agent source review required persistent restart-evidence gating for uncertain Force requests:
a clean pending-state probe must not claim reboot completion, including after watch expiry. The
independent reviewer additionally found that an older fast watcher could verify a newer uncertain
request using the older baseline. Per-attempt watcher ownership and an overlap regression are
implemented and tested before acceptance. Review also caught reversed dispatch-completion ordering: older progress, results, and catches now defer to the current attempt. These checks do not authorize another reboot or alter dispatch timing.

The classification uses a narrow list of definite pre-start rejections; unknown service/RPC errors
remain uncertain. Microsoft StartServiceW documentation confirms that timeout 1053 alone cannot
prove whether the process ran. Real SCM/RPC behavior remains untested locally.


### F03 final local verification and delivery

- Main agent independently built the full solution including Desktop: **zero errors, one existing
  missing agent-signing certificate warning**. The local agent is unsigned; no release was created.
- Main agent ran the full regression suite after behavioral corrections: **1,273 passed, zero failed,
  zero skipped**. After a final wording-only correction for unreadable uptime, the full solution was
  rebuilt and all **75 focused F03/wave/Force/inventory tests passed** again.
- Independent gpt-5.6-sol red-team review attempted to refute definite versus uncertain starts,
  1053/RPC errors, non-Win32 HRESULT collisions, 1115/1191 behavior, created-but-never-executed services,
  uptime rescue, no restart, target/force/count preservation, cleanup diagnostics, and overlapping
  requests. It rebuilt current sources, passed **75/75** focused tests, and reported no remaining
  confirmed defect. Main agent independently reviewed the changed source and tests.
- All new SCM tests use injected dummy services/hosts; no Windows service or remote reboot is invoked.
  Existing tests were extended without removing or weakening assertions. The primitive inventory and
  manifest are unchanged. `git diff --check` passed.
- Help, README, architecture guidance, patching documentation, and Unreleased notes reflect the change.
  The old Force help text claiming a machine-name list was also corrected to match F02's count/scope prompt.
- Interactive WPF behavior and actual Windows SCM/RPC start/disconnect lifecycle remain unverified.
  Fake-service tests do not establish a production canary. Live reboot validation and deployment require
  separate authorization. Shaun's standing instruction authorizes this local fix to be committed and pushed
  with a short conventional subject and no attribution trailers.

**Recommended next: F04 — RDP persistence and recovery.** Use safe replacement writes, recoverable
backups, and load-failure protection so a failed save or corrupt load cannot erase saved connections.
Preserve existing RDP behavior and DPAPI encryption. F12 remains skipped.


## F04 capability and acceptance ledger

| Capability | Status | Required evidence |
|---|---|---|
| Safe replacement saves and recoverable backups | Implemented and verified locally | Interrupted write and replacement-failure tests in temporary stores |
| Failed reads cannot overwrite existing stores with empty defaults | Implemented and verified locally | Corrupt/unreadable primary and backup cases; UI edit guard |
| Credential backups remain encrypted and references recoverable | Implemented and verified locally | DPAPI round trip, backup contents, tree/reference consistency |
| Clear failure/recovery reporting and unchanged normal RDP operation | Implemented and verified locally | Caller review, help update, Desktop build |
| Local verification | Completed; independent review accepted | 51 focused RDP tests; 1,292 full-suite tests; full Desktop build |
| Live RDP session / power-loss validation | Outside local validation | No real credentials, sessions, or user stores used |


### F04 review checkpoint

Recovery keeps host and credential IDs intact, but the two backups can represent different saves.
A missing recovered login must be reported; a blank edit must not silently discard its reference or
substitute a parent login. Recovery may require re-entering that login. This is not a paired database
transaction across both files.

Review corrections implemented and independently accepted: unique temporary files; preserve a valid backup when
the primary is missing and publishing its replacement fails; validate save inputs; return detached
credential records; block edits before credential changes when the host tree failed to load; retain
recovery notices through a first-operation credential save; and reject corrupt DPAPI blobs before
allowing them to rotate over a usable backup. Tests remain isolated from real user stores.


### F04 final local verification and delivery

- Main agent independently reviewed the changed source and tests, built the full solution including
  Desktop, and ran the final regression suite: **1,292 passed, zero failed, zero skipped**. Build:
  **zero errors, one existing missing agent-signing certificate warning**; local agent remains unsigned.
- Independent refutation review accepted the final scope after corrections, independently built the
  solution, and passed **51/51 focused RDP tests**. No remaining confirmed defect was reported.
- Tests use disposable dummy stores and credentials, including real Windows DPAPI round trips and a
  real locked-primary replacement failure. They verify backup preservation, failed-load save blocking,
  input validation, cache isolation, ciphertext validation, and explicit missing credential references.
- Host-tree and credential saves remain separate. If the login saves first and the tree save fails,
  Activity identifies the durable login change and unsaved tree edit. Recovery can require re-entering
  a login absent from an older credential backup; no silent blank or inherited substitution occurs.
- Help, README, repository guidance, and Unreleased notes are updated. `git diff --check` passed.
  RDP session controls, scaling, DPAPI entropy, reboot dispatch, and the reboot inventory are unchanged.
- No real AppData stores, live RDP sessions, physical power-loss, ACL-denial, or multi-process stress
  tests were used. This is local persistence validation, not a production canary or deployment.
- Delivery follows Shaun's standing authorization to commit and push validated changes with a short
  conventional subject and no attribution trailers.

**Recommended next: F05 — consistent and recoverable software package staging.** Verify a replacement
before publishing it and preserve the previous usable package if copying or replacement fails. Scope
is Stage software only, including local, SMB, and WinRM paths; Server 2016 CU staging and automatic
installation remain separate. F12 remains skipped.


## F05 capability and acceptance ledger

| Capability | Status | Required evidence |
|---|---|---|
| Verified replacement across local, SMB, and WinRM staging | Implemented and tested locally | Exact content/hash verification before publication; generated-script execution with dummy files |
| Prior package preserved through transfer/verification failure | Implemented and tested locally | Failure and cancellation fixtures with old-package byte checks |
| Replacement failure has truthful recovery | Implemented and tested locally | Rename/publish failure, rollback and recovery-path checks |
| Package boundary and sibling isolation | Implemented and tested locally | Single-file rename, folder stale-file removal, sibling preservation and path-boundary tests |
| Existing operational constraints preserved | Implemented and tested locally | Copy-only, credentials, cleanup coordination, protected recovery paths; no Server 2016 CU changes |
| Independent refutation and full regression | Completed; independent review accepted | Separate review, main source review, solution build and 1,311 passing tests |
| Live SMB/WinRM server validation | Outside local validation | No production target or actual package deployment used |


### F05 review checkpoint

Review requires exact single-file destination semantics, hidden files and empty folders in verification,
no deletion of another actor's destination during rollback, local-alias staging serialization, and
reparse-path rejection before recursive operations. Protected recovery siblings must be registered
before mutation. The UI now retains detailed success and cancellation messages rather than hiding
cleanup/recovery guidance. Executable failure tests and independent review now cover these corrections; full-suite evidence follows.


### F05 verification limits and operational behavior

- Main and independent reviewer each passed **26/26 focused deployment tests** and built the full
  solution including Desktop with **zero errors** and the existing missing signing-certificate warning.
- Real disposable filesystem cases cover changed source bytes, locked destination, candidate/backup
  collisions, cancellation before publication, exact renamed-file and folder replacement, unrelated
  siblings, and empty directories. Actual Windows PowerShell 5.1 runs exercise the generated scripts,
  including ZIP file/folder verification and cleanup, hash mismatch, locked target, candidate collision,
  injected publication failure with successful rollback, and injected rollback failure with a named backup.
- The old package moves to a unique sibling only after candidate verification. Publication uses an
  exact rename, never folder merge. Failed rollback does not delete a newly appeared destination;
  it retains the old package at the reported backup path. New working/recovery paths are registered
  before mutation. Post-publication cleanup warnings remain visible in the row and Activity.
- Once dispatched, SMB publication and WinRM finalization are non-cancellable: Stop can end the visible
  sweep while in-flight work finishes and later reports its outcome. These commands have no separate
  execution timeout; a hung filesystem/remote command may retain ownership indefinitely. Keep this
  runtime limit visible; do not claim Stop proves that remote work stopped. Unknown results contain
  recovery locations and never trigger another publication through WinRM.
- No live SMB/WinRM server, EDR behavior, ACL-denial, process crash, physical power loss, or separate
  concurrent Vivre processes were exercised. The process-local lock normalizes known host aliases
  and serializes an exact target, not arbitrary DNS aliases or cross-process/overlapping destinations.
- No Server 2016 CU lane, installer execution, reboot primitive, or reboot inventory change is included.
  Help, README, repository guidance, patching coordination documentation, and Unreleased notes reflect F05.

**Recommended next: F06 — ambiguous WhatsUp Gold device names.** Refuse ambiguous matches instead of
choosing the first result, so a maintenance change cannot target the wrong matching device. Preserve
unique matches and require independent red-team review. F12 remains skipped.


### F05 final local verification and delivery

- Main final full regression: **1,311 passed, zero failed, zero skipped** (2m10s).
- Main full solution build: **zero errors, one known missing agent-signing certificate warning**;
  the locally built UpdateAgent remains unsigned. No deployment or release was performed.
- Independent review reran all **26 focused tests**, built the solution, reviewed the final source,
  and reported no confirmed remaining defect. Main independently read all changed source/tests and
  verified the existing transport/copy-only assertions were retained or adapted to the new contract.
- `git diff --check` passed. Changes are ready for the standing authorized conventional commit/push;
  no attribution trailers, version bump, live server staging, or package execution is included.


## F06 capability and acceptance ledger

| Capability | Status | Required evidence |
|---|---|---|
| Unambiguous distinct valid device identity | Implemented and tested locally | Duplicate short/full names, reversed ordering, same-ID repeats, invalid IDs and conflicting fields |
| Name ambiguity cannot be bypassed through DNS/IP | Implemented and tested locally | Ambiguous name plus unique IP; exact IP and lookup-error regression tests |
| Shared state-read and maintenance identity rules | Implemented and tested locally | Actual generated PowerShell 5.1 scripts with controlled inventory |
| Ambiguous inputs never mutate; valid mixed-batch targets proceed | Implemented and tested locally | Capture exact IDs passed to stub mutation API and honest per-name outcomes |
| Operator distinguishes ambiguity, absence and lookup failure | Implemented and tested locally | Streamed rows, final reconciliation, maintenance rows and partial summary |
| Independent review and final verification | Completed; independent review accepted | Main source review, independent 163-test WUG run, full solution builds and 1,340 passing regression tests |
| Live WUG inventory/maintenance validation | Outside local validation | No live device inventory or maintenance changes used |


### F06 review checkpoint

- Shared resolution must count distinct valid device IDs, preserve short-name collisions even when
  one row is an exact bare-name match, and permit a unique exact full name only after checking its
  own matching identities. Ambiguous names cannot fall through to DNS/IP selection.
- Read-only inspection of installed WhatsUpGoldPS 0.1.21 confirmed `DeviceId` is `int[]`; validation
  therefore requires a positive scalar Int32. Malformed or missing identity evidence cannot enter a
  mutation batch or poison valid targets through parameter binding.
- The installed module returns aggregate `successfulOperations`, `errors`, and `success` metadata.
  It can report a partial API failure without throwing. F06 must inspect that metadata; an aggregate
  partial count cannot identify successful individual rows, so those rows remain change not confirmed.
- These are local source and module-contract observations, not evidence of a live inventory collision
  or live maintenance failure. Executable dummy-inventory tests and independent refutation remain pending.

- Review also rejected dotted-prefix equivalence between different full names and short-name exact
  preference that could hide another qualifying device. Both matching and field-consistency rules now
  restrict prefix compatibility to a genuinely bare name paired with its full name.
- State callbacks now require the run to remain current and accept each pending name only once;
  callbacks queued after final reconciliation cannot overwrite final rows or double-count outcomes.
- Intermediate validation: six new outcome-parser cases passed; the main WUG checkpoint passed
  156 tests. These counts precede the final edge-case additions and do not constitute final acceptance.


### F06 final verification and delivery

- Main full regression: **1,340 passed, zero failed, zero skipped** (3m08s). Main and independent
  reviewer each built the full solution including Desktop: **zero errors, one known missing
  agent-signing certificate warning**. The local UpdateAgent remains unsigned; no release was made.
- Independent final WUG run: **163/163 passed**, including real Windows PowerShell 5.1 processes.
  Final implementation-targeted run: **37/37 passed**. Main personally reviewed changed source,
  tests, fixture, caller reconciliation, help, and documentation; independent review reported no
  remaining confirmed defect after corrections.
- Executable cases cover reversed duplicate short names, duplicate full/display names, unique full
  names with trailing dots, same-ID repeats, invalid/missing/array IDs, conflicting fields, dotted
  full-name prefixes, exact IP fallback, and name ambiguity that cannot be rescued by DNS/IP.
  Captured maintenance ON/OFF calls contain only distinct valid IDs; invalid-only input produces no
  mutation. Mixed batches retain successful input names, ambiguity and lookup failures separately.
- The installed module's aggregate response is checked. Partial API success reports its confirmed
  count without inventing successful individual names. The actual pooled state loop preserves explicit
  streamed and final outcomes. Existing SSL, BOM, credential, cancellation and process tests passed.
- Tests use dummy devices and disposable capture files. No live inventory, WUG API call, maintenance
  change, or production canary was performed. The module contract was checked against installed
  WhatsUpGoldPS 0.1.21; future module behavior is not established by these fixtures. Desktop behavior
  was source-reviewed and compiled, not exercised through an interactive live WUG session.
- Help, README, repository guidance, Unreleased notes, and this ledger are updated. F06 delivery uses
  the standing authorized conventional commit/push with no attribution trailers or version bump.

**Recommended next: F07 — recovery from cached Kerberos failure.** Provide a deliberate per-host
WinRM retry and a fresh attempt after credential changes, so a temporary failure does not keep a
recovered server on fallback for the rest of the session. F07 remains unstarted; F12 remains skipped.


## F07 capability and acceptance ledger

| Capability | Status | Required evidence |
|---|---|---|
| Explicit Retry WinRM bypasses cached rejection | Implemented and tested locally | Harmless fresh connection check; verified success restores relevant connection |
| Failure, timeout and cancellation remain truthful | Implemented and tested locally | Bounded return, no false healthy verdict, late-result fencing |
| Credential and endpoint isolation | Implemented and tested locally | Same-name credential replacement, host/port/TLS isolation; no secrets in keys/logs |
| Routine monitoring avoids repeated failed attempts | Implemented and tested locally | Cached rejection regression for normal and streaming operations |
| Operator can recover selected machines | Implemented; source-reviewed and compiled | Context-menu wiring, current credentials, row/Activity outcome and Stop support |
| Regression and delivery | Verified locally; authorized commit/push | 22 focused tests, main source review, full solution build and 1,353 passing regression tests |
| Live WinRM/Kerberos validation | Outside local validation | No real server or production authentication used |


### F07 review checkpoint

- Operator action is **Retry WinRM** in the selected-machine context menu. It uses the current
  credential snapshot, checks at most eight machines concurrently, and bounds each dispatched check
  to 25 seconds. Stop cancels queued/in-flight checks. It only updates Command result and Activity;
  Check Vitals refreshes the prior health snapshot.
- Connection decisions use host, port, TLS, normalized username and a weak in-memory credential
  identity token. Password contents and password hashes are absent from keys and logs. New wrappers
  around the same saved credential retain suppression; replacing its password identity permits a
  fresh attempt even for the same username.
- Review requires atomic route/generation snapshots for normal calls, sticky ordinary Kerberos
  rejections, and recovery generation fencing at both start and completion. A late ordinary request,
  including one dispatched during recovery, must not overwrite the newer verified result.
- Verification runs only a harmless sentinel script through the existing WinRM host/gate. Script
  errors, missing verification output, timeout, cancellation and supersession cannot report success.
  No scan, install, reboot, server configuration change or previous script is replayed.


### F07 final local verification and delivery

- Main final full regression: **1,353 passed, zero failed, zero skipped** (2m57s). The implementation
  worker also passed 22 focused routing/recovery cases and the full suite before the final queued-task
  cancellation guard. Main verified the final guard with the full regression run.
- Main full solution build, including Desktop: **zero errors, one known missing agent-signing
  certificate warning**. The local UpdateAgent remains unsigned; no release or deployment was made.
- Main personally inspected the cache, routing/recovery service, deterministic tests, Desktop menu,
  row/Activity handling, and documentation. Review corrections preserve sticky ordinary rejection,
  atomically capture route/generation, and fence requests both before and during explicit recovery.
  Cancellation is checked before queued dispatch and before reporting verification success.
- Tests exercise successful recovery, missing/error verification output, timeout and cancellation
  with an uncooperative inner task, late normal/retry results, endpoint and credential isolation,
  same-username credential replacement, and streaming suppression. All use dummy hosts and credentials;
  no live WinRM, AD/Kerberos repair, server mutation, or production authentication was exercised.
- The Desktop action was source-reviewed and compiled, not interactively exercised against a real
  server. Existing health readings remain snapshots until Check Vitals refreshes them. Timeout/Stop
  bounds waiting and requests cancellation; it is not evidence that every underlying OS activity
  has already terminated. Late completion cannot change the recovery verdict.
- Help, README, repository guidance, patching-lane documentation, and Unreleased notes are updated.
  Delivery uses the standing authorization for a conventional commit/push, with no attribution trailers
  or version bump. `git diff --check` passed.

**Recommended next: F08 — distinguish software absence from a failed check.** Show “Could not check”
when a registry/provider query fails, so an access failure cannot be mistaken for “Not installed.”
F08 remains unstarted; F12 remains skipped.

## F08 implementation ledger — October 14, 2026

| Capability | Status |
| --- | --- |
| Complete absence versus failed/incomplete search, WinRM and DCOM | Implemented; 58 focused Software tests passed |
| Preserve proven matches with partial-read warnings | Implemented and tested with generated scripts and injected DCOM provider responses |
| Service absence versus lookup failure | Implemented and tested for verified missing and failed lookups |
| Grid, logging and CSV preserve unknown results and reasons | Implemented and tested; 13 CSV/lifecycle cases included in the passing full suite |
| Real server/registry validation | Not performed; local controlled tests only |

Scope: existing registry matching and read-only software checks. No installation, remediation, release or version bump.

F08 review checkpoint: independent review identified stale queued cancellations and retained software errors. The row lifecycle now resets before queueing, clears only its own prior error, and guards all completion writes by check generation so an old cancelled run cannot overwrite a replacement. Product and service queries remain separate; help and tests explicitly cover a registered-product absence alongside a running matching service. Root found and corrected a value-read error still treated as missing. Additional injected DCOM tests now exercise the actual reader orchestration; final validation passed.

### F08 implementation and independent review

- WinRM requires a valid result and trustworthy completed search before reporting absence. DCOM rejects unknown/error return codes instead of silently treating them as absent. Missing optional roots are allowed; if neither uninstall root can be enumerated, both transports report unknown.
- A proven product match survives unrelated read failures, including unavailable version details, with visible warnings. Optional service-query failure is separate from verified service absence. Matching remains literal name/publisher substring within the existing machine uninstall registry scope; no Win32_Product or remediation was added.
- Grid and Activity show the result and reason. Software CSV preserves its original nine columns and appends result/error/warning fields; failed checks export Installed=Unknown, not No.
- Cancelled queued checks reset old results; cancelled active callbacks cannot overwrite a newer check. Only the previous software-owned LastError clears on recheck. Independent Sol review accepted these fixes and the final DCOM failure-handling design.
- Production Desktop compiled with zero errors. The existing missing signing-certificate warning remains; no release or deployment was performed.
- Root independently executed five generated-script scenarios under Windows PowerShell 5.1 using dummy registry/service functions: installed, complete absence, both roots missing, access denied, and service error. All passed; no actual registry/service operations occurred.
- Tests use synthetic provider results and dummy data. Real WinRM/DCOM authentication, MI marshalling, live-server cancellation and interactive WPF behavior were not integration-tested.

**Recommended next: F10 — ordered preference saves and bounded normal-exit flush.** This prevents an older background save from replacing newer preferences and reduces settings lost on normal exit. F09 was delivered with F01; F12 remains skipped.

### F08 final verification

- `dotnet build source/Vivre.slnx --no-restore -v minimal`: succeeded, zero errors, one existing missing agent-signing-certificate warning.
- `dotnet test source/Vivre.slnx --no-build -v minimal`: **1,392 passed, zero failed, zero skipped**, 2m39s. This includes the 58 Software tests and 13 CSV/lifecycle tests.
- Independent red-team findings addressed and re-reviewed; root inspected the final diff. `git diff --cached --check` passed.
- F08 delivery uses a `fix:` commit on master; exact commit and push are recorded in Git history. No version bump, release, or live operational changes.

## F10 implementation ledger — October 14, 2026

| Capability | Status |
| --- | --- |
| One ordered save stream across personal-settings callers | Implemented; FIFO ordering and shared-caller tests passed |
| Detached snapshots; preserve future keys and atomic replacement | Implemented; isolated real-file tests passed |
| Bounded normal-exit flush with visible failure/timeout reporting | Three-second close flush implemented; flush tested, WPF warning reviewed/compiled |
| Failure recovery and responsive ordinary preference updates | Implemented; blocked writes, failure/recovery and callback isolation tests passed |
| Shared operational settings and remote-operation shutdown | Outside F10 scope; unchanged |

Implementation uses a Sol worker for queue concurrency and root for Desktop integration. Independent review will refute ordering, failure and shutdown assumptions before delivery.

### F10 implementation and review evidence

- Personal `AppSettingsStore` instances for the same normalized file share a cache and one `OrderedSaveQueue`. Save captures a detached JSON snapshot synchronously, then the single background writer merges future keys and calls the existing atomic writer. No shared operational settings were changed.
- Flush captures the accepted queue tail and reports that result even when newer work arrives. A successful newer full snapshot recovers an older failure. Failure callbacks run separately, cannot block the writer/flush, and their exceptions are contained.
- `MainWindow.OnClosing` honors an already-cancelled close, otherwise waits up to three seconds before existing Closed disposal. Success closes normally. Failure or timeout is logged and shown in a warning for the operator to acknowledge; the disk wait is bounded, while the warning remains until acknowledged. Remote operation teardown is unchanged.
- Independent Sol review found no correctness blocker. Root corrected a test-scheduling race and clarified delayed-error log wording so it acknowledges possible recovery by a newer save.
- Root solution build passed with zero errors and one existing missing agent-signing-certificate warning. Root targeted test run: **14 passed**, zero failed/skipped (7 queue and 7 production-store tests).
- Tests use isolated temporary files and controlled writer stalls/errors. They cover FIFO order, detached mutable fields, shared callers, future keys, immediate save/flush/reload, locked and corrupt file refusal, recovery, timeout, serialization, and captured-tail reporting. User settings were not touched.
- Interactive WPF close/tray warning behavior was inspected and compiled, not exercised in a UI session. Forced termination/power loss still cannot guarantee the latest preferences. No deployment or version bump.

**Recommended next: F11 — bounded WinRM staging and owned temporary-file cleanup.** This reduces memory pressure from large packages and avoids abandoned transfer files while preserving F05’s integrity and replacement guarantees. F12 remains explicitly skipped.

### F10 final verification

- `dotnet build source/Vivre.slnx --no-restore -v minimal`: zero errors; one existing missing agent-signing-certificate warning.
- `dotnet test source/Vivre.slnx --no-build -v minimal`: **1,406 passed**, zero failed/skipped, 2m35s.
- Independent review and root final inspection found no outstanding correctness blocker. `git diff --cached --check` passed.
- Delivery uses a short `fix:` commit on master; exact commit/push are in Git history. No release or deployment.

## F11 implementation ledger — October 14, 2026

| Capability | Status |
| --- | --- |
| Disk-backed ZIP and bounded byte/base64 buffers | Implemented; 5 focused tests and 8/32/96 MiB live-memory sampling passed |
| ZIP SHA-256 and existing manifest/publication checks | Implemented; real PowerShell 5.1 publication and truncated-archive preservation tests passed; final multi-chunk regression passed |
| Transfer-owned cleanup after success/failure/cancellation | Implemented; lifecycle tests passed; tombstone interruption checks passed |
| Durable pending cleanup and safe later retry | Implemented; unreachable cleanup retry passed; isolated journal and full-suite checks passed |
| Active/concurrent transfer and unrelated-file protection | Implemented; lease, busy-lock, unknown-file, collision and recovery tests passed; symlink execution remains unverified |
| SMB-first, copy-only behavior and Server 2016 CU separation | Preserved; existing regression suite passed |

Sol workers own separate archive and remote-script helpers; root owns orchestration/journal integration. Independent red-team findings and their resolution are recorded below. No live transfers or cleanup are authorized by this implementation batch.

### F11 validation checkpoint

- Archive helper: 5 focused tests passed, including round-trip bytes/hash/empty folders and cancelled
  archive cleanup. Dummy 8/32/96 MiB payloads sampled at 3.7/3.9/3.7 MiB peak additional managed live
  memory. Forced collections perturb the runtime; these values exclude native/OS buffers and are not
  a total-process working-set ceiling. Manifest/ZIP metadata still scales with entry count.
- Independent red-team review identified late initialization losing its journal and interrupted cleanup
  losing its ownership marker. The integration retains uncertain initialization records; the cleanup
  helper now uses an ownership-coded tombstone. Child reparse checks are implemented; execution limits are below.
- Existing cross-process publication coordination, manifest entry-count scaling, pre-transfer manifest
  cancellation, and old-backup partial deletion were noted as limitations of the pre-existing F05 path;
  this batch does not claim to resolve them. Server 2016 F12 remains explicitly skipped.
- Final integration, executable corruption/retry tests, and full solution checks passed. Commit and push are authorized; delivery is recorded in Git history.

Additional review resolution: remote initialization now has a 30-second cancellable local wait;
abandonment retains the journal without racing immediate cleanup. Its temporary initialization path
is deterministic and recorded through the ownership token. Local ZIP deletion failures report the
exact path for manual cleanup; automatic local ZIP recovery is not included. Incomplete initializer
metadata is conservatively retained for manual verification rather than deleting unproven files.

Validation limitation: the Windows account cannot create symbolic links (confirmed by a separate
file-link privilege probe). The four conditional script reparse cases return early on this host;
their passing test entries do not establish executed reparse protection. Static checks are present,
but those cases require an appropriately privileged test environment. No live WinRM/SMB transfers,
server cleanup, deployment, or signing-certificate changes were performed.

### F11 final verification

- `dotnet build source/Vivre.slnx --no-restore -v minimal`: succeeded, zero errors, one existing
  missing agent-signing-certificate warning.
- `dotnet test source/Vivre.slnx --no-build -v minimal`: **1,441 passed**, zero failures, 4m46s.
  The runner reports zero skips, but four conditional reparse cases returned early because symbolic
  link creation lacks privileges; do not treat those cases as executed protection evidence.
- Real Windows PowerShell 5.1 fixtures verified multi-chunk publication, ZIP SHA-256 rejection of a
  truncated transfer with the previous package unchanged, owned cleanup, busy locks, mismatched
  ownership/offsets, unknown-file preservation, partial tombstone recovery and initializer collisions.
- Fake transport tests verified successful staging, missing acknowledgements, chunk cancellation,
  unknown publication response, unresponsive initializer cancellation without racing cleanup,
  bounded unresponsive cleanup, durable pending retry before SMB, and active-transfer exclusion.
- Journal tests verified independent-instance leases, distinct transfer identities, durable initialization
  acknowledgement, host filtering and refusal of tampered path records.
- `git diff --check`: passed. No live servers contacted; no deployment or version change.

**Recommended next:** review the recorded validation gaps before authorizing another implementation
batch. F11 closes the agreed queue; F12 remains skipped. Live transport behavior, privileged reparse
fixtures and the documented pre-existing F05 limitations are not claimed as verified or resolved.


## Performance implementation ledger — October 14, 2026

Shaun authorized tackling F13–F20 after the performance assessment. Reuse existing components; commit/push validated changes. F12 remains skipped. No deployment or live server operations are included.

| Finding | Capability / acceptance | Status |
|---|---|---|
| F13 | Debounced text filtering, cached counts, correct immediate state filters and disposal | Implemented and locally tested; operational limits below |
| F14 | Efficient batched protected-path persistence off UI; retire only confirmed-cleaned owned paths | Implemented and locally tested; operational limits below |
| F15 | Reset/replace/dispose detach old row subscriptions | Implemented and locally tested; operational limits below |
| F16 | Bounded pending UI logs with explicit overflow summary and complete durable logging | Implemented and locally tested; operational limits below |
| F17 | Cancellable preparation and bounded manifest transfer, preserving verified publication | Implemented and locally tested; operational limits below |
| F18 | Losing throttle waits cancelled without permit loss | Implemented and locally tested; operational limits below |
| F19 | Publication lock ownership retires idle entries safely | Implemented and locally tested; operational limits below |
| F20 | Bounded completion banners with discoverable summaries | Implemented and source-reviewed; Desktop build passed; visible banner/tray interaction unverified |
| Operational validation | Live SMB/WinRM/EDR and native RDP soak | Requires specifically authorized live targets/window; no such operations run |

File ownership: workspace/probe; log/throttle/probes; registry/banner/tests are separate worker scopes. Coordinator owns staging integration, acceptance ledger, final builds/tests, independent review and delivery. No Claude task is editing these files.


### F13–F20 final local validation

- Full Release Core suite: 1,454 passed. Focused staging/gate: 69 passed; final registry/throttle checks: 31 passed; final local-copy/preparation/gate checks: 18 passed. Privilege-conditional reparse cases remain unproven despite the runner reporting zero skips.
- Final Desktop/audit Release build passed with the existing missing agent-signing-certificate warning. Two final repeated probes passed, including actual offscreen WorkspaceView/DataGrid rendering, all 15 live filter inclusion/removal cases, stale-row collection, bounded log admission and reentrant Clear/Add, and zero losing throttle waiters.
- Independent review corrected registry durability and logging reentrancy regressions before delivery. The measured live-property optimization preserved all predicate dependencies; runtime checks caught and corrected deferred-refresh misuse.
- [Implementation report and exact evidence](vivre-performance-implementation-2026-10-14.md). F13–F19 have focused local runtime evidence; F20 is implemented and source-reviewed but actual banner/tray interaction remains unverified. No live servers or production integrations were exercised; existing operational validation gaps remain.
- Reuse preserved: WPF views/timers, Serilog, durable registry writer, shared throttles, ownership conventions, chunk transfer, SHA256 verification and recoverable publication. Original File.Copy semantics remain; cancellation cannot interrupt an individual blocking copy. Metadata memory scales with entry count. Cross-process registry coordination remains a pre-existing limitation. F12 stays skipped.

## Release follow-through — 2026-10-14

Shaun authorized release preparation and remaining-work follow-through, then restricted live checks to dummy files under Z:\Vivre\testing. Release 1.17.2 includes CU SHA-256 copy verification, monitor-only Stop, .NET security servicing and final publisher signature enforcement. See [acceptance ledger](release-1.17.2-validation.md) for exact evidence and limits. No server scan/install/cleanup/reboot or native RDP canary was authorized or run. Multiple-task schedule tracking remains open; fast-watch lifecycle retains the earlier explicit deferral, and F12 stays skipped.

## Remaining-work completion — 2026-10-14

Shaun asked to finish the outstanding work. F25 multiple-task schedule display is implemented in 1.17.3; it changes local tracking and result identity only, not scheduled-task creation, cancellation or reboot commands. Actual MainWindow banner overflow/order/dismissal now has an offscreen runtime probe. The native UI helper still fails to initialize; visible tray interaction and live server/RDP checks remain unverified. The testing-folder-only authorization and earlier explicit F12/fast-watch deferrals remain in force. See [1.17.3 ledger](release-1.17.3-validation.md).

## Save-list prompt follow-up — 2026-10-19

- User evidence: Save list appears with white right/bottom margins; after OK another Save list appearance is shown. Rename is reported working. Screenshots cannot establish whether a second window opened or the same window repainted.
- Implemented: close the Lists menu and yield past input teardown before opening the modal; guard repeated requests from entry through dialog completion; capture the original tab. Save-only dialog uses width 440, automatic height, no backdrop, and the existing application background brush. Rename and list storage are unchanged.
- Verified: Release Desktop build succeeds (zero errors, one missing agent-signing certificate warning); five ComputerListStore tests pass. Source review confirms the guard resets on all exits and the existing help workflow remains accurate.
- Not verified: reproduction of the operator's duplicate-prompt/white-margin symptom, mouse interaction or DPI/RDP rendering on the affected machine. This is a targeted mitigation, not a proven root-cause resolution. No deployment or release package produced.
- Independent review: no blocking code regression found; changelog softened to reflect the unverified UI outcome.

## AD Linux label acceptance — 2026-10-19

Scope: label Linux from AD only; no Linux operations, appliance detection, changes to scans, credentials, update routing or existing OS facts.

| Capability | Status |
| --- | --- |
| Recognize Linux from AD operatingSystem; expose original value on hover | Implemented; 22 classification/identity/cache/concurrency tests and actual name-template probe passed |
| Background bounded batches/cache shared across tabs; scans never await AD | Implemented; shared one-request gate, 64-host batches, 4096-entry/10-minute cache; delayed-directory UI probe passed |
| Missing/unreachable AD leaves label blank; no Windows/absence inference | Implemented/tested; outages retain existing labels and back off one minute |
| Removed/renamed rows and closed tabs cannot receive stale labels | Implemented; production workspace probe passed, including list reset |
| Focused tests, delayed-directory responsiveness probe, independent red team | 27 focused tests and WPF probe passed; independent red-team review found no blocking defect and reran 22 AD tests plus the expanded probe |
| Real domain lookup against Shaun's AD | Not validated locally; needs domain access |

Validation checkpoint: Release Desktop + PerformanceAudit build passed (0 errors, existing missing agent-signing certificate warning). 27 tests passed: 22 new AD-label tests plus 5 existing list-store tests. Final 1,000-row production workspace/name-template probe: 55.9941 ms row enqueue, 12 dispatcher heartbeats during 200 ms held directory response, 16 batches, maximum 64 names/batch and one active request per workspace. Core tests separately verify that the actual service offloads blocking reads, serializes callers and caches positive/negative results. Probe uses fake AD and no remote scan; these timings are not a live scan benchmark.

Scope/limits: default/current joined AD domain using ambient Windows login, matching the user's default-domain example; no RSAT dependency, no trusted-domain discovery, no credentials UI, no LDAP writes. Unknown OS strings are left unlabeled; recognized distro prefixes include the user's Ubuntu/RedHat/CentOS/Amazon values. AD metadata can be stale and is displayed separately from operational OS/build data. Lookup runs when rows load/rename; reload a list to retry unavailable AD. Native in-flight calls finish under configured LDAP timeouts rather than being forcibly aborted; only one shared worker holds the directory lane. No live AD transport or production scan was exercised. No package or deployment in this change.

Independent final red-team validation: 22/22 AD tests passed; Release probe build passed with the existing signing warning; 1,000-row WPF probe passed again (99.7 ms enqueue, 14 dispatcher ticks during 200 ms delay). No live AD or concurrent remote-scan latency claim. The label capability is implemented and locally validated; operational domain validation remains outstanding.

## Release preparation — 1.18.0, 2026-10-19

Shaun authorized packaging current changes and requested a stable ZIP filename. The release includes Save List prompt mitigation and AD Linux labels; appliance detection is explicitly excluded. Version/changelog/README align at 1.18.0. Use `publish.ps1 -Zip -OutputDir C:\src\Vivre\publish\Vivre` to produce `Vivre.zip` and its checksum; internal version and source commit remain in the publish manifest. Existing publisher signature and ZIP-content verification are mandatory. No production deployment is performed here.

Remaining evidence gaps: Save List white-margin/apparent-repeat behavior on Shaun's machine; live default-domain AD labeling and actual concurrent scan timing. Earlier live/native integration gaps remain recorded in their original acceptance ledgers. F12 (separate Vivre instances staging CU files to the same Server 2016 host) and the earlier force-reboot fast-watch work remain explicitly deferred; this release does not claim those are resolved.
