# Vivre performance review — 2026-10-14

> Point-in-time case file — never edited after the fact.
> Baseline: `f60fce943867b5bc900a80f9fa61138de8063f9d`. Assessment and diagnostic tooling only; no application behavior changed.

## Decision summary

- **Start with F13 filtering**, then F14 staging registry and F15 stale subscriptions. These have reproducible local evidence and can reuse existing mechanisms.
- **F16 logging** amplifies a stalled dispatcher; **F17 package preparation** still scales with file count despite F11 streaming payloads. Both warrant focused fixes and adversarial validation.
- **F18–F20 are lower priority:** temporary throttle churn, small retained lock entries, and stale notification queues. None establishes a permanent large memory leak.
- Numbers continue the earlier review: F01–F11 were delivered; **F12 remains explicitly skipped**. These new findings are recommendations, not approved implementation decisions.

## Findings and reusable remedies

### F13 — High at large fleet sizes: filtering blocks the UI thread

- **Evidence:** every text change calls synchronous ICollectionView.Refresh; count-related getters enumerate the view repeatedly. Local runs reproduce increasing per-keystroke costs (measurements below), before any visual layout. The search box updates its source on every keystroke.
- **Source:** `source/Vivre.Desktop/ViewModels/WorkspaceViewModel.cs:492–530`; `source/Vivre.Desktop/WorkspaceView.xaml:287`.
- **Reuse:** retain ICollectionView/live filtering; debounce only free-text typing using the existing DispatcherTimer pattern and cache the visible count after actual view changes. Keep state filters immediate and all collection mutation on the UI thread. Debouncing alone does not eliminate the cost of the final refresh.
- **Accept when:** rapid typing coalesces, final results/counts remain correct, state updates remain timely, disposal stops pending refreshes, and a rendered 319/3,190-row comparison shows improved responsiveness. Profile the remaining refresh before redesigning it.

### F14 — Medium: staging history grows and is repeatedly rewritten

- **Evidence:** registry loading uses a list Contains check for each path (quadratic within a host bucket), and Register saves the full file even for an existing entry. Unique transfer paths accumulate. Initial registration occurs before StageAsync's first await and can run on the UI caller. A 10,000-path bucket produced substantial read/write delays locally.
- **Source:** `source/Vivre.Core/Operations/ProtectedStagingPathRegistry.cs:24–109`; `source/Vivre.Core/Deploy/DeploymentService.cs:63–69,251–256`; UI caller `WorkspaceViewModel.cs:2961–2984`.
- **Reuse:** existing atomic registry writer and cleanup ownership journal; batch registrations, use a HashSet for deduplication, skip unchanged saves, and move persistence off the UI thread. Retire entries only after their owned cleanup is proven complete. Never remove protection merely because an entry is old.
- **Accept when:** duplicate registrations perform no write, concurrent updates survive, large histories remain responsive, and active/recoverable backup paths stay protected through failures. Synthetic same-host history is a stress case, not the user's measured registry size.

### F15 — Medium: replacing a list leaves stale row subscriptions

- **Evidence:** Clear emits Reset without OldItems. The collection handler therefore fails to detach old row PropertyChanged handlers; Dispose only visits current rows. Holding an old row kept a disposed workspace alive; equivalent Remove allowed collection. Old rows can also trigger redundant fleet recomputation after replacement.
- **Source:** `WorkspaceViewModel.cs:928–944,1015,1264–1277,2545–2553`.
- **Limit:** this is conditional retention, not proof of unlimited production growth. Detail windows and in-flight work already retain the workspace through other references. Once all external owners release, the cycle itself is collectible.
- **Reuse:** detach outgoing rows before Clear, or track subscribed rows for Reset-aware teardown; add the existing disposed-state check to dirty scheduling.
- **Accept when:** Remove/Reset/replace/dispose all detach correctly, old-row changes schedule no work, and a retained-old-row control no longer retains the workspace after disposal.

### F16 — Medium: the log entry cap does not bound pending UI callbacks

- **Evidence:** every background message enqueues a dispatcher callback; the 2,000-entry cap applies only after execution. With the dispatcher deliberately paused, 10,000 messages of 1,024 characters queued 10,000 callbacks and approximately 24.6 MiB managed data, versus approximately 4.1 MiB after draining to the entry cap.
- **Source:** `source/Vivre.Desktop/ActivityLog.cs:93–140`.
- **Reuse:** retain ActivityLog and Serilog; use one scheduled drain with a bounded pending UI buffer, batching and an explicit overflow-summary policy. Preserve full durable file logging.
- **Accept when:** stalled-UI bursts have a bounded pending footprint, UI summaries disclose omitted/coalesced entries, ordering remains understandable, and file logging remains complete. This test intentionally creates a stall; it does not prove that such stalls naturally occur.

### F17 — Medium: package metadata work remains expensive and cannot be interrupted mid-preparation

