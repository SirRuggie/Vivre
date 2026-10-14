# Vivre application design and unresolved issues

> Point-in-time review dated October 5, 2026. Preserve this record; track later decisions and fixes separately. Application changes are not authorized. Only this requested report was created.

## Overall assessment

**Vivre has a sound core architecture for a single administrator managing a Windows fleet, but important protections are applied inconsistently between features. I recommend targeted reliability work before expanding functionality, not a rewrite.**

The strongest parts are the separation of UI and core services, shared remoting infrastructure, explicit patch/reboot state models, and the effort to distinguish an unverified result from success. The weakest parts are the boundaries between features: remediation can bypass patching ownership, different transports stage different folder contents, some reboot paths provide less disclosure than others, and persistence has several different failure behaviors.

This review found substantive issues beyond the existing backlog. The most immediately useful starting points are **F01, F02, F04, and F05** below. F06 and F12 become especially important if duplicate host names or concurrent Vivre instances exist in your environment.

## Review scope and evidence

- Reviewed the local checkout at **C:\src\Vivre**, whose Git HEAD reference points to **master**, commit **f75381aedcb081313b1b10db4a0be1454f7ba2ab**. Findings describe the files actually present, which may include uncommitted changes; Git working-tree status could not be obtained.
- Inventoried 237 production C# and XAML files, approximately 44,279 lines, and 103 C# test files. Counts exclude build output. This was a broad, risk-focused static review of all major subsystems, not a claim that every line received exhaustive verification.
- Inspected architecture/composition, desktop operation ownership and confirmations, patching and the remote agent, reboot handling, WinRM/DCOM/SMB routing, staging, health/remediation, software checks, WUG resolution, RDP persistence/lifecycle, settings, tests, and publishing configuration. Checked the current backlog and relevant historical investigations.
- Independent reviewers examined desktop, remoting/persistence, and patching. The primary reviewer re-read the source supporting the findings retained here.
- **No application code, settings, tests, dependencies, or remote machines were changed. No fixes, commits, builds, deployments, or live operations were performed.**
- **Build and test execution are unverified.** The normal command runner failed with a Windows process security environment error; alternate process launch was also unavailable. File reads worked. Historical claims of 1,200 passing tests are not a fresh test result.
- The running WPF UI and a deployed executable were not inspected. Visual layout, actual remote behavior, and timing-dependent failures need controlled validation. No package-vulnerability assessment was performed.

“New” means not found in the current backlog and records checked, not proof that nobody has encountered it before. “Confirmed” below means the code path or missing protection is confirmed; it does not mean a production incident was reproduced.

## Findings at a glance

| ID | Priority | Issue | History |
|---|---|---|---|
| F01 | High | Disk cleanup can interfere with active patching or staged packages | Newly identified |
| F02 | High | Reboot disclosure and approved target handling remain inconsistent | Known and still present |
| F03 | High | SMB reboot fallback can report issued after a genuine start failure | Known and still present |
| F04 | High | Saved RDP connections and credentials lack safe recovery from interrupted writes | Newly identified |
| F05 | High | Restaging a folder can leave mixed contents or destroy the prior package | Newly identified |
| F06 | High when names collide | WUG can choose the wrong device from multiple matching names | Newly identified |
| F07 | Medium | A Kerberos failure remains cached after the underlying problem is repaired | Known and still present |
| F08 | Medium | Failed software reads can become “not installed” | Newly identified |
| F09 | Medium | Cleanup can claim success despite suppressed failures | Newly identified |
| F10 | Medium | Preference saves can finish out of order or be lost on exit | Newly identified |
| F11 | Medium | Large WinRM staging buffers the whole payload and can leave remote ZIPs | Newly identified |
| F12 | High if runs overlap | Two Vivre instances can collide on Server 2016 staging files | Newly identified; conditional exposure |

High means a credible path to unintended operational impact, loss of saved state, or a materially misleading result. It does not mean every use of the feature fails. Medium means correctness, recovery, or reliability needs attention under the stated conditions.

