# Vivre comprehensive audit — 2026-10-14

> Point-in-time record, never edited after the fact. Live status belongs in vivre-backlog.md.
> Baseline: master 074d374 (1.17.3). Review only: no application fixes, deployment, or live server operations performed.

## Verdict and evidence limits

The app is not free of remaining defects. This review identified **20 prioritized items: five High, twelve Medium, three Low**. Some are existing open design gaps; accessibility and runtime performance effects require measurement. Severity describes impact under the stated trigger, not incident frequency. No Critical finding was established.

Three independent review workers covered operations, performance/resource lifetime, and UX/readability. The coordinating review covered packaging, validation, persistence, and cross-checked key claims. Workers challenged their initial findings; one concurrency claim was downgraded after tracing actual dispatcher callers. This is a broad source audit, not proof every possible defect has been found.

No new full test suite, heap/native-handle soak, live WUA/DISM run, real RDP session, screen-reader inspection, or visible UI performance measurement was performed. Earlier release tests remain point-in-time evidence; they do not validate these new findings. The WMI substring defect was reproduced with a harmless local string expression only.

High = potential destructive action, operational conflict, materially false health/update result, or severe resource exhaustion. Medium = workflow failure, recovery/validation weakness, conditional concurrency loss, or accessibility risk. Low = bounded retention, reuse/maintenance drift, or wording inconsistency.

## Ranked findings

### F26 — High — Publish can delete an unintended directory
- **Impact:** a mistaken `-OutputDir` can recursively delete unrelated content before building. The output parameter has no canonical-path ownership/containment check.
- **Evidence:** `publish.ps1:51,68,78`; deletion directly uses the caller's directory. Signing preflight does not establish directory safety. Destructive reproduction was deliberately not run.
- **Recommendation/reuse:** publish into an owned staging directory; reject repository/source/root paths and unowned nonempty folders; use literal paths. Preserve the prior artifact until replacement is verified.
- **Acceptance:** temporary-directory tests reject unsafe targets without deleting a sentinel; failed builds preserve the last valid release.

### F27 — High — Built-in repair scripts can conflict with active patching
- **Impact:** Run Script directly dispatches PowerShell without host operation ownership. Update-cache reset or SCCM-cache deletion can overlap patching/staging on the same machine.
- **Evidence:** `source/Vivre.Desktop/ViewModels/ScriptRunnerViewModel.cs:20-31,130-175`; `scripts/Repair/Restart Windows Update (reset cache).ps1:3-10`; `scripts/SCCM Client/Clear CM client cache.ps1:2-6`. Coordinated services receive the guard in `App.xaml.cs:74-80`; this runner does not.
- **Recommendation/reuse:** route known built-in mutations through the existing HostOperationCoordinator/coordinated remediation contract. Define explicit policy for arbitrary scripts; do not pretend filename or text matching can classify arbitrary PowerShell safely.
- **Acceptance:** known mutating scripts cannot overlap conflicting same-host work; other hosts remain usable. This is an existing scope gap, not a regression in the Details cleanup guard.

### F28 — High — WMI repair reports an inconsistent repository as healthy
- **Impact:** the repair skips salvage and reports no action needed for the very condition it should handle.
- **Evidence:** `scripts/Repair/Rebuild WMI repository.ps1:2-7`; matching `consistent` also matches `inconsistent`. Local expression reproduced True for the inconsistent message.
- **Recommendation/reuse:** explicit result/exit-code interpretation and post-repair verification; handle unknown/localized output honestly.
- **Acceptance:** healthy, inconsistent, failed, and unrecognized responses produce distinct correct outcomes without invoking live repair in unit tests.

### F29 — High — Standalone update scripts can report a false clean result
- **Impact:** a non-clean WUA search returned normally can be presented as zero missing updates or no applicable updates.
- **Evidence:** `scripts/Windows Update/Count missing updates.ps1:19-21`; `scripts/Windows Update/Download and install updates.ps1:23-34`; search ResultCode is not checked before interpreting the collection. Install-result checks later in the script do not validate the search.
- **Recommendation/reuse:** reuse the core lane's clean-success/result classification and transient-error conventions instead of maintaining a weaker parallel implementation.
- **Acceptance:** failed/partial searches never report a clean zero or proceed as if inventory were complete. COM exceptions are a separate path; this finding concerns returned non-clean results.