- **Evidence:** Prepare enumerates/hashes all files without a cancellation token. Task.Run's token only cancels work before execution begins. Empty-file tests still take seconds at 10,000 entries; the encoded manifest reaches 1,533,372 characters. Metadata is serialized and sent inline even though F11 bounded archive payload chunks.
- **Source:** `source/Vivre.Core/Deploy/PackagePublication.cs:16–34,115–116,156–180`; `DeploymentService.cs:69`.
- **Reuse:** thread cancellation through existing preparation and incremental hashing; consider the existing chunk transport for large manifests. Preserve SHA256 validation and recoverable publication. No alternate deployment engine is needed.
- **Accept when:** cancellation during hashing stops promptly without publishing partial state; high-entry-count packages keep bounded transport messages and publish correctly through both lanes. Actual remote envelope failure and live cancel latency were not tested here.

### F18 — Low: passive throttle races leave losing waiters queued

- **Evidence:** passive acquisition races two waits but leaves the loser queued until it eventually acquires/releases. With active capacity held, 319/3,190 passive acquisitions left 315/3,186 losing waiters. All drained after active capacity was released; measured drain cost was small.
- **Source:** `source/Vivre.Core/Threading/SplitThrottle.cs:127–170`.
- **Reuse:** fix loser cancellation/permit ownership inside SplitThrottle, preserving reserved capacity. No replacement scheduler.
- **Accept when:** adversarial cancellation/winner races neither leak nor double-release permits, losing queues promptly disappear, and existing capacity/fairness tests pass. This is temporary backlog, not a demonstrated permanent leak.

### F19 — Low: publication lock entries never retire

- **Evidence:** static PublicationLocks retains a semaphore per distinct host/target key; reusing a target reuses its entry. Growth is code-confirmed, magnitude unmeasured; no native semaphore handle growth was established.
- **Source:** `source/Vivre.Core/Deploy/DeploymentService.cs:26,78`.
- **Reuse:** the existing host-operation coordinator's ownership/refcount approach; retire only when no owners or waiters remain. Never evict a live lock.
- **Accept when:** many unique completed destinations return to a bounded idle count without permitting concurrent publication to the same destination.

### F20 — Low: completion banners can become stale

- **Evidence:** an unbounded FIFO displays each completed sweep for about seven seconds. Fifty completed sweeps could take about 350 seconds to drain; this is not fifty machines in one sweep.
- **Source:** `source/Vivre.Desktop/MainWindow.xaml.cs:488–555`; sweep notification `WorkspaceViewModel.cs:2853`.
- **Reuse:** coalesce/cap the existing notification queue and retain a useful completion summary.
- **Accept when:** bursts show recent meaningful outcomes promptly and omitted banners remain discoverable in existing logs. Retained strings are a lower-priority UX concern, not evidence of a large leak.

## Measurement method and evidence

Three sequential local Release runs use actual production classes with isolated dummy data on .NET 10.0.12 / 16 logical processors. No production App startup, windows, server calls, operational changes or settings saves. Workspace construction reads existing settings. No concurrent build/test workload was deliberately introduced. OS load and antivirus were not controlled; timings vary and are not release guarantees.

- [Run 1](vivre-performance-evidence-2026-10-14-run1.json), [run 2](vivre-performance-evidence-2026-10-14-run2.json), [run 3](vivre-performance-evidence-2026-10-14-run3.json).
- Reproducible tool and command: [tools/PerformanceAudit](../tools/PerformanceAudit/README.md). Uses existing project dependencies, not a new benchmark framework.
- Run 1's 2,000-message after-drain heap delta is **excluded**: prior probe callbacks collected unrelated objects. Runs 2/3 add an initial dispatcher drain/GC to settle that baseline. Other timing variation remains visible in the raw records.
- No DataGrid/layout/render costs are included. Filter timings include one explicit visible-count read. Logging bypasses the disk sink and deliberately blocks dispatcher processing during production. Memory figures are managed heap deltas or allocations as labeled, not whole-process peak/RSS. Throttle queue counts inspect runtime internals; logging callback counts derive from one callback per completed submission.

| Probe | Repeated local result |
|---|---|
| Filter, 319 rows | 16.7–103.0 ms/key; per-run medians 61.8, 25.9, 41.3 ms. |
| Filter, 3,190 rows | 388.6–855.4 ms/key; per-run medians 581.3, 465.0, 591.7 ms. |
| Filter, 10,000 rows | 1367.7–2411.3 ms/key; per-run medians 1721.0, 1812.7, 1739.8 ms. |
| Registry, 10,000 paths in one host bucket | Reads 488–864 ms; duplicate full save 509–826 ms. |
| Manifest, 10,000 empty files | Preparation 13.1–15.8 s; 1,533,372 base64 characters; approximately 18.4 MiB allocated for preparation plus encoding (not retained peak). |
| Log, 10,000 × 1,024-character messages, paused UI | Corrected runs: approximately 24.6 MiB queued managed data, 4.1 MiB after drain; drain 163–412 ms without visual controls. |
| Reset retention | All three runs: Remove → collected; Clear/Reset → alive while old row deliberately retained. |
| Throttle, 3,190 passive acquisitions under contention | All three runs: 3,186 losing waiters; zero after release/drain; drain 7.5–14.2 ms. |

