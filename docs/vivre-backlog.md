# Vivre — current backlog and validation gaps

Reconciled **2026-10-20** against release **1.20.0**, source baseline `5c56ea3`, later acceptance records and targeted source reads. This is a documentation reconciliation, not a new full audit or production test.

The [UI/UX checklist](vivre-ui-ux-backlog.md) holds individual U decisions. The [release ledger](cu-transfer-load-validation.md) identifies the current released source and packaging limits. The previous tracker is preserved verbatim in the [dated snapshot](vivre-backlog-snapshot-2026-10-20.md); frozen findings and historical test results remain unchanged.

**Current release 1.20.2:** a shared CU file-I/O queue reduces simultaneous controller traffic during mixed installs; the host/RDP incident remains unproven in production. See [acceptance and review](cu-transfer-load-validation.md). U-58/U-59 remain included from 1.20.1; deferred decisions are unchanged.

**Follow-up under Unreleased:** Shaun requested a separate CU file-transfer setting, default 2, beside install concurrency. See [current settings acceptance](cu-transfer-settings-validation.md); this does not close the production freeze incident.

**Repeat-install / legacy OS follow-up:** checklist reconciliation and Server 2012/2012 R2 build recognition are tracked in [acceptance and validation](repeat-install-legacy-os-validation.md); live target validation is separate from local regressions.

## Status rules

- **Implemented / locally validated:** the cited implementation has evidence; this does not claim current live-server, native interaction or whole-app soak coverage.
- **Validation remaining:** name the exact untested behavior; do not describe it as a proven defect or silently mark it complete.
- **Deferred by Shaun:** retain the decision until Shaun changes it; a backlog entry is not implementation authorization.
- **Legacy candidate:** preserve the observation, but recheck current code and reproduce it before prioritizing a fix; later broad fixes do not prove every older symptom closed.
- Historical commit IDs and test counts are checkpoint evidence, not current totals; use Git history for delivery and restore points.

## What remains for the current release

| Area | Current status and next evidence needed |
| --- | --- |
| **APVPatching freeze / RDP loss** | 1.20.2 serializes per-host CU file work and clarifies lost-monitoring outcomes; local tests do not prove the production freeze resolved. Verify remote results before retrying timed-out jobs, then collect host/load evidence during the next operator-authorized run. See the release ledger. |
| **U-21 — layout/scaling** | Local layout fixes and raster probes delivered; actual Windows DPI/text scaling, mixed monitors and narrow-window use remain unverified. |
| **U-22 — accessibility/input** | Local focus/menu/name fixes delivered; physical keyboard and spoken screen-reader checks remain open. RDP host/folder credential naming and older validation announcements also need inspection; this is not solely a test checkbox. |
| **Native popup behavior** | The library-search probe could not acquire keyboard focus in the latest shared-navigation batch; focused picker/editor checks passed separately. Keep native popup focus unverified within U-22. |
| **Long-running resource use** | No current-build whole-app memory/CPU/handle/native-RDP soak proves that every leak or stall is absent. Repeated tab/Details/RDP open-close and large-fleet work are measurement targets, not confirmed leaks. |
| **Deployment integration** | Latest-build directory, WinRM/DCOM/SMB, local-host update, multi-server CU throughput and EDR behavior need deployment-host evidence where relevant; older canaries and dummy-host checks do not cover every environment. No live operation is authorized by this tracker. |
| **Packaging / restart** | See the 1.20.2 release ledger for metadata/build checks; Shaun will produce/deploy the ZIP. Do not infer that the latest signed ZIP, deployed startup or upgrade/persistence checks ran here. |
| **Completion notifications** | F20's actual MainWindow/InfoBar queue/cap/order/dismissal behavior was exercised in the 1.17.3 acceptance work; visible native tray behavior remains unverified. |
| **Filesystem privilege cases** | Privileged file-symlink cases were skipped where Windows rights were unavailable; directory-junction coverage did execute. Do not merge those into one “all passed” claim. |

Evidence: [U-21/U-22 matrix](ui-ux-u21-u22-validation.md), [UI ledger](vivre-ui-ux-backlog.md), [People ledger](people-lookup-validation.md), [audit acceptance](vivre-audit-implementation-2026-10-14.md), [performance acceptance](vivre-performance-implementation-2026-10-14.md), [1.17.3 notification checks](release-1.17.3-validation.md), [current release](cu-transfer-load-validation.md).