## Detailed findings

### F01 Disk cleanup bypasses operation ownership

**What can happen:** While a machine is installing updates or receiving files, its Details window still allows “Free up space.” That action stops Windows Update and deletes the Windows Update download cache and temporary files. The default software staging location is also under Windows Temp, so even an already staged package can be swept away by this broad cleanup.

**Evidence:** [ComputerDetailViewModel.cs:124](C:/src/Vivre/source/Vivre.Desktop/ViewModels/ComputerDetailViewModel.cs:124) calls remediation directly. Its [action gate at line 170](C:/src/Vivre/source/Vivre.Desktop/ViewModels/ComputerDetailViewModel.cs:170) checks only that detail window's own IsBusy state. The workspace's [row-ownership delegate at line 1522](C:/src/Vivre/source/Vivre.Desktop/ViewModels/WorkspaceViewModel.cs:1522) protects Check Vitals, not remediation. [RemediationService.cs:173](C:/src/Vivre/source/Vivre.Core/Remediation/RemediationService.cs:173) removes Temp contents, stops wuauserv, and clears SoftwareDistribution downloads.

**Impact:** An action intended to improve a machine's health can disrupt another Vivre operation or remove a payload the operator expects to install later. The cross-feature exclusion is missing in code; the exact servicing outcome was not reproduced.

**Recommended next step:** Make mutating remediation participate in a shared per-host operation coordinator, and define exclusions for Vivre-owned staging/working directories. Checking the button alone is insufficient: the claim must be acquired when execution starts and shared across tabs/windows.

**Verification before accepting a fix:** A simulated active install or stage prevents conflicting cleanup; two Details windows cannot bypass the gate; cleanup preserves staged packages unless their deletion is explicitly requested.

### F02 Reboot approvals do not consistently describe or bind the action

**What remains unresolved:** The install prompt's “Reboot the N first” button directly calls force reboot. With no selection, its pending set comes from the whole tab. The prompt describes rebooting, but does not name the affected machines or disclose the forced reboot and loss of unsaved work in the way the dedicated Force reboot dialog does.

Separately, the dedicated Force reboot and Reboot & verify dialogs display one selection, then execute a command that reads the selection again. The code does not bind execution to the exact approved snapshot. This is a conditional authorization gap if selection changes during the asynchronous confirmation; such a UI sequence was not reproduced here.

**Evidence:** [MainWindow.xaml.cs:1067](C:/src/Vivre/source/Vivre.Desktop/MainWindow.xaml.cs:1067) chooses selected rows or the entire tab; [line 1120](C:/src/Vivre/source/Vivre.Desktop/MainWindow.xaml.cs:1120) builds the nudge and calls force reboot with pending rows. [WorkspaceView.xaml.cs:751](C:/src/Vivre/source/Vivre.Desktop/WorkspaceView.xaml.cs:751) displays the force-reboot selection but calls the method without passing it. [Line 1194](C:/src/Vivre/source/Vivre.Desktop/WorkspaceView.xaml.cs:1194) captures the verification selection, while [WorkspaceViewModel.cs:3853](C:/src/Vivre/source/Vivre.Desktop/ViewModels/WorkspaceViewModel.cs:3853) reads it again. The normal install flow also executes its command without passing the displayed target snapshot at [MainWindow.xaml.cs:1105](C:/src/Vivre/source/Vivre.Desktop/MainWindow.xaml.cs:1105).

**Related known gaps:** Schedule reboot uses its scheduling modal as the entire approval gate, with Schedule as the default Enter action ([ScheduleWindow.xaml:47](C:/src/Vivre/source/Vivre.Desktop/ScheduleWindow.xaml:47)). Script execution has no separate destructive-operation approval ([ScriptRunnerViewModel.cs:107](C:/src/Vivre/source/Vivre.Desktop/ViewModels/ScriptRunnerViewModel.cs:107)), including shipped reboot scripts. These remain operator-initiated actions; this is not evidence of spontaneous automatic reboots.

