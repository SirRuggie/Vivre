# Vivre — Code Review Findings

**Date:** 2026-05-31  ·  **Scope:** full solution (`Vivre.Core`, `Vivre.Desktop`, `Vivre.UpdateAgent`, `Vivre.Core.Tests`)
**Method:** 8 independent dimension reviewers (modern-C#/.NET 10, architecture/SOLID, async/concurrency,
remoting/COM lifetime, security/injection, WPF/MVVM, error handling, test coverage) → **adversarial
verification of every finding** against the actual code and the project's real threat model → synthesis.
47 findings were raised; **39 survived verification** (8 were rejected as false positives or threat-model
mismatches), plus 1 from a completeness pass. **This is a document-only review — no code was changed.**

Severity tags: **[CRITICAL]** correctness/safety/data-loss · **[IMPROVEMENT]** real bug or worthwhile fix ·
**[SUGGESTION]** lowest actionable tier (nit / consistency / regression-guard).

> **Status (2026-06-01):**
> - **Batch 1 — done.** Remediation-plan items **#1–#5** (DISM KB token match, DISM stderr drain,
>   activity-log write isolation, agent progress-write resilience, `PSDataCollection` disposal) plus
>   the trivial cleanups (stale `IsUninstallable` doc, `InverseBooleanConverter` `object?` signature,
>   `PatchService` host guards + dead `?? string.Empty`, `_activeCts` UI-thread invariant comment).
> - **Batch 2 — done.** Test-coverage items **#6–#9**: the load-bearing install/uninstall streaming
>   controller (`RunWorkerTaskAsync` — heartbeat filter, last-status-wins, bootstrap-failed,
>   watchdog, typed-exception arms, user-cancel), per-host release on the fault path, the
>   cross-framework agent-config JSON round-trip, and DISM exit-code translation. Enabled by a
>   test-only watchdog-poll-interval + agent-bytes seam on `WuaUpdateLane` and splitting `AgentConfig`
>   into a linkable POCO + loader. **+15 tests (144 total), all green; clean `-warnaserror` build.**
> - **Batch 3 — done.** #10 (monitor/probe continuations kept on the UI thread — dropped
>   `ConfigureAwait(false)` on the probe awaits), #11 (the agent EXE is SHA-256-verified on the
>   target before it runs as SYSTEM), #12 (fatal `AppDomain` handler writes straight to the file
>   sink), #13 (the reboot-probe bare `catch` now backs off + logs once instead of swallowing
>   persistent failures), #14 (the copy-pasted local-vs-remote predicate consolidated into one
>   `HostName.IsLocal` helper), the per-row dictionary pruning on row removal, and the #16 parser
>   guards (FailedCount / garbled-percent, `InstalledAt` + installed-scope message, case-insensitive
>   host claim, bootstrap integrity check). **+7 tests (151 total), all green; clean `-warnaserror`.**
> - **Deliberately not done** (low value / high churn / debatable): primary-constructor conversions
>   (cosmetic), the `WorkspaceViewModel` god-object extraction (large, risky, touches the load-bearing
>   monitor logic), the context-menu rebuild (review judged the current code fine), the Update-grid
>   left-click focus guard (debatable — "follow the cursor" is arguably the better behaviour), and the
>   orphaned-cleanup `Warn` (would require threading a logger into Core's `WuaUpdateLane` — most
>   invasive SUGGESTION for the least benefit).

## Headline