## Validation gaps: what remains unproven

| Area | Existing evidence / remaining gap |
|---|---|
| F01/F09 cleanup | Local guard/fixture coverage exists. Live service/delete behavior, restrictive ACLs and junction handling still require operational validation. |
| F02/F03 confirmation/reboot | Local tests do not prove actual interactive confirmation and SCM/RPC/reboot behavior on a server. |
| F04 RDP/state | Local persistence/DPAPI tests exist; native COM session/close/reconnect soak and power-loss/ACL/multiprocess behavior remain unverified. |
| F05/F11 staging | Local publication, recovery and chunk tests exist. Live SMB/WinRM, EDR interaction, crash/power loss and multiple-process publication remain unverified. Four conditional reparse cases returned early because symlink privilege was unavailable; the runner's zero skips must not be read as coverage. |
| F06 WUG | Dummy Windows PowerShell 5.1 process tests exist; real inventory/maintenance integration remains unverified. |
| F07/F08 remote access | Fake-connection tests do not establish real AD/Kerberos repair or DCOM software-query behavior. |
| F10 shutdown | Local save/flush tests exist; actual close/tray warning interactions remain unexercised. |
| Memory/performance | Earlier F11 forced-GC payload measurements exclude native/OS memory. This audit adds metadata and queue measurements, not a full-app memory ceiling, rendered UI trace, natural freeze capture or native RDP soak. |
| Deferred scope | F12 Server 2016 concurrency remains skipped by prior decision. This audit does not silently reopen it. |

For the next operational performance validation, reuse the existing [freeze playbook](freeze-hunting-playbook.md), its watchdog instrument tag and `tools/Get-VivreFreezeLog.ps1`. Capture a control and actual slow interaction, UI/background progress, process working set/private bytes, and RDP/native handles across repeated open/close cycles. Live server operations need their separately authorized target/window; none were run here.

## Sweep coverage and checks that should stay intact

The repository-wide pattern sweep covered approximately 404 tracked C#/XAML/PowerShell files, followed by targeted source reads of Desktop collections/events/timers, logs/settings, RDP/tab lifetimes, deployment, throttles, remoting, monitor/update loops and worker processes. This is broad static review plus focused measurements, not a claim that every line or operational path was exercised. An independent review challenged the workspace findings and narrowed the retention claim above.

- Preserve ThreadPool.SetMinThreads(64,64), the previously diagnosed freeze fix; do not relabel unrelated costs as that historical failure.
- Preserve intentional TabControlEx RDP keep-alive. RDP control teardown, timer/event detachment and close disposal are present; no code-proven native leak emerged, but a native soak is still needed.
- Existing host gates/shared throttles bound work. Deferred native-runspace disposal avoids disposing in-flight native operations. WUG async pipe draining/timeouts, watchdog cancellation and incremental agent-log reads are useful existing designs.
- Row recycling/virtualization is present. Column/layout tuning requires a rendered A/B trace; missing local XAML settings alone do not establish effective theme defaults or a freeze cause.
- Watch only, without a new finding/fix: minute-based relative-time notifications on kept-alive tabs; save-queue growth during a stalled writer; transport-cache growth across changing credential identities; accumulated PowerShell output during verbose long commands. Their practical cost was not measured. Catalog cancellation poisoning is not an active UI finding because the current UI call supplies no token.

## Delivery/acceptance ledger

| Requested capability | Status |
|---|---|
| Review recorded validation gaps | Completed against the existing decision ledger; unresolved operational gaps listed above. |
| Broad performance/lifetime sweep | Completed static sweep and targeted independent refutation; live/native/rendered behavior explicitly unverified. |
| Performance run | Completed isolated production-class probes, controls and repeated raw evidence. |
| Concise numbered reusable recommendations | F13–F20 recorded; no production fixes authorized by this assessment. |
| Validation of audit tooling | Release audit build: 0 errors, one existing signing-certificate warning. Three probe runs completed. Focused existing Release tests: runner reported 31 passed in 10.8 s (StagingArchive, SplitThrottle, OrderedSaveQueue, HostWinRmGate). The archive reparse test can return early without symlink privilege; its guard coverage is not established by this pass count. |

Prior baseline full Debug suite: 1,441 passed (4m46s), with the four privilege-conditional early returns noted above. That is reused historical evidence, not a fresh full-suite execution for this report. Production source is unchanged by this audit.

Fresh archive test samples: 8/32/96 MiB payloads yielded approximately 3.8/3.7/3.7 MiB sampled live managed memory. This supports the existing bounded payload path only; it does not cover file-count metadata growth or total process memory. Focused tests ran after the timed probes, not concurrently.

Focused test command (repository-root PowerShell):

```powershell
dotnet test source/Vivre.Core.Tests/Vivre.Core.Tests.csproj -c Release --filter "FullyQualifiedName~StagingArchiveTests|FullyQualifiedName~SplitThrottleTests|FullyQualifiedName~OrderedSaveQueueTests|FullyQualifiedName~HostWinRmGateTests"
```