**Recommended next step:** Agree one consistent target-and-consequence disclosure policy, then pass an immutable approved request into execution. Preserve the explicit decision to allow immediate forced reboots during maintenance windows; a countdown is not proposed here.

**Verification:** Changing selection while an approval is pending cannot change the executed target set; each built-in reboot entry point states scope and force behavior accurately. Existing backlog: reboot-path findings 1–4.

### F03 SMB reboot dispatch can falsely report issued

**What can happen:** If DCOM fails, Vivre creates an SMB/SCM service to invoke shutdown. After creation succeeds, every service-start exception is swallowed, including genuine failures. The caller still returns an Issued or EscalatedToForced result.

**Evidence:** [DcomRebootTrigger.cs:95](C:/src/Vivre/source/Vivre.Core/Updates/DcomRebootTrigger.cs:95) calls the fallback and returns a dispatch result. [Lines 377–399](C:/src/Vivre/source/Vivre.Core/Updates/DcomRebootTrigger.cs:377) catch all start errors and use Debug.WriteLine, which is not a production Release log.

**Impact:** A machine can initially be reported as having received a reboot command when it has not. Later offline/uptime verification is a backstop, so this is not necessarily permanent false success; the immediate result and missing diagnosis are still wrong.

**Recommended next step:** Distinguish expected ambiguous shutdown/service errors from definitive failure, retain production diagnostics, and represent uncertain dispatch honestly. Do not simply treat every exception as failure either: a machine actually going down can break the RPC call.

**Verification:** Controlled start failures, expected service timeouts, and connection loss during a real dispatch produce different, truthful results. Existing backlog: reboot-path finding 6.

### F04 RDP connection persistence can lose the saved tree

**What can happen:** Both RDP stores overwrite their only JSON file directly. An interrupted write can leave corrupt data. When loading the host tree fails, the UI logs the error and substitutes an empty tree; a subsequent edit can save that replacement over the existing file. A transient read failure followed by a successful write is particularly concerning because the original file could have been intact.

**Evidence:** [RdpHostStore.cs:24](C:/src/Vivre/source/Vivre.Core/Rdp/RdpHostStore.cs:24), [RdpCredentialStore.cs:31](C:/src/Vivre/source/Vivre.Core/Rdp/RdpCredentialStore.cs:31), and [CrossDomainRdpViewModel.cs:31](C:/src/Vivre/source/Vivre.Desktop/ViewModels/CrossDomainRdpViewModel.cs:31). There is no atomic replacement, last-good backup, or read-failure latch preventing that save path.

**Impact:** Loss of the folder/host inventory or saved logins, with limited recovery. Passwords are DPAPI protected; the issue is persistence and recovery, not plaintext storage.

**Recommended next step:** Use atomic writes, retain a recoverable last-good copy, and prevent a degraded load from silently becoming the basis for a replacement save. Coordinate credential and tree changes so references remain valid.

**Verification:** Interrupt a write and simulate an unreadable existing file using disposable stores; the prior data remains recoverable and an empty fallback cannot overwrite it.

### F05 Staging does not have consistent replacement semantics

**What can happen:** Restage a folder after removing or renaming a file. SMB deletes the destination first; WinRM and local copy overlay it instead. The latter paths can retain obsolete files while reporting success. If SMB copying fails after deletion, the old package is already gone and the new folder may be partial.

**Evidence:** [DeploymentService.cs:110](C:/src/Vivre/source/Vivre.Core/Deploy/DeploymentService.cs:110) copies local files without removing obsolete ones; [line 198](C:/src/Vivre/source/Vivre.Core/Deploy/DeploymentService.cs:198) deletes the SMB destination before copying; [line 396](C:/src/Vivre/source/Vivre.Core/Deploy/DeploymentService.cs:396) expands the WinRM ZIP over the existing folder. ZIP hashing validates transfer bytes, not the final destination's exact contents.