## Explicitly deferred

| ID / stable name | Decision retained |
| --- | --- |
| **S-1** | WinRM update-agent folder permissions: decision deferred. Establish effective permissions and repair if needed before considering refusal of a new launch on an unsafe host; an ACL command error alone must not block a safe update, and running updates must not be interrupted. No unsafe production folder or exploit has been confirmed. |
| **U-40** | External save/delete while the saved-list choice dialog is open: rare race deferred by Shaun; revisit on an observed problem or changed priority. |
| **U-42** | List menu/open disk reads: measure a real deployment-host pause before adding asynchronous loading behavior; deferred by Shaun. |
| **F12** | Separate Vivre instances concurrently staging Server 2016 CU files on the same server: skipped by Shaun. Existing same-instance host guards and cross-process settings-file leases do not close this separate target-operation issue. |
| **Force-reboot fast-watch lifetime** | Cancellation/lifetime changes remain deferred. A repeat force reboot during the watch window and late status-text replacement are retained concerns; do not add cancellation or another reboot dispatch opportunistically. |

Sources: [S-1/U-40–U-42 review](vivre-follow-up-review-2026-10-20.md), [F12 decision](vivre-review-decisions.md), [historical fast-watch detail](vivre-backlog-snapshot-2026-10-20.md).

## Implemented work — supersedes stale open claims

| IDs / topic | Reconciled status and evidence |
| --- | --- |
| **F01–F11** | Agreed cleanup, reboot approval/reporting, RDP recovery, staging, WUG identity, transport recovery, software reporting and preference fixes implemented with scoped local evidence in the [decision/acceptance log](vivre-review-decisions.md). F12 is excluded. |
| **F07 — transport-cache-staleness** | Retry WinRM and credential/connection-scoped rejection recovery implemented. The old tracker incorrectly called this F10; **F10 is preference write ordering and exit flushing**. |
| **F13–F20** | Performance/resource fixes implemented with [measured local evidence](vivre-performance-implementation-2026-10-14.md). Later [1.17.3 InfoBar checks](release-1.17.3-validation.md) supersede the old F20 “source-reviewed only” claim, within their stated limits. |
| **F21–F24** | CU SHA-256 copy verification, monitor-only Stop, runtime/dependency servicing and agent-signature release checks are recorded in [1.17.2 acceptance](release-1.17.2-validation.md). That historical package is not the current release package. |
| **F25 — multiple scheduled tasks** | [1.17.3](release-1.17.3-validation.md) tracks actual known task identities independently within a workspace session; remote inventory reconciliation, persistence and cross-tab schedule ownership were not added. |
| **F26–F45** | Comprehensive audit fixes implemented with independent review and [local acceptance evidence](vivre-audit-implementation-2026-10-14.md); live/native/privileged validation limits remain above. |
| **F46 — CU staging pause** | Package preparation/hash preflight moved off the dispatcher with local responsiveness checks. The old “not a wrong-thread hash / byte-count only” explanation is superseded by F21/F46; remaining fleet I/O saturation is a measurement question. See [audit acceptance](vivre-audit-implementation-2026-10-14.md). |
| **F47 — APVPATCHING local task discovery** | The later user-raised ScheduledTasks failure was fixed in the generated local controller with task-construction/streaming/policy-isolation evidence; the old “parked, do not troubleshoot” status is superseded. Actual host installation remains untested in that [acceptance record](vivre-audit-implementation-2026-10-14.md). |
| **F48 / F49** | Operation-scoped Install anyway approval and software result/column readability implemented; see the [audit ledger](vivre-audit-implementation-2026-10-14.md) for boundaries and tests. |
| **U-1–U-57** | Checklist records implementation/local evidence except the open U-21/U-22 validation/accessibility work and explicitly deferred U-40/U-42. U-39 is operator-confirmed resolved. U-43/U-44 and U-45–U-57 are included in 1.20.0; earlier “Unreleased over 1.19.2” text is checkpoint history. |
| **Health → Patching handoff** | Later correction uses normal Auto-check on load and distinct Patching tab titles. U-8's original same-title/auto-check-suppressed acceptance is historical, not current behavior; see the [UI follow-up](vivre-ui-ux-backlog.md#patching-handoff-and-status-banner-correction-2026-10-19). |
| **Probe-only Unverified self-heal** | Already shipped in 1.16.2 and retained in MonitorSelfHeal. This does not close the separately parked “update rescan failed” recovery feature; see [changelog](../CHANGELOG.md) and snapshot. |