### F30 — High — Verbose scripts can consume excessive memory and stall the UI
- **Impact:** full PowerShell results, per-machine strings, a cumulative StringBuilder, and a repeatedly copied window string retain large output. Many verbose targets amplify allocation; row output survives closing the runner.
- **Evidence:** `source/Vivre.Desktop/ViewModels/ScriptRunnerViewModel.cs:130-188,195-217`. Each target appends to the aggregate then copies it with ToString; equal-sized per-host output causes quadratic aggregate copying over the run.
- **Recommendation/reuse:** bounded per-host/window display buffers, explicit truncation, throttled updates, and optional full output export. Reuse ActivityLog's bounded-display approach. Streaming alone is insufficient if final results still materialize everything.
- **Acceptance:** dummy verbose-output runs measure bounded retained memory and dispatcher responsiveness. No current heap leak or measured stall duration is claimed.

### F31 — Medium — Update-cache repair hides failures
- **Impact:** service stop, cache rename/delete, and service restart failures are suppressed; the script still prints success and may leave services stopped.
- **Evidence:** `scripts/Repair/Restart Windows Update (reset cache).ps1:3-11` uses SilentlyContinue throughout and unconditional success.
- **Recommendation/reuse:** structured phase outcomes and finally-based recovery, reusing coordinated remediation error reporting.
- **Acceptance:** injected failures identify the failed phase and service recovery result; success requires verified completion.

### F32 — Medium — Existing users do not receive corrected bundled scripts
- **Impact:** updating the app does not update an already seeded built-in script, so fixes such as F28/F29 may never reach existing users.
- **Evidence:** `source/Vivre.Core/Scripts/ScriptLibrary.cs:132-153` copies only missing files into the user's script library.
- **Recommendation/reuse:** version/hash-aware built-in updates within ScriptLibrary: update unchanged shipped copies; preserve edited copies and offer explicit review/reset.
- **Acceptance:** unmodified old built-ins update, customized files survive, and conflicts are visible. Copy-if-missing intentionally protects user edits; retain that protection.

### F33 — Medium — Package-share access runs on the UI thread
- **Impact:** opening Stage can freeze interaction while a large or unavailable package share is enumerated. Opening the 2016 package folder has a smaller related risk.
- **Evidence:** `source/Vivre.Desktop/DeployWindow.xaml.cs:40,61-64`; `DeployPackaging.cs:49-68`; `LcuPackageNeededDialog.xaml.cs:37-52` synchronously creates the folder before Explorer.
- **Recommendation/reuse:** reuse the asynchronous package-loading approach in `SettingsPage.xaml.cs:307-312`, with loading/error states and stale-result protection.
- **Acceptance:** controlled slow/failing filesystem tests plus the existing UI-liveness watchdog verify responsiveness. The blocking source path is confirmed; actual stall duration is unmeasured.

### F34 — Medium — Staging protections can be lost across Vivre processes
- **Impact:** two processes updating the protected-path registry can overwrite one another; later cleanup may omit an active staging protection.
- **Evidence:** `source/Vivre.Core/Operations/ProtectedStagingPathRegistry.cs:9-21,41-78,159-178`; locking is process-local, with whole-file replacement. HostOperationCoordinator is also process-local.
- **Recommendation/reuse:** reuse the StagingTransferJournal exclusive-file lease pattern with reload/merge under ownership; define cross-process host coordination consistently.
- **Acceptance:** deterministic two-process contention preserves both protections and releases them correctly after failure.
- **Status:** known multi-process limitation, not a newly demonstrated incident. Medium reflects the single-admin operating model; impact rises when multiple app instances are used.

### F35 — Medium — Shared settings can lose cross-process changes
- **Impact:** concurrent app instances can overwrite settings or collide on a fixed temporary filename.
- **Evidence:** `source/Vivre.Core/Configuration/SharedSettingsStore.cs:116-140`; AtomicFileWriter documents required caller serialization and uses a fixed temporary path.
- **Recommendation/reuse:** shared exclusive lease and merge while holding it; reuse existing save/lease primitives rather than another independent persistence framework.
- **Acceptance:** two processes preserve unrelated key changes and report write failures.
- **Status:** known open limitation. Initial same-process concern was refuted: known production writers synchronously run on the WPF dispatcher. Do not cite a proven same-process race.

### F36 — Medium — A live but wedged servicing child lacks a recovery outcome
- **Impact:** DISM/tool output reads and WaitForExit can wait indefinitely while agent heartbeats continue, retaining operation ownership. Silence detection alone cannot identify this state.
- **Evidence:** `source/Vivre.UpdateAgent/Program.cs:333-353,365-414`; `DismHelper.cs:120-135,164-182`. Cleanup stall signaling is nonterminal.
- **Recommendation/reuse:** extend existing progress/stall reporting with an explicit uncertain state and documented operator recovery. Do not automatically kill CBS/DISM or release ownership while servicing may still be active.
- **Acceptance:** a simulated live-but-stalled child yields actionable status without incorrectly declaring success, failure, or safe cancellation. No live servicing hang was reproduced.