**Impact:** The same “Stage” action can produce different payloads depending on transport. Subsequent manual installation can consume old configuration or binaries. Vivre itself does not automatically execute the staged payload.

**Recommended next step:** Define whether a package means an exact replacement, then stage and verify in a unique temporary directory before promotion. Preserve the previous usable package until replacement succeeds.

**Verification:** Restage a version with a deleted file over each transport; destination contents match the agreed contract. Inject copy failure and verify the earlier package remains usable.

### F06 WUG name matching can select the wrong device

**Trigger:** WUG contains two distinct devices such as WEB01.domainA and WEB01.domainB, and Vivre looks up WEB01. Both satisfy the normalized name matcher, but the resolver chooses the first result instead of returning Ambiguous.

**Evidence:** [WugMaintenance.cs:126](C:/src/Vivre/source/Vivre.Core/Wug/WugMaintenance.cs:126) accepts the short-name/FQDN matches; [line 176](C:/src/Vivre/source/Vivre.Core/Wug/WugMaintenance.cs:176) selects the first. The chosen device ID feeds [maintenance mutation at line 378](C:/src/Vivre/source/Vivre.Core/Wug/WugMaintenance.cs:378). The IP fallback already counts exact matches and rejects ambiguity; the name path does not.

**Impact:** State can be read from, or maintenance applied to, the wrong matching device. **No evidence was obtained that your current inventory contains these duplicates.** The defect is conditional but deterministic for such inputs.

**Recommended next step:** Require one unambiguous device identity; multiple qualifying device IDs should require disambiguation. Prefer a unique full identity over a broad short-name match where available.

**Verification:** Duplicate short names and duplicate display names cannot silently choose by API result order. The current resolver tests cover prefix collisions and single FQDN matches, not this case.

### F07 Kerberos failure caching does not recover in the same session

**Trigger:** A host produces a Kerberos principal error; the underlying AD/SPN problem is then fixed or the operator changes credentials. Vivre continues rejecting WinRM for that host without attempting it again until the application is restarted.

**Evidence:** [HostTransportCache.cs:47](C:/src/Vivre/source/Vivre.Core/PowerShell/HostTransportCache.cs:47) records the failure without expiry/removal. [RoutingPowerShellHost.cs:59](C:/src/Vivre/source/Vivre.Core/PowerShell/RoutingPowerShellHost.cs:59) checks that host-only cache before using current credentials, port, or SSL settings.

**Impact:** A repaired machine still appears unable to support WinRM operations; fallback transports may work, while scripts/custom columns remain unavailable.

**Recommended next step:** Add a bounded re-probe or explicit retry/reset operation and account for connection identity changes. Avoid restoring repeated connection delays on every poll.

**Verification:** After a simulated failure, recovery or a changed credential permits a new attempt without restarting Vivre. Existing backlog: transport-cache-staleness.

### F08 Unreadable software can be reported as absent

**Trigger:** A registry/provider access failure occurs while the WinRM session itself succeeds. The software script suppresses the query error and emits Found=false if no match is returned.

**Evidence:** [SoftwareProbe.cs:104](C:/src/Vivre/source/Vivre.Core/Software/SoftwareProbe.cs:104) and [line 128](C:/src/Vivre/source/Vivre.Core/Software/SoftwareProbe.cs:128). Parsing accepts the result object without proving the registry search completed. Service enumeration uses the same error-to-“not found” pattern.

**Impact:** “Not installed” can mean “could not read.” This matters when checking security agents or deciding whether a deployment is needed.

**Recommended next step:** Preserve Found, Not found, and Unknown/error as separate outcomes; distinguish a legitimately absent registry path from an access/provider failure.

**Verification:** Denied or failed queries produce Unknown with a reason, while a successful empty search still produces Not found.

### F09 Disk cleanup reports success without establishing success