## Legacy reboot findings — stable case-file numbers

The frozen [reboot-path case file](reboot-path-and-guardrail-findings.md) retains its original numbering and severity assessment. Its blanket “none fixed” wording is historical. The table below does not assign new severity or authorize work.

| Case-file ID | Current disposition |
| --- | --- |
| **#1, #3** | Captured approved targets and install-nudge disclosure fixed under F02; later install approval/U-10/U-47 work further clarifies actions. Do not re-open solely from the old report. |
| **#2, #4** | Scheduled/script reboot approval implemented under F38; the old F02 discussion excluding those paths was superseded by that later authorization. |
| **#5, #12** | Reboot primitive inventory gate and warned-reboot path were already closed in the original tracker; preserve the documented dispatch/force constraints. |
| **#6** | SMB failed/uncertain start reporting and restart-evidence integration implemented under F03; actual SCM/live-reboot coverage remains scoped. |
| **#7** | The Force fallback still passes host/forced state without the selected alternate credential; SMB/DCOM uses the Windows login, as current Help now explains. Identity handling remains a limitation; per-action disclosure and Wave routing need a scoped follow-up before changes. |
| **#8** | Arbitrary custom-column autorun/approval: no closure established. F27 script ownership and F43 retired-column cleanup do not by themselves prove this separate capability question resolved. |
| **#9** | Fallback service creation still has no explicit SDDL; orphan cleanup does not prove access control. Effective default permissions and the delete lifecycle need validation before claiming exposure. This is separate from S-1's WinRM update-agent folder. |
| **#10** | Enable-WinRM remote process/service use remains an EDR compatibility review candidate; the historical detection claim is not a new incident or a proven defect on current systems. |
| **#11** | Still visible in `HostName.IsLocal`: literal `localhost` matching is case-sensitive and the helper does not normalize local FQDNs. F02 target capture did not fix alias recognition; no new live self-target test was run. |
| **#13** | The current Install Help topic explains Force-reboot first but still omits its self-target warning; that warning appears in separate Force/Schedule/Wave topics. This is a documentation gap, not evidence that the runtime confirmation is absent. |

Targeted source references: [HostName](../source/Vivre.Core/PowerShell/HostName.cs), [Force fallback](../source/Vivre.Core/Updates/ForceRebootRunner.cs), [service controller](../source/Vivre.Core/Remoting/RemoteServiceController.cs), [custom-column probe](../source/Vivre.Core/Columns/CustomColumnProbe.cs), and [current Help](../source/Vivre.Desktop/HelpContent.cs). These source reads are not live access-control or reboot tests.

## Retained candidates — revalidate or measure before implementation

Full original descriptions and constraints remain in the [snapshot](vivre-backlog-snapshot-2026-10-20.md). These are not a new set of confirmed current defects.