- **No confirmed CRITICAL issues.** Every finding initially rated CRITICAL was **downgraded** on
  verification: WPF auto-marshals scalar `PropertyChanged` across threads (so the "off-UI-thread Computer
  writes" don't crash), the cross-tab `PatchOptions` sharing is **by design**, the DISM stderr hang
  **self-recovers** at the 6 h task limit, and "no test" is a coverage gap, not a live defect. The
  adversarial pass earned its keep here — it stopped a lot of scary-sounding-but-false escalation.
- The architecture is **coherent** and the load-bearing WUA reliability machinery (persistent streaming
  session, compiled SYSTEM agent, heartbeat watchdog, per-host serialization, boot-busy guard) is
  **intact and well-reasoned**.
- The real work clusters in two places: **error/cleanup paths that only behave on the happy path**, and
  **the load-bearing WUA/install path being the least-tested code in the repo**.
- The single most consequential finding came from the completeness critic, not the 8 dimensions: the
  **DISM uninstall can resolve a ticked KB to the wrong installed package and remove it** (see #1 below).

## Cross-cutting themes

1. **Disposal/cleanup only on happy paths.** `PSDataCollection` never disposed; DISM stderr redirected but
   never drained; WUA uninstall job RCWs leaked on the per-KB exception path. The careful disposal discipline
   elsewhere breaks down on error/streaming branches.
2. **Logging/error-surfacing paths can themselves throw**, defeating the "never die silently" rule: the
   activity-log file write is unguarded inside callers' catch blocks; the agent's in-catch `progress.Write`
   can re-throw out of `Main`; the fatal `AppDomain` handler marshals through a tearing-down dispatcher; the
   reboot-probe monitor swallows persistent errors with a bare catch.
3. **Implicit, undocumented UI-thread affinity** in the per-tab view model — monitor/probe continuations
   resume off-thread after `ConfigureAwait(false)`, and `_activeCts` is a non-thread-safe `List` relying on
   an unstated invariant. Correct today only because of subtle continuation-context capture; fragile to edits.
4. **The load-bearing WUA streaming/install path is the least-tested code in the repo** — `RunWorkerTaskAsync`,
   the watchdog, the cross-framework agent-config JSON round-trip, DISM exit-code translation, per-host-release
   on fault, and `FailedCount` parsing all lack tests (partly because the controller bakes in concrete time).
5. **Layering / DRY drift in the 1901-line `WorkspaceViewModel`** — the local-vs-remote dispatch predicate is
   copy-pasted across 5 files; several VM methods hand-write PowerShell instead of routing through Core.
6. **Defense-in-depth gap on the remote SYSTEM path** — the agent EXE is dropped to world-writable
   `C:\Windows\Temp` and run as SYSTEM with no integrity check; best-effort cleanup can orphan that task/exe
   without surfacing it.

---

# Findings by layer

## Vivre.UpdateAgent (the net48 on-target SYSTEM worker)

### [IMPROVEMENT] DISM uninstall resolves KB → package by unbounded substring match — can remove the WRONG update
`DismHelper.cs:88` (`ResolvePackage`). The needle `"KB"+kb` is matched against the CBS package identity with a
bare `IndexOf(...) >= 0`. CBS identities look like `Package_for_KB5000802~31bf3856…`, and KB numbers vary in
length, so one update's KB can be a digit-prefix of another's package name (`KB5000` matches
`Package_for_KB5000802`); the **first** Installed match wins. This is reached only on the DISM **fallback** —
i.e. exactly the updates WUA refuses to remove — and uninstall is the **irreversible**, confirm-gated operation
the whole lane exists to make safe, so removing the wrong cumulative/security package is a genuine hazard.
**Fix:** match the KB as a delimited token (`(?<![0-9])KB<kb>(?![0-9])`) so `KB5000` can't match `KB5000802`;
if >1 Installed package still matches, prefer the exact-token one and otherwise refuse with a clear reason.
*Trigger is conditional (needs the ticked KB to be a digit-prefix of another installed package), but the fix is
cheap and the consequence is the worst in the report.*

### [IMPROVEMENT] DISM `EnsurePackages` redirects stderr but never drains it — pipe-buffer deadlock hangs the SYSTEM worker
`DismHelper.cs:116`. Both stdout and stderr are redirected, but only stdout is read before `WaitForExit()`. If
DISM writes >~4 KB to stderr (servicing-stack warnings, corrupt packages), the child blocks on the full stderr
pipe and the worker hangs until the 6 h `ExecutionTimeLimit`. `RunDism` (`:158`) already drains **both** pipes —
so this is an inconsistent oversight, not a choice. **Fix:** kick off `StandardError.ReadToEndAsync()` before
`StandardOutput.ReadToEnd()`, then `WaitForExit()`. Apply the same concurrent pattern to `RunDism`'s sequential
reads (`:161`). *Downgraded from CRITICAL: stderr is normally near-empty and the task self-recovers at 6 h.*

### [IMPROVEMENT] Agent's in-catch `progress.Write` can re-throw out of `Main`, killing the worker with no terminal line
`ProgressWriter.cs:54` + `Program.cs:66-72`. `Write()` does unguarded `File.AppendAllText`; `Main`'s catch calls
`progress.Write("Error", …)` again against the same file. A transient sharing violation (AV/indexer on
`C:\Windows\Temp`) makes the second throw escape `Main`, so the worker dies silently and the user only sees the
controller's generic 2-minute timeout. **Fix:** guard the in-catch write (and optionally retry briefly inside
`Write`) so a failing sink can't abort the operation.

### [SUGGESTION] WUA uninstall job RCW (`IInstallationJob`) leaked on the per-KB exception path
`Program.cs:299` (`TryWuaUninstall`). If anything between `BeginUninstall` and `EndUninstall` throws, the catch
returns a reason but never ends/releases the job; looped over every ticked KB, failures accumulate orphaned RCWs.
**Fix:** end/release the job (and per-iteration installer/collection) in a `finally`. *Tolerable in a one-shot
SYSTEM process — finalizers reclaim on exit — but a real micro-leak.*

### [SUGGESTION] Pre-reboot "force-release the RCWs" comment doesn't match the code
`Program.cs:53`. The comment claims it force-releases COM handles, but the code only does
`GC.Collect()/WaitForPendingFinalizers()/GC.Collect()` — there is no `Marshal.ReleaseComObject` anywhere in the
agent. The GC dance usually works but is a weaker guarantee than the prose implies. **Fix:** make the comment
honest, or actually `Marshal.FinalReleaseComObject` the session/searcher/jobs in reverse order. *Cosmetic on a
process about to exit.*

### [SUGGESTION] `AgentConfig.Load` can NRE before any progress sink exists
`AgentConfig.cs:38`. `Deserialize<AgentConfig>` returning null (empty/`null` payload) makes `config.ProgressPath`
throw **before** `ProgressWriter` is constructed, so the controller only ever sees its generic 2-minute timeout.
**Fix:** throw a descriptive exception in `Load()` when the result is null or required fields are missing.
*Config is tool-generated, so the bad-payload path is corruption-only.*

### [SUGGESTION] DISM package name concatenated onto the child-process command line
`DismHelper.cs:42`. `packageName` is concatenated into the DISM arg string. It's machine-parsed CBS identity
(not user input) and `UseShellExecute=false` means no shell, so this is **not** an injection vector — a robustness
nit only. **Fix (optional):** pass args via `ProcessStartInfo.ArgumentList`. *Security framing stripped on
verification — kept as a low-value robustness suggestion.*

## Vivre.Core / Updates (WUA lane + PatchService)

### [IMPROVEMENT] Harden the SYSTEM agent drop — world-writable `C:\Windows\Temp`, run as SYSTEM, no integrity check
`WuaUpdateLane.cs:106` (`BuildBootstrapScript` `:665-690`). The bootstrap writes `Vivre.UpdateAgent.exe` to a
fixed `C:\Windows\Temp` path (overwrite semantics, no oplock guard) then registers a task that runs it as
`S-1-5-18` at RunLevel Highest. `C:\Windows\Temp` is user-writable, so a low-priv user **already on the target**
could win the write→start race to execute code as SYSTEM (LPE/TOCTOU). **Fix:** write with `FileMode.CreateNew`
(a pre-planted file then hard-fails) and/or SHA-256-verify the dropped bytes before `Start-ScheduledTask`.
*The per-run GUID paths defeat blind pre-planting, so exploitation is conditional — but the fix is cheap and the
target is exactly where a foothold-to-SYSTEM is in scope.*

### [SUGGESTION] Best-effort cleanup can orphan a SYSTEM task + exe and never surface it
`WuaUpdateLane.cs:328` (`SafetyCleanupAsync`). The cleanup call is wrapped in an **empty** `catch {}` and the lane
holds no logger; it runs on the very WinRM-failure paths (session-lost / reboot-pending) that can also kill the
cleanup, leaving a SYSTEM task + exe behind invisibly. Brushes against the "no empty catch" rule. **Fix:** emit a
`Warn` naming the host so a stray `Vivre_WUA_*` task is at least visible; optionally sweep leftovers at startup.

## Vivre.Core / PowerShell

### [IMPROVEMENT] Streaming `PSDataCollection` never disposed — leaks a wait handle per remote call
`PSRunspaceHost.cs:241`. `new PSDataCollection<PSObject>()` is created on every `RunInRunspaceAsync` (local,
remote, streaming) but never disposed — it's the lone `IDisposable` in the method not under a `using`, while
`ps`, the runspace, and the CTS registration all are. Leaks a kernel handle and keeps the captured `onOutput`
chain alive until GC, on every scan/install/uninstall. **Fix:** `using var output = …` (it's materialized via
`[.. output]` before return, so disposal is safe).

## Vivre.Desktop / ViewModels (`WorkspaceViewModel` and friends)

### [IMPROVEMENT] Monitor/probe continuations mutate data-bound `Computer` state off the UI thread
`WorkspaceViewModel.cs:1435` (`MonitorRowAsync`) and `:1526` (`ProbeRebootPendingAsync`). Both `await … 
ConfigureAwait(false)` then write `LastStatus / RebootRequired / RebootMessage / WentOfflineAt` on a thread-pool
thread — intermittently, only on the probe branch — in an otherwise UI-affine VM. **No crash today** (WPF
auto-marshals scalar `PropertyChanged`; the activity-log write is already dispatcher-guarded), but it leans on
implicit behavior on a hot self-healing path. **Fix:** drop `ConfigureAwait(false)` on these probe awaits so the
continuations stay on the UI context. *Downgraded from CRITICAL — predicted `CollectionView` exceptions don't
materialize because no collection is mutated here.*

### [SUGGESTION] Install/uninstall `Progress<T>` is constructed off the UI thread
`WorkspaceViewModel.cs:1159`/`:1083`. For rows that wait on the install semaphore, the `new Progress<HostPatchStatus>(…)`
is created after `ConfigureAwait(false)`, so it captures a null `SynchronizationContext` and callbacks run on the
thread pool. **No user-visible defect** — `ApplyStatus` on this path only writes scalar properties (WPF
auto-marshals them), and the collection-mutating branch (`Available`) never streams during install/uninstall.
**Fix (defensive):** capture the UI `SynchronizationContext` at command entry / build the `Progress<T>` before
the `ConfigureAwait(false)` boundary. *Downgraded from CRITICAL; the "misleading" comment is actually correct.*

### [SUGGESTION] `_activeCts` is a non-thread-safe `List` with an undocumented UI-thread-only invariant
`WorkspaceViewModel.cs:65`. Added/removed in `BeginOperation`/`EndOperation` and snapshotted in `Stop()`. Correct
**only** because the owning sweeps await without `ConfigureAwait(false)` (so continuations resume on the UI
thread) — but the sibling `RunOnePatchHostAsync` already uses `ConfigureAwait(false)`, so a maintainer copying
that upward would race `Stop()`'s enumeration and corrupt the list / lose the `IsBusy` reset. **Fix:** document
the invariant at the field, or use a `ConcurrentDictionary`/lock. *(Raised by two dimensions.)*

### [SUGGESTION] Reboot-probe monitor swallows persistent failures with a bare `catch {}`
`WorkspaceViewModel.cs:1581` (`ProbeRebootPendingAsync`). After re-throwing cancellation and back-off-handling
`RemoteShellInitException`, a bare catch also absorbs `RemoteSessionLostException` and any sustained failure (a
credential that started failing, a permanently dead host) for the monitoring lifetime — the one place in the VM
where a real failure leaves zero trace, against the "no empty catch" rule. **Fix:** reuse the existing
first-time/back-off `Warn` pattern, or surface the last probe error to `RebootMessage`/`LastError`.

### [SUGGESTION] Several VM methods hand-write PowerShell, bypassing Core services
`WorkspaceViewModel.cs:562/616/1797/1847` (ScheduleReboot, CancelScheduledTask, RebootForce, FetchOS). These
interpolate scripts and dispatch `_powerShell` directly, unlike every other remote op (which routes through a
typed Core service). Errors **are** surfaced and there's no injection (only a `DateTime` is interpolated), so
it's a layering inconsistency, not a bug. **Fix:** move them into a Core `HostOps` service behind an interface.

### [SUGGESTION] One shared `PatchOptions` is mutated per-tab
`App.xaml.cs:39` → every `WorkspaceViewModel`. A Source/Exclude/Drivers/Scope toggle in one tab changes what
another tab next scans. **This is documented, intentional design** (mirrors the session-only credential model),
and the consequential per-host state (`IncludeKbArticleIds` — which KBs actually get touched) is already isolated
via `Clone()`. The real residual is a **UI consistency** wrinkle: each tab keeps its own toggle display while the
shared value can be overwritten. **Fix (if desired):** per-tab `PatchOptions`, accepting the loss of the shared
behavior. *Downgraded from CRITICAL — not a correctness/safety bug.*

### [SUGGESTION] 1901-line `WorkspaceViewModel` god-object
`WorkspaceViewModel.cs:27`. Owns ~7 responsibilities (sweeps, monitor/reboot state machine, WUA patch engine,
checklist tracking, fleet aggregates, raw-script actions). **Fix:** extract a `HostMonitor` service and a
`PatchRowController`. *Downgraded to SUGGESTION — a known, tolerated trade-off for a single-admin power tool, and
the monitor logic is the load-bearing machinery the conventions say to preserve, so extraction carries regression
risk. Low priority.*

## Vivre.Desktop / Views & infrastructure

### [IMPROVEMENT] Activity-log file write isn't isolated — a logging failure throws into the caller's catch block
`ActivityLog.cs:63`. `Add()` guards the in-memory insert (dispatcher) but calls `_file.Write(…)` outside any
`try`. Since `Info/Warn/Error` are called from inside nearly every catch in `WorkspaceViewModel`, a synchronous
Serilog-sink throw would propagate **out of that catch**, discard the real operation failure, and surface only
the logging error — the "never die silently" mechanism becoming the thing that hides the cause. **Fix:** wrap
`_file.Write` in its own `try` with an in-memory-only fallback. *Low-probability trigger (Serilog routes Write
failures to SelfLog), bounded blast radius — hence IMPROVEMENT not CRITICAL.*

### [SUGGESTION] Fatal `AppDomain.UnhandledException` handler logs through a tearing-down dispatcher
`App.xaml.cs:49`. The handler calls `activity.Error(…)` → `ActivityLog.OnUi` → `Dispatcher.Invoke` during process
teardown; the `Invoke` can throw/block and the subsequent file write is never reached — losing the one line you
most want. (This path mostly fires for off-UI-thread faults, so the cross-thread `Invoke` is the common case.)
**Fix:** on the fatal path, write straight to the Serilog file sink, bypassing the dispatcher; keep the dispatcher
route for the non-fatal nets.

### [SUGGESTION] Update-grid left-click resets `FocusedComputer` on every mouse-up (incl. multi/range select)
`WorkspaceView.xaml.cs:145`. `PreviewMouseLeftButtonUp` unconditionally re-points the side panel at the row under
the cursor on release, fighting `OnGridSelectionChanged` during Ctrl/Shift selection. Only affects which *selected*
machine the panel previews (bulk actions use `SelectedComputers`), so no wrong-target hazard. **Fix:** guard on
single-selection / skip when Ctrl/Shift is held. *Downgraded from IMPROVEMENT — arguably "follow the cursor" is
even the more intuitive behavior.*

### [SUGGESTION] Context menu rebuilt with fresh `Click` closures on every right-click
`WorkspaceView.xaml.cs:168`. `BuildContextMenu` clears and re-adds ~25 `MenuItem`s with new lambdas per open.
Not a leak (cleared items are collectable) and menu-open is human-paced, so it's churn, not a defect. **Fix
(optional):** build the static structure once, refresh only `IsEnabled`/anchor on open.

### [SUGGESTION] Per-row dictionaries not pruned when rows are removed
`WorkspaceViewModel.cs:1662`/`:823`. `RemoveSelected`/`RemoveOffline` drop rows but leave their name-keyed entries
in `_degradedHosts`/`_rebootRecheckBudget`/`_scheduledTasks`. Functionally harmless (only read for live rows;
re-adds overwrite via indexer), bounded by the name universe. **Fix:** `TryRemove` each removed name.

## Modern C# / .NET 10 idioms

### [SUGGESTION] Adopt primary constructors for single-dependency services
`ConfigMgrClient.cs:7`, `HostRebootProbe.cs:14`, `WuaUpdateLane.cs:26`, `PatchService.cs:26` all capture one
injected dependency via a hand-written ctor — the textbook primary-constructor case, and the one modern idiom the
codebase otherwise consistently adopts. **Fix:** `public sealed class ConfigMgrClient(IPowerShellHost powerShell)`.
Leave ctors that do real work (`WorkspaceViewModel`, the seeding VMs) as-is.

### [SUGGESTION] `InverseBooleanConverter` uses a non-nullable `IValueConverter` signature
`InverseBooleanConverter.cs:9`. Style outlier — every sibling converter uses `object?`. *Verified it does **not**
produce CS8767 (WPF's interface is null-oblivious) and the body already handles null, so this is a pure
consistency nit, not the claimed NRT bug.* **Fix:** add `?` to the parameter annotations.

### [SUGGESTION] Dead `?? string.Empty` on a non-null `host` parameter
`PatchService.cs:112`. `TryClaim`/`Release` coalesce a non-nullable `string host`; dead under the contract and
inconsistent with `WuaUpdateLane`, which guards `host` with `ArgumentException.ThrowIfNullOrWhiteSpace`. **Fix:**
add the same guard to the three public methods and drop the coalesce.

### [SUGGESTION] Stale XML doc on `SoftwareUpdate.IsUninstallable`
`SoftwareUpdate.cs:11`. The doc says the Installed scan sets it from "WUA `IsUninstallable` OR a removable DISM
`Package_for_KB`", but that DISM clause was deliberately removed (it over-promised on permanent SSU/cumulative
updates) — removability is now WUA `IsUninstallable` alone. **Fix:** update the comment to match.

# Test coverage

### [IMPROVEMENT] The install/uninstall streaming controller (`RunWorkerTaskAsync`) has no test
`WuaUpdateLane.cs:91`. The most complex, explicitly load-bearing path in Core — watchdog/heartbeat liveness,
heartbeat phase-regression filter, `progressSeen` failure, and the four typed catch arms each with
`SafetyCleanupAsync` — is never invoked by any test (no `InstallAsync`/`UninstallAsync` references in the suite),
and the `FakeHost` stub can't script a terminal sequence. **Fix:** extend `FakeHost.RunRemoteStreamingAsync` to
replay a scripted sequence of `onOutput` lines and optionally throw a chosen exception, then assert the
heartbeat-filter, last-status-wins, zero-progress-fails, exception-mapping, and user-cancel paths. *Downgraded
from CRITICAL — a coverage gap, not a demonstrated defect (the parsing sub-pieces are covered).*

### [IMPROVEMENT] DISM exit-code translation (`0x800F0825`) is untested
`DismHelper.cs:65` (`DescribeDismExit`). Pure `int→string` mapping the whole uninstall-reason surfacing depends on,
incl. the by-design permanent-cumulative case — private and untested; an int-vs-uint sign bug could regress
silently. **Fix:** make it `internal`, link it into the test project (the `BootServicingState.cs` link pattern
already exists), and assert the three branches.

### [IMPROVEMENT] Agent-config JSON contract (net10 writer → net48 reader) never round-tripped
`AgentConfig.cs:38` + `WuaUpdateLane.cs:611`. The writer uses `System.Text.Json`; the reader uses
`JavaScriptSerializer`. Tests assert the JSON shape but never feed the produced string into `JavaScriptSerializer`,
so a property rename/casing change would break install on every target and pass all tests. **Fix:** link
`AgentConfig.cs` into the tests and round-trip the produced JSON.

### [IMPROVEMENT] Per-host serialization release on the fault/cancel path isn't tested
`PatchService.cs:81`. Tests prove a second op is blocked and that `Release` runs on normal completion, but nothing
asserts the host is **claimable again after the in-flight op faults or is cancelled** (the `finally`) — the guard's
whole purpose. Install/uninstall over remoting routinely throws/cancels, so a regression moving `Release` out of
the `finally` would silently wedge a host with all tests green. **Fix:** make the first op throw/cancel, then
assert re-entry.

### [SUGGESTION] Installed-scope scan parsing (`InstalledAt`, install-count message) untested
`WuaUpdateLane.cs:818`. Every `ParseScan`/`ScanAsync` test uses Applicable scope; `InstalledAt` and the
"N installed update(s)" branch — which drive the uninstall UI — have no coverage. **Fix:** add a `ParseScan` test
with an `InstalledAt` property and a `Scope=Installed` `ScanAsync` test.

### [SUGGESTION] `TryParseProgress` doesn't cover `FailedCount` or garbled/overflow percent
`WuaUpdateLane.cs:399`. The "N could not be removed" (`failed>0`, the by-design `0x800F0825` outcome) and
non-integer/overflow percent (→ `null`, not a throw) paths are unasserted. **Fix:** add a `failed:N` case and a
string/huge-number percent case.

### [SUGGESTION] Empty/whitespace + case-insensitive host edge cases for the in-flight guard unspecified
`PatchService.cs:112`. The `OrdinalIgnoreCase` collapse (`HOSTA` == `hosta`, the same physical box) and the
null→`""` coalescing are untested, so a comparer regression could let two installs collide on one host. **Fix:**
add a case-insensitive-blocks test and pin the empty-host behavior.

### [SUGGESTION] `ProgressWriter` JSONL ↔ `TryParseProgress` contract not verified end-to-end
`ProgressWriter.cs:39`. The two halves of the wire contract are tested only in isolation against hand-written
literals. The contract works today (`GetInt` handles `null`), so this is regression-safety. **Fix:** add
`TryParseProgress` cases for a `"percent":null` line and a BOM-prefixed/newline-wrapped line (the line-407 comment
claims BOM handling but nothing exercises it).

### [SUGGESTION] `ScanAsync` partial-output-with-errors path untested
`WuaUpdateLane.cs:44`. `HadErrors==true` **with** output rows falls through to a clean "N available". *Verified
this is near-unreachable (the scan script's per-update errors are swallowed and don't set `HadErrors`), so it's a
pin-the-decision test, not a bug.* **Fix:** add the test to document the intended behavior.

### [SUGGESTION] `RunWorkerTaskAsync` testability is itself a design smell
`WuaUpdateLane.cs:133`. The watchdog bakes in `Task.Delay(15s)` and `DateTime.UtcNow` and builds its own task, so
the safety-critical timeout behavior can only be tested by real-time waiting (≥15s). **Fix:** inject a
`Func<DateTime>` clock and a poll-interval parameter (default 15s) — this also unblocks the watchdog tests above.

### [SUGGESTION] `PSRunspaceHost` behavioural tests boot a real local PowerShell engine
`PSRunspaceHostTests.cs:50`. `Start-Sleep 30` + real runspaces are integration smoke tests in a unit suite (slow,
host-coupled), while the unit-level streaming `onOutput` delivery (`PSRunspaceHost.cs:244`) and in-pipeline
exception translation (`:136`) are uncovered. **Fix:** add a fast local streaming test; treat the remote
translation arm as integration-only and lean on the strong `TranslateRemotingException` unit tests.

---

# Prioritized remediation plan

Ranked by impact-for-effort. Items 1–5 are the "do first" set (real bugs / cheap fixes); 6–9 lock down the
load-bearing-but-untested WUA path; 10+ are robustness and consistency.

| # | Action | Sev | Effort | Where |
|---|--------|-----|--------|-------|
| 1 | **Delimit the DISM KB→package match** so uninstall can't remove the wrong update | IMPROVEMENT | small | `DismHelper.cs:88` |
| 2 | **Drain DISM stderr in `EnsurePackages`** (concurrent read) to avoid the pipe-buffer hang | IMPROVEMENT | small | `DismHelper.cs:116`, `:161` |
| 3 | **Isolate the activity-log file write** so a logging failure can't throw into a caller's catch | IMPROVEMENT | small | `ActivityLog.cs:63` |
| 4 | **Guard the agent's in-catch `progress.Write`** so a transient file lock can't kill the worker silently | IMPROVEMENT | small | `ProgressWriter.cs:54`, `Program.cs:66` |
| 5 | **Dispose the streaming `PSDataCollection`** (one wait handle leaked per remote call) | IMPROVEMENT | small | `PSRunspaceHost.cs:241` |
| 6 | **Test `RunWorkerTaskAsync`** (watchdog, heartbeat filter, typed catch arms) via a scriptable `FakeHost` | IMPROVEMENT | medium | `WuaUpdateLane.cs:91`+`:133` |
| 7 | **Test per-host release on fault/cancel** (re-entry after the in-flight op throws) | IMPROVEMENT | small | `PatchService.cs:81` |
| 8 | **Round-trip the agent-config JSON** across the net10 writer / net48 reader | IMPROVEMENT | small | `AgentConfig.cs:38`, `WuaUpdateLane.cs:611` |
| 9 | **Test DISM exit-code translation** (`0x800F0825` / access-denied / default) | IMPROVEMENT | small | `DismHelper.cs:65` |
| 10 | **Drop `ConfigureAwait(false)`** on the monitor/probe `Computer`-mutation continuations | IMPROVEMENT | small | `WorkspaceViewModel.cs:1435`, `:1526` |
| 11 | **Harden the SYSTEM agent drop** (`FileMode.CreateNew` / SHA-256 verify before run) | IMPROVEMENT | medium | `WuaUpdateLane.cs:106` |
| 12 | **Fatal `AppDomain` handler → write straight to the file sink**, bypass the dispatcher | SUGGESTION | small | `App.xaml.cs:49` |
| 13 | **Stop the reboot-probe bare `catch {}`** from swallowing persistent failures | SUGGESTION | small | `WorkspaceViewModel.cs:1581` |
| 14 | **Consolidate the copy-pasted local-vs-remote dispatch** into `IPowerShellHost` | IMPROVEMENT | medium | `IPowerShellHost.cs` + 5 callers |
| 15 | **Document the `_activeCts` UI-thread invariant** (or make it thread-safe) | SUGGESTION | small | `WorkspaceViewModel.cs:65` |
| 16 | **Add the cheap WUA parser/test guards** (`FailedCount`, null/overflow percent, config null-validation, case-insensitive host-claim, Installed-scope parse) | SUGGESTION | medium | `WuaUpdateLane.cs:399`, `AgentConfig.cs:38`, `PatchService.cs:112` |

Then the remaining SUGGESTIONS as time allows: primary constructors, `InverseBooleanConverter` signature, dead
`?? string.Empty`, stale `IsUninstallable` doc, the left-click focus guard, context-menu rebuild, per-row dict
pruning, the orphaned-cleanup `Warn`, and the `WorkspaceViewModel` extraction (largest, lowest priority).

# What's healthy (don't "fix")

- The WUA reliability architecture is sound and the load-bearing mechanisms are intact — do **not** regress the
  persistent streaming session, compiled SYSTEM agent, heartbeat watchdog, per-host serialization, or boot-busy
  guard while addressing the above.
- 8 raised findings were **rejected** on verification (false positives / threat-model mismatches) and are
  deliberately omitted — notably the claim that off-UI-thread scalar writes crash the bound `CollectionView`
  (WPF auto-marshals them) and that the cross-tab `PatchOptions` sharing is a bug (it's intentional).
- The intended designs are correct for a single-admin desktop tool: in-memory-only credentials, running remote
  PowerShell as SYSTEM, no app authentication, and the one-click routine actions. None of these are findings.