**What happens:** The cleanup script suppresses file/service errors and always emits ok=true with “Cleared TEMP, Windows Update cache, and recycle bin.” A failed service restart or failed deletion is not reflected in that result.

**Evidence:** [RemediationService.cs:167](C:/src/Vivre/source/Vivre.Core/Remediation/RemediationService.cs:167), especially lines 177–187. [ComputerDetailViewModel.cs:133](C:/src/Vivre/source/Vivre.Desktop/ViewModels/ComputerDetailViewModel.cs:133) displays the resulting success/reclaimed-space message.

**Impact:** The user cannot distinguish a complete cleanup, partial cleanup, or Windows Update service recovery failure. This is separate from F01: even an isolated cleanup can misreport its outcome.

**Recommended next step:** Report each cleanup component's outcome, restore the service state deliberately, and surface partial success. Preserve benign handling of locked temp files without hiding meaningful errors.

**Verification:** Force deletion and restart failures in a controlled fixture and check the reported result names the failed step.

### F10 Preference writes are not ordered or drained at shutdown

**Trigger:** Save preferences twice quickly, or change a preference and exit immediately. Each save queues a separate background task. A lock serializes disk access but does not guarantee those tasks acquire it in invocation order, and no pending save task is retained for shutdown to await.

**Evidence:** [AppSettingsStore.cs:137](C:/src/Vivre/source/Vivre.Desktop/AppSettingsStore.cs:137) and [MainWindow.xaml.cs:569](C:/src/Vivre/source/Vivre.Desktop/MainWindow.xaml.cs:569). AtomicFileWriter prevents a torn replacement within the intended single-writer use; it does not solve ordering or shutdown completion.

**Impact:** The next launch can restore an older preference snapshot. This report does not attribute current machine-wide StagedHosts persistence to this per-user store; some comments still describe the older arrangement.

**Recommended next step:** Use an ordered/coalescing save queue and a bounded shutdown flush with visible failure handling.

**Verification:** Deliberately delay an earlier save and ensure the newest value wins; an immediate clean exit persists the last acknowledged change.

### F11 WinRM staging has avoidable memory and cleanup costs

**What happens:** Before chunking, Vivre reads the entire ZIP into a byte array and creates one complete base64 string. Chunked network transfer therefore does not mean bounded memory use. A ZIP of size N requires approximately N bytes plus 8N/3 bytes for the UTF-16 base64 characters while both coexist, excluding other allocations. A 1 GiB ZIP is roughly 3.7 GiB at that point; concurrent hosts can multiply the cost.

**Evidence:** [DeploymentService.cs:226](C:/src/Vivre/source/Vivre.Core/Deploy/DeploymentService.cs:226), [line 321](C:/src/Vivre/source/Vivre.Core/Deploy/DeploymentService.cs:321). Failure/cancellation during chunks returns without a cleanup finally ([line 243](C:/src/Vivre/source/Vivre.Core/Deploy/DeploymentService.cs:243)); several finalization failures also leave the remote VivreStage ZIP behind.

**Impact:** Large fallback transfers can fail from memory pressure; repeated interrupted transfers can consume target disk. Exact limits depend on package compression, runtime limits, process architecture, and available memory; no large-package stress test was run.

**Recommended next step:** Stream hashing/encoding from disk, bound staging concurrency, and provide identifiable temporary-file cleanup with safe retry semantics.

**Verification:** Peak memory remains bounded as payload size increases; interrupted transfers do not indefinitely accumulate remote ZIPs.

### F12 Server 2016 staging files are shared across Vivre instances

**Trigger:** Two Vivre processes stage a CU against the same host at the same time. The per-host PatchService guard protects tabs within one process, but it does not coordinate another process/operator.