### F37 — Medium — Script selection can discard unsaved edits
- **Impact:** selecting another script or adding one replaces editor text without a dirty-state decision.
- **Evidence:** `source/Vivre.Desktop/ScriptsPage.xaml.cs:40-48,53-63`; `ScriptRunnerWindow.xaml.cs:50-57`.
- **Recommendation/reuse:** one shared script-editor dirty-state mechanism with Save/Discard/Cancel, including navigation/close paths where applicable.
- **Acceptance:** cancel preserves the current text and selection; save failures retain edits; deliberate discard works.

### F38 — Medium — Reboot approval remains unclear in scheduling and scripts
- **Impact:** scheduling's default button arms the task without the explicit force/unsaved-work disclosure used elsewhere; Run Script can issue a reboot directly.
- **Evidence:** `source/Vivre.Desktop/WorkspaceView.xaml.cs:1058-1078`; `ScheduleWindow.xaml:27-48`; `ScriptRunnerWindow.xaml.cs:60-65`. These exceptions are already recorded in CLAUDE.md.
- **Recommendation/reuse:** reuse count/scope/consequence wording from RebootApproval in the existing action flow. Keep 30-machine lists collapsed; do not add repetitive confirmation dialogs to harmless actions.
- **Acceptance:** the final intentional action clearly identifies target count, timing, force behavior and consequences. Arbitrary scripts require an explicit policy, not unreliable source classification.
- **Status:** known open scope decision, not an F02 regression or authorization to change reboot behavior.

### F39 — Medium — Accessibility contracts are incomplete
- **Impact:** compact icon controls, date/time inputs and dynamic errors may be difficult to identify or discover with assistive technology.
- **Evidence:** `source/Vivre.Desktop/MainWindow.xaml:34-41,392-448,534-574,631-744`; `ScheduleWindow.xaml:27-48`; `HelpWindow.xaml:39-51`; status bindings in ColumnsWindow, SoftwareCheckWindow, WugStateWindow and RdpSessionView. Explicit accessible names are sparse and no LiveSetting strategy was found.
- **Recommendation/reuse:** shared accessible labels/status presenter, associated field labels, selective announcements, keyboard/focus verification. Do not announce every fleet progress update.
- **Acceptance:** actual UI Automation/screen-reader checks confirm names, focus order and meaningful error announcements at compact width/high DPI.
- **Confidence:** source risk, not proof controls are unnamed at runtime; WPF peers may infer names. Contrast/high-DPI rendering remains untested.

### F40 — Medium — Failed publishing can leave a misleading release artifact
- **Impact:** build failure leaves the prior versionless ZIP/checksum in place; compression failure can leave a partial/missing ZIP with a previous checksum.
- **Evidence:** `publish.ps1:80-81,108-114`; final ZIP is deleted and rebuilt in place, checksum updated afterward.
- **Recommendation/reuse:** verify a temporary archive, then promote it with matching checksum/version identity, using the project's existing verify-before-publish convention.
- **Acceptance:** forced build/compression failures never present a partial artifact as the current release and preserve a clearly identified last-good package.

### F41 — Medium — Validation can look greener than its actual coverage
- **Impact:** some symlink tests return successfully when setup cannot create the link; desktop/native checks are not part of the Core test suite.
- **Evidence:** `source/Vivre.Core.Tests/Deploy/StagingArchiveTests.cs:100-102`; `StagingTransferScriptTests.cs:111,134`. Only Core.Tests is checked in; no repository CI workflow was found. Existing PerformanceAudit is separate coverage, not proof of live integrations.
- **Recommendation/reuse:** explicit skipped/inconclusive reporting and a Windows validation entry point reusing existing tests/harnesses; report which native/UI checks actually ran.
- **Acceptance:** unavailable prerequisites never count as exercised assertions. Add a controlled long-run heap/handle and visible UI session before claiming native leak-free behavior. External CI may exist; this review did not establish its configuration.

### F42 — Medium — Rebuilding the same commit can resolve different dependencies
- **Impact:** a fresh restore can pick a different PowerShell runtime package than the audited build.
- **Evidence:** `source/Vivre.Core/Vivre.Core.csproj:26` uses Microsoft.PowerShell.SDK 7.6.*; no checked-in dependency lock or global.json was found.
- **Recommendation/reuse:** pin/lock the validated dependency set and document SDK roll-forward policy in existing release tooling.
- **Acceptance:** clean restores produce the expected dependency graph. This is reproducibility risk, not a claim that the currently resolved package is vulnerable.