| Topic / stable name | Follow-up boundary |
| --- | --- |
| **details-window-leak** | Old tracker contradicted itself (“measure first” and “cleared”). No confirmed leak carried forward; include repeated open/close in a measured resource soak. |
| **trace-empty-default-interface-method** | `IActivityLog.Trace` still has a default no-op. Check production implementations/coverage before changing the interface; not evidence that current production logging is absent. |
| **DCOM 1191 test seam** | `TryDcomShutdown` still creates its CIM session inline. Target the unexercised throw-after-escalation branch if adding coverage; retain prior field evidence and avoid altering reboot primitives. |
| **Monitor / verify-arc wiring** | Recheck timeout/admission, cross-tab ownership and coverage of the actual Desktop orchestration. Real WPF probes now exist under `tools/PerformanceAudit`; the old claim that all Desktop wiring is untestable is obsolete. Per-tab settings are already fixed by U-1 and are not the same problem as operation guards. |
| **Post-reboot DCOM probe / stale readings** | Revalidate the old WinRM-only reboot-pending claim and DCOM null-result behavior before assuming all Kerberos failures or stale dots are resolved. Last-reboot grid/readings disagreement remains a legacy candidate. |
| **CU settings / staged-host export** | Recheck whether hand-editing KB leaves stale month text and whether staged-host management still lacks copy/export after later settings/UI changes. No closure inferred from unrelated batches. |
| **Fleet-scale responsiveness** | Measure real multi-server CU copy I/O, ~50 concurrent cleanups, ~300-host idle monitoring, large-grid dock resizing and false first-scan timeouts before tuning. Install/stage concurrency is already configurable; do not recreate that setting. |
| **List/grid hot paths** | Distinguish the fixed cold-start worker-injection freeze from smaller row-add/layout costs and U-42 disk latency. Earlier 319-row additions measured about 65–83 ms; do not repeat the old unsupported “seconds” claim as current evidence. |
| **Residual recomputation** | Extra Has* walks, PatchState parsing, command-state recomputation, checklist row insertion and column virtualization are measurement candidates. Earlier remaining recomputation measured near zero; avoid speculative cache/batching changes in the thread-sensitive live grid. |
| **Legacy recovery / diagnostics** | Recheck overdue wording, midstream Kerberos cleanup, single-host-service assumptions, swallowed fallback errors, RDP reconnect/fullscreen/session notifications and dialog task error reporting before promoting old notes into fixes. |
| **Known-WinRM-failure presentation** | Proactive visual indication is a design candidate, not an approved new status or an automatic instruction to run a fallback transport. |
| **Unknown update size** | A dash intentionally means no trustworthy size is available. Investigate catalog reachability/row match or zero reported size only if needed; do not substitute an unreliable estimate. |

## Decisions and parked features to preserve

- **scheduled-reboot-stampede / warn-then-force / force-reboot-no-escalation:** same-absolute-instant scheduled reboots and immediate forced reboot semantics were consciously accepted; no unrequested countdown, stagger or second forced dispatch.
- **post-reboot-smb-optout / 1191-fix-inert-on-kerberos-leg / 2016-stage-service-creation:** preserve the documented lane boundaries. DCOM 1191 escalation does not eliminate service creation on all Kerberos/CU paths; consult the [patching lane](windows-patching-lane.md) and [1191 case file](dcom-1191-reboot-fallback-findings.md).
- **shared-settings-stomp-guard / stop-monitor-only-tab:** resolved by F35 and F22 respectively; do not confuse file-write leases with F12 target-operation coordination or add new schedule cancellation behavior to Stop.
- **Manual CU metadata:** keep KB/target UBR operator-controlled; no automatic scan overwrite or reintroduction of the removed approximate-size field.
- **RDP scaling:** session scale stays pinned at 100% with client ZoomLevel; the discarded per-host session-scale toggle is not an open feature.
- **Rare recovery:** update-untick carry-forward and failed-update-rescan Unverified recovery remain parked pending recurring evidence; probe-only self-heal is already shipped.
- **Optional capabilities:** per-machine-local-time scheduling, predefined-column DCOM equivalents, arbitrary-script-over-SMB delivery and extra reboot fallbacks remain parked; build only after a concrete need and scope decision.
- **Reachability / presentation:** broader online-definition changes require an observed problem; custom columns intentionally stay out of the Patching-grid CSV, and no reliable preflight DISM-vs-WUA predictor was established.
- **Resource ownership:** bounded application-lifetime clients/gates are not automatically leaks. Do not dispose semaphores still held by in-flight work, rebuild keep-alive RDP controls, or replace collections without preserving row subscriptions.

## Reconciliation record

- Two lower-cost helper agents inspected separate audit/status and UI/release documentation; parent verified conflicting claims and edited this current index.
- Preserved the old tracker verbatim, corrected F07/F10 attribution, linked later F03/F38/F46–F49 evidence and separated current work from dated release checkpoints.
- No application behavior, release version, Help workflow, deployment state or frozen case file changed; no new runtime/live validation is claimed.
- Documentation verification: snapshot body matches the original tracked backlog (newline-normalized); 103 local link targets and the handoff anchor resolve; only U-21/U-22/U-40/U-42 remain unchecked; all five explicit deferrals are preserved. Separate helper diff review found no false closure or lost deferral. No runtime tests were rerun for this documentation-only change.