**Evidence:** [PatchService.cs:37](C:/src/Vivre/source/Vivre.Core/Updates/PatchService.cs:37) stores an in-memory claim. Despite unique agent/config names, [SmbAgentLane.cs:218](C:/src/Vivre/source/Vivre.Core/Updates/SmbAgentLane.cs:218) uses a shared directory plus original package filename, and [line 496](C:/src/Vivre/source/Vivre.Core/Updates/SmbAgentLane.cs:496) overwrites it. [Program.cs:290](C:/src/Vivre/source/Vivre.UpdateAgent/Program.cs:290) deletes all CABs in the package directory, extracts into that directory, then selects all CABs there.

**Impact:** Concurrent runs can delete, overwrite, or consume one another's extracted inputs. Windows servicing may reject some overlapping work, but that does not protect the earlier file-copy/extraction phase. Actual concurrent remote staging was not run.

**Recommended next step:** Isolate every package and extracted file by run ID, and explicitly define target-wide ownership for mutating agent jobs. If concurrent instances are out of scope, enforce that limitation rather than relying on a comment.

**Verification:** Two independent controllers against a disposable target cannot mix/delete each other's payloads and either serialize or clearly refuse incompatible operations.

## Other unresolved items and watch points

These do not displace the priorities above.

- **Alternate credentials are not preserved on reboot fallback.** [ForceRebootRunner.cs:76](C:/src/Vivre/source/Vivre.Core/Updates/ForceRebootRunner.cs:76) passes the selected credential to WinRM; its fallback call has no credential parameter, as does [IRebootTrigger at line 14](C:/src/Vivre/source/Vivre.Core/Updates/IRebootWave.cs:14). The documented ambient-login behavior can fail or use a different identity. Known backlog finding 7; surface the identity change and settle the credential contract before supporting broader cross-domain operation.
- **Shared settings remain unsafe for concurrent writers.** [SharedSettingsStore.cs:125](C:/src/Vivre/source/Vivre.Core/Configuration/SharedSettingsStore.cs:125) performs a read/merge/write without cross-process coordination, and [AtomicFileWriter.cs:12](C:/src/Vivre/source/Vivre.Core/IO/AtomicFileWriter.cs:12) uses one fixed temporary filename. Already explicitly deferred for the single-admin operating model; revisit before simultaneous operators/instances, rather than treating it as a newly discovered incident.
- **Removed RDP entries retain encrypted credentials.** Clearing a username drops its reference; removing a node removes the tree entry, but neither calls credential deletion ([CrossDomainRdpViewModel.cs:238](C:/src/Vivre/source/Vivre.Desktop/ViewModels/CrossDomainRdpViewModel.cs:238)). DPAPI still applies. Reconcile unused credentials when implementing F04, with care for any shared references.
- **Self-target recognition is incomplete.** [HostName.cs:12](C:/src/Vivre/source/Vivre.Core/PowerShell/HostName.cs:12) matches lowercase localhost aliases and the machine short name, not every casing/FQDN form. Known reboot finding 11; self-reboot disclosure can be missed for those forms.
- **RDP drag/drop selection deserves a UI reproduction.** [MoveNode at line 293](C:/src/Vivre/source/Vivre.Desktop/ViewModels/CrossDomainRdpViewModel.cs:293) replaces the moved wrapper without explicitly remapping SelectedNode. Later actions could operate through an old parent if selection remains stale. WPF selection-event behavior was not exercised, so the user-visible failure is not promoted to a confirmed finding.
- **Stop versus remote job lifetime needs a controlled test.** Servicing continuation can be intentional. The interaction between agent service shutdown, controller claim release, and a still-running worker was not established at runtime; do not call confirmed overlap or introduce forced termination based on this review alone.
- **A partial health score is not a complete health certificate.** [VitalityScorer.cs:44](C:/src/Vivre/source/Vivre.Core/Vitals/VitalityScorer.cs:44) intentionally does not penalize unknown signals. If the score becomes an operational gate, add a separate data-completeness indicator; do not reinterpret missing measurements as healthy evidence.

## What is designed well