### F43 — Low — Removing custom columns retains their row values
- **Impact:** repeatedly adding/running/removing distinct columns retains old keys/strings for the tab lifetime; reusing a name can briefly display stale values.
- **Evidence:** `source/Vivre.Core/Models/CustomValueStore.cs:15-27`; `source/Vivre.Desktop/ViewModels/WorkspaceViewModel.cs:1749-1772` removes the specification but not row dictionary entries.
- **Recommendation/reuse:** add removal/pruning to CustomValueStore and apply it to loaded rows on removal with appropriate change notification.
- **Acceptance:** repeated dummy column cycles return dictionary cardinality to baseline; same-name replacement semantics stay deliberate.

### F44 — Low — Duplicate script presentation logic and unused Settings UI invite drift
- **Impact:** ScriptsViewModel and ScriptRunnerViewModel duplicate CRUD/category projection, differ in deletion handling and keep independent snapshots. SettingsWindow has no construction caller found in the repo, competing with active SettingsPage.
- **Evidence:** `source/Vivre.Desktop/ViewModels/ScriptsViewModel.cs:43-104`; `ScriptRunnerViewModel.cs:82-106,219-237`; `SettingsWindow.xaml.cs` versus SettingsPage.
- **Recommendation/reuse:** share ScriptLibrary change notifications/projection/default policy, preserve each surface's selection, and retire legacy SettingsWindow only after confirming no external/reflection use.
- **Acceptance:** edits in either open script surface refresh the other; built-in policy agrees; settings has one supported implementation. Avoid a generic framework rewrite.

### F45 — Low — Help wording does not match current target controls
- **Impact:** instructions refer to selection count in the bottom bar and button labels unlike the command bar, adding uncertainty about scope.
- **Evidence:** `source/Vivre.Desktop/HelpContent.cs:656-664` versus `MainWindow.xaml:583-626,655-689`.
- **Recommendation/reuse:** align help with current labels/location and centralize recurring action terminology where practical.
- **Acceptance:** walk through Help instructions against the visible app at normal and compact width; counts and action names agree.

## Previously deferred — not silently reopened

- **F12:** cross-process Server 2016 staging coordination/extraction remains explicitly skipped. F34 concerns the separate protected-path registry; coordinate designs if either is later authorized.
- **Force-reboot fast watch:** lifetime/cancellation and a minor downtime-text overwrite remain deferred. Watches can outlive Stop/tab disposal; adding lifetime cancellation also needs correct cancellation handling. This audit does not authorize that behavior change.
- **F38 / F35:** known open approval and multi-process settings limitations are included for completeness, not presented as newly discovered regressions.

## Checked and not substantiated as new defects

- **F25 scheduled tasks:** traced unique install task identity, fixed reboot-task replacement, uncertain cancellation retention, expiry advancement and counts; no new wiring regression found. No live Task Scheduler check performed.
- **RDP teardown:** inspected handler removal and ActiveX/panel disposal; no concrete source leak found. Native-session soak remains necessary.
- **Details window:** timer stops on close; collectible timer/VM cycle is not proof of a leak. No persistent strong-root leak established.
- **WUG processes/runspace pools:** disposal, cancellation/tree termination and finally cleanup inspected; no persistent parent-process resource leak demonstrated.
- **Catalog cache:** process-lifetime singleton and bounded practical key space do not establish an actionable leak; first-caller cancellation concern is not reachable through the current production caller.
- **Synchronous waits / exports:** reviewed apparent Result/Wait matches; no demonstrated UI Task.Result deadlock. CSV exports already offload work and reuse formula-safe escaping.

## Recommended implementation order (not performed)

1. F26/F40 publish safety; F28/F29/F31 correctness together with F32 update delivery.
2. F27 coordinated built-in mutations and F30 bounded output; adversarial tests before any live rollout.
3. F33/F37/F43 responsiveness and edit retention; F41/F42 trustworthy validation/rebuilds.
4. F39/F44/F45 accessible, consistent shared UI behavior. Resolve explicit scope decisions for F34/F35/F36/F38 separately.

Use existing coordinators, persistence leases, bounded log patterns and validation harnesses. Each change needs a focused refutation pass; do not rewrite working subsystems just to consolidate names. No production access, signing changes, or live destructive operations are authorized by this report.