**The foundations are appropriate.** [App.xaml.cs:51](C:/src/Vivre/source/Vivre.Desktop/App.xaml.cs:51) constructs shared services once and injects them into workspaces. This makes cross-tab coordination possible without a heavier framework. Core contains reusable models and interfaces rather than putting all remote behavior in WPF controls.

**Patching has explicit reliability rules.** PatchService coordinates mutating operations across tabs in one process. Reboot outcome and freshness helpers, finite verification windows, and reserved probe capacity show deliberate attention to failure modes. Keep these protections when fixing boundaries; moving code merely to make a file shorter is not a reliability improvement.

**Some recovery standards are already good and should become consistent.** Shared settings refuse to overwrite an unreadable existing file; named lists and app preferences use atomic file replacement. Those patterns offer a practical starting point for RDP persistence. WUG already distinguishes lookup failure from a clean absence and handles IP ambiguity, which makes F06 a narrow inconsistency rather than a wholesale resolver redesign.

**Tests are substantial, but cover different risks unevenly.** The test project references Core and links selected pure UpdateAgent helpers. It does not reference Desktop or exercise the complete agent as its normal unit-test dependency. Existing tests are valuable for parsing, state rules, transport selection, and generated script contracts; they do not establish that every WPF interaction, remote service lifecycle, or embedded PowerShell failure case works.

## Design improvements that would prevent repeat issues

1. **One operation ownership contract per host.** Patching, deployment, remediation, and destructive commands should declare incompatible work through a shared coordinator. Carry the approved target set, identity, cancellation semantics, and operation ID with the request.
2. **One result vocabulary.** Separate success, partial success, failed, unknown, and still-running-remote. F03, F08, and F09 are different examples of flattening uncertainty into a stronger result.
3. **One persistence standard.** Atomic writes, ordering, recovery, and readable failure reporting should apply to every saved store, not just the ones involved in a past incident.
4. **Tests at feature boundaries.** Add focused regression coverage for approval-to-execution targets, remediation during patching, RDP save failure, repeat staging, duplicate WUG names, and agent lifecycle. A large green Core suite does not cover these automatically.
5. **Reduce WorkspaceViewModel responsibility gradually.** It is approximately 6,971 lines. Its size is a maintenance risk, not proof of a performance defect. Extract tested operation coordination and persistence boundaries as related fixes land; avoid a broad refactor of working reboot/monitor logic before regression coverage exists.

Publishing also warrants a later reproducibility check: PowerShell SDK uses a floating 7.6.* reference and agent signing is best-effort in the project file. These are release-process tradeoffs, not evidence that the current package is vulnerable or unsigned; actual resolved dependencies, signatures, and published output were not verified.

## Decisions already made that this review preserves

- No arbitrary hard time limit or forced process kill is proposed for an active Windows servicing install. The patching design records why that previously cut off valid work.
- Immediate forced reboot within the agreed maintenance window and same-instant scheduled reboot were consciously accepted. This review addresses accurate approval/disclosure and execution, not adding a countdown or stagger.
- SMB/service use in the Server 2016 lane is intentional. The remaining exposure to EDR policy is not automatically an application defect.
- A missing update download-size value is an intentional unknown, not inherently a broken scan.
- A suspected Details-window memory leak still needs measurement; it is not reported as established.

## Recommended work order after discussion

1. **Operational conflicts and approvals:** F01 and F02, followed by F03. Agree desired behavior, capture focused regression cases, then make narrow changes.
2. **Saved data and payload integrity:** F04 and F05. These need failure-injection checks, not just successful-path tests.
3. **Environment-dependent targeting risks:** determine whether duplicate WUG identities or simultaneous Vivre instances are plausible. If yes, move F06/F12 into the first group.
4. **Truthful results and recovery:** F07–F11, with the relevant smaller persistence/credential items above.
5. **Architecture cleanup:** extract shared boundaries only after the behavior is protected by tests.

**No action in this plan is authorized or implemented by this report.** The next conversation can select scope using the stable finding IDs. This report is a reference for that decision, not a release approval or a claim that the application has been fixed.
