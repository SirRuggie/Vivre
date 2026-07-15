# Vivre — key file-path map

> **Project knowledge note:** The load-bearing files, so new chats don't re-derive them. Paths are relative to the repo root (SirRuggie/Vivre).

## Patch lane / 2016 LCU lane
- `source/Vivre.Core/PowerShell/PSRunspaceHost.cs` — WSMan connect/execute; `MaxConnectionRetryCount=0` on both sites (the WSMan retry crash fix). **Also the PSModulePath contaminator — see gotcha below.**
- `source/Vivre.Core/PowerShell/HostWinRmGate.cs` — per-host WinRM shell cap (≤4 concurrent/host; background probes capped at 2 so operator-clicked ops always have reserved slots). Acquired at the shell-open chokepoint in `PSRunspaceHost` via a `background` flag threaded through `IPowerShellHost`/`IHostRebootProbe`; the monitor's reboot-pending poll is the only `background: true` caller.
- `source/Vivre.Core/Net/ReachabilityConfirmation.cs` — pure `ConfirmEffectiveOnline(previous, rawOnline, consecutiveFailures, threshold)`: a previously-online box needs 2 consecutive failed probes before the monitor declares it offline (kills false "went offline" blips). Wired into `WorkspaceViewModel.MonitorRowAsync`.
- `source/Vivre.Core/Updates/WuaUpdateLane.cs` — the normal Windows Update lane; owns the agent bytes + `SmbAgentLane`. Install selector lives here (an install is multi-call). Scan emission: the PS scan string AND the `Vivre.UpdateAgent` agent (`Program.cs`) now emit raw `MinSizeBytes`+`MaxSizeBytes` per update (replaced the single rounded `SizeMb`); `SmbAgentLane.ParseScanResultJson` parses both.
- **Update download-size lane** (display-only — nothing reads or acts on the size; replaced WUA's inflated `MaxDownloadSize` worst-case aggregate in the grid):
  - `source/Vivre.Core/Updates/SoftwareUpdate.cs` — the scan record now carries `long MinDownloadSizeBytes` + `long MaxDownloadSizeBytes` (raw bytes; replaced the old `double SizeMb`).
  - `source/Vivre.Core/Updates/UpdateSizeResolver.cs` — pure tiered resolution: `ResolveDisplaySize(catalogBytes?, minBytes, maxBytes)` → WUA `MaxDownloadSize` when `Max>0 && Max≤10 GB` (PRIMARY, matches BatchPatch); `Min` when `Max==0`; the catalog size when `Max>10 GB` (`AbsurdMaxDownloadSizeBytes` — the inflated express/checkpoint-CU aggregate); else `null` (→ dash). `NeedsCatalogLookup(maxBytes)` = `Max>10 GB` gates the network call to absurd rows only. `ArchFromTitle` extracts x64/arm64/x86 from the update title for catalog row selection.
  - `source/Vivre.Core/Updates/CatalogPageParser.cs` (pure) — HtmlAgilityPack parse of the catalog `Search.aspx` result page: reads the hidden `_originalSize` RAW-BYTE spans (ignores the formatted `_size` text), pairs each to its row title; `SelectSizeBytes` picks the architecture-matched row else the largest (full OS package, never a small companion).
  - `source/Vivre.Core/Updates/MicrosoftUpdateCatalogService.cs` (`ICatalogSizeService`) — one read-only **TLS-1.2** HTTPS GET to `catalog.update.microsoft.com/Search.aspx?q=KB#`, ~30 s timeout; per-KB `ConcurrentDictionary<string,Task<long?>>` cache (caches the in-flight Task, so many machines/tabs showing the same KB fetch it once); all failures → `null` ("unavailable"); **log-free** (Core convention — null is the surfaced outcome). One shared instance built in `App.xaml.cs`. Dependency: **HtmlAgilityPack 1.12.4** (`Vivre.Core.csproj`).
  - Display + fill: `SelectableUpdate.DisplaySizeMb` (computed via the resolver from the observable `CatalogSizeBytes`, wired with `[NotifyPropertyChangedFor]`) is grid-bound with `TargetNullValue=—` in `MainWindow.xaml` + `ComputerDetailWindow.xaml`. `WorkspaceViewModel.ResolveCatalogSizesAsync` runs fire-and-forget after `ReplaceUpdatesForScope`, resumes on the UI thread (no `ConfigureAwait(false)`), and its `Where` requires `NeedsCatalogLookup`, so a normal fleet scan makes **zero** catalog calls.
- `FullPackageLcuLane` (Vivre.Core/Updates) — the Server 2016 full-package CU lane. `StageAsync`, `VerifyAsync`, `ComponentCleanupAsync`.
- `PatchService.cs` / `IPatchService` (Vivre.Core) — per-host serialization owner (the `_inFlight` guard the LCU lane reuses). The LCU lane lives INSIDE PatchService so Stage/Cleanup/Wave can't collide with a WUA install on the same box.
- `RebootWave.cs` / `IRebootWave.cs` (Vivre.Core) — the wave state machine. Graceful→8min→force escalation, scoped to operator-selected + confirmed boxes only. `RebootAndCommitAsync` takes a pluggable `IRebootReadinessProbe` and `IPostRebootConfirmation` so the wave is reusable across box types, and an optional `IRebootGate` for burst-rate limiting.
- `DcomRebootTrigger` (Vivre.Core) — the ONLY reboot primitive in the C# codebase (wave-only, DCOM `Win32Shutdown`); SMB reboot fallback for Kerberos boxes (64337d1).
- `OrphanRebootServiceReaper` + `RebootServiceReapPolicy` (Vivre.Core/Remoting) — the list-load reaper for orphaned `Vivre_Reboot_<32hex>` services the SMB fallback's best-effort delete can leave behind (`a008747`). Read-enumerate-query-delete ONLY — its advapi32 set deliberately binds no StartService/ControlService/CreateService; deletes exact-name + confirmed-Stopped matches, once per host per session, gated by auto-check-on-load.
- `DcomLcuBuildReader` (Vivre.Core) — reads the UBR over DCOM for Verify.
- `StagePreconditions` (Vivre.Core/Updates) — pure, unit-tested pre-Stage decision predicates: `IsAlreadyStaged` (RebootRequired && StagedThisSession → skip "Already staged — run Reboot Wave"), `IsAlreadyCurrent` (VerifyLcuAsync's verdict == Verified → skip "Already current — skipped"; fail-OPEN on a null/unreadable read), `UnscannedThisSession` (targets whose `LastScannedApplicable` is null → the scan-this-session gate). Wired into `StageLcuRowAsync` (the two skips) and `OnStage2016` via `WorkspaceViewModel.UnscannedStageTargets()` (the gate, shown before the package check).
- **Staged-patching toggle (opt-in 2016 routing) — Core pieces:**
  - `StagedInstallPlanner` (Vivre.Core/Updates) — pure planner. `Plan` partitions an Install set into flagged-2016-not-staged (the dialog set) vs Normal, + per-box Settings-vs-scan CU KB mismatches; `NeedsStageDecision` (the per-box predicate); `PartitionByCurrency` — the pre-dialog already-current split, **fail-open**: a box is excluded only on `LcuVerifiedThisSession` OR a definitive `Verified` UBR read (Unreachable / WrongBuild / null read → stays in the dialog).
  - `Lcu2016CuMatcher` (Vivre.Core/Updates) — identifies the 2016 OS CU KB from a scan's titles. `FindCuKb` (single confident match → the dialog's mismatch warning, returns null when ambiguous) and `CuKbs` (EVERY CU-titled KB → the "Install minor updates only" exclude set, so the CU can't slip through WUA even when the scan lists two CU KBs).
  - `LcuRouting.RebootVerifyLaneFor(int?, bool)` — override-aware lane: a 2016 box verifies via the UBR (Lcu2016) lane ONLY when flagged for staging; a non-flagged 2016 box verifies via WUA. The 1-arg overload is kept for legacy callers (treats every 2016 box as the LCU lane).
- **Transient WUA reach-failure retry (no false-green) — the `0x80072EE2` SLS timeout + the BatchPatch fake-green trap (see windows-patching-lane.md ▸ "Transient WUA reach failures"):**
  - `TransientWuaError` (Vivre.Core/Updates) — pure classifier: is a WUA failure a transient reach hiccup (retry) or terminal (surface at once)? Transient family = `0x80072EE2` + `0x80240438` + the WININET/WinHTTP & WU_E_PT timeout/5xx siblings; auth/config/4xx/install errors excluded. Keys on the HRESULT, **not** the phase. `IsTransient(int)` / `IsTransient(string)` / `FirstTransientToken`.
  - `TransientRetryRunner` (Vivre.Core/Updates) — pure retry driver (injected attempt / delay / onRetrying / buildExhausted): transient + retries-left → calm "retrying" + backoff + re-dispatch; success or terminal → return at once; exhausted → honest `Unreachable`. Wraps the WHOLE operation (service-reg → search → download → install).
  - **Face 2 (non-clean search ≠ up-to-date):** `WuaUpdateLane.ScanAsync` reads the search `ResultCode` (the scan script emits it as a `SearchResultCode` status row) and diverts any non-`orcSucceeded` result to a transient reach failure via `SearchDidNotCleanlySucceed` / `BuildSearchIncompleteMessage` (`OrcSucceeded=2`) **before** the up-to-date path. `SmbAgentLane.BuildScanStatus` does the same for the SMB scan; `Vivre.UpdateAgent` `RunScan`/`RunInstall` write a terminal Error line on a non-clean `ResultCode` (read-only — no install/reboot added).
  - `HostPatchStatus.Unreachable` / `PatchPhase.Unreachable` → reduces to `PatchState.Error` (never green) with the distinct **"Can't reach WU"** chip label (`WorkspaceView.xaml` `UpdatePhase=Unreachable` text trigger).
  - **VM wiring** (`WorkspaceViewModel`): `ScanRowAsync` / `InstallRowAsync` wrap the `_patch` call in `TransientRetryRunner`. `MaxTransientRetries`=3; jittered `TransientBackoffDelayAsync` (60s + up to 15s); **fresh per-attempt** `ScanAttemptTimeoutSeconds`=300s via a linked CTS inside each scan attempt (NOT a shared budget — the (a) fix; the 3 scan dispatch sites dropped the old shared per-host 300s); install re-entry guard so a transient after install began surfaces terminal, never a re-run — the began-flag is `InstallBeganLatch` (Vivre.Core/Updates), a synchronous producer-side `IProgress` decorator (`832aa7f` closed the race where the old UI-posted flag write lost to the retry attempt's thread-pool read).
- `DcomRebootReadinessProbe` (Vivre.Core) — pre-reboot readiness guard (3 signals, fail-safe: unreadable = not-ready). Used for Server 2016 staged boxes to prevent rebooting into the 2-hour TrustedInstaller Stopping hang.
- `BasicReachabilityReadinessProbe` (Vivre.Core) — permissive readiness probe for non-2016 operator-ordered reboots. Always answers Ready; the 2016-specific TrustedInstaller/CBS signals do not apply.
- `IPostRebootConfirmation` (Vivre.Core) — pluggable post-reboot confirmation strategy. Three outcomes: Confirmed (terminal green), Failed (terminal red), NotReady (retry).
  - `UbrConfirmation` — 2016 strategy: reads UBR via `DcomLcuBuildReader` and delegates to `FullPackageLcuLane.Decide`. Same rule as the standalone Verify, so wave and Verify can't drift.
  - `ReadyConfirmation` — non-2016 strategy: queries `Win32_OperatingSystem` via DCOM/CIM. Confirmed = OS stack answered; NotReady = not up yet. Never returns Failed (whether updates took is decided by the WUA rescan).
- `IRebootGate` (Vivre.Core) — rate-limiter interface for reboot issuance. Acquired only around the actual reboot trigger; never held through the offline watch.
  - `RebootTriggerGate` (Vivre.Desktop/ViewModels) — `IRebootGate` wrapping a `SemaphoreSlim` with optional jitter. Shared across all per-box tasks in a wave via the static `_rebootTriggerThrottle`.
- `RebootOutcomeSelector` (Vivre.Core) — pure (no I/O) selector mapping post-reboot rescan counts → one of the `RebootOutcomeMessages` strings. Called from `WorkspaceViewModel.ReportPostRebootOutcomeAsync`.
- TCP-445 reachability probe (`TcpReachabilityProbe`) — drives the offline-detection and online-return watch loops inside `RebootWave`.

## ⚠ Two gotchas that make a Windows PowerShell 5.1 shell-out misbehave (load-bearing, reusable)

These BOTH bit the WUG feature and together ate most of a debugging session. Any NEW code that
writes a temp `.ps1` and shells out to **Windows PowerShell 5.1** must respect BOTH or it will fail
in confusing, non-obvious ways. They are independent — fixing one does not fix the other. (Canonical short version: `CLAUDE.md` § Conventions; the file locations where each fix is applied are listed below.)

### Gotcha 1 — PS7 contaminates `PSModulePath` (module reads as "not installed")
Vivre hosts an **in-process PowerShell 7 runspace** (`PSRunspaceHost`). When the PS7 SDK initializes
that runspace it **rewrites the host process's `PSModulePath`** to PS7's module folders
(`…\Documents\PowerShell\Modules`, `…\Program Files\PowerShell\Modules`, the SDK's own). Any child
process started with `UseShellExecute=false` **inherits that contaminated path** — so a shelled-out
**5.1** child looks only in PS7's module folders, NOT `WindowsPowerShell\Modules`, and
`Get-Module -ListAvailable` comes back **empty for a module that is actually installed**.
- **Symptom:** a 5.1 module reads as "not installed" from inside Vivre, though a plain 5.1 shell +
  `Import-Module` finds it fine. (The original false "WhatsUpGoldPS isn't installed" report.)
- **Fix — do this for ANY 5.1 shell-out:** after building the process env, call
  `psi.Environment.Remove("PSModulePath")`. With no inherited value the 5.1 child rebuilds its native
  path (CurrentUser + AllUsers `WindowsPowerShell\Modules` + `$PSHOME\Modules`) — exactly like a plain
  admin shell. Harmless no-op if it isn't set.
- **Applied at:** both `RunPreflightProcessAsync` AND `RunAsync` in `WugMaintenance.cs`.

### Gotcha 2 — BOM-less UTF-8 script → PS 5.1 reads it as ANSI → it won't even PARSE
This was the REAL root cause of the WUG saga (after Gotcha 1 was already fixed). **Windows PowerShell
5.1 treats a `.ps1` with no byte-order-mark as ANSI (Windows-1252), not UTF-8.** `File.WriteAllText` /
`WriteAllTextAsync` with no explicit encoding write UTF-8 **without** a BOM. So any non-ASCII byte in
the script — an em-dash `—` (UTF-8 `E2 80 94`), curly quotes, `✓`, etc. — is misread as 2–3 garbage
ANSI characters, corrupting the token stream so **the whole script fails to parse before a single line
runs**. Nothing executes, nothing is emitted, and the C# side defaults to a wrong/empty result.
- **Symptom:** parse errors from a temp `Vivre_*.ps1` — "Unexpected token 'X'", "the string is
  missing the terminator", "missing closing '}'" — all pointing at a line that contains a non-ASCII
  character. The script never produces output, so a downstream feature silently shows its default
  (here: the false "isn't installed").
- **Fix:** write the temp script as **UTF-8 WITH BOM** — .NET
  `new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)` (BOM bytes `239,187,191` = `EF BB BF`).
  PS 5.1 sees the BOM and reads the file as UTF-8, so non-ASCII survives.
- **In Vivre:** both 5.1 launchers write through ONE shared helper **`WritePs51ScriptAsync`**
  (`WugMaintenance.cs`) so a new call site can't silently regress it; locked by regression test
  **`WritePs51Script_writes_utf8_with_bom`**. Any new 5.1-shell-out script writer must use that helper
  (or the same BOM encoding).
- **VALIDATION TRAP that hid this for a whole session (meta-lesson):** a PowerShell validation harness
  that writes the test script with `Set-Content -Encoding UTF8` **adds a BOM in 5.1** — so it tests a
  file the real C# launcher (no BOM) NEVER produces, and **passes while the real thing fails**. When
  validating a shelled-out script, reproduce the EXACT bytes the real launcher writes
  (`new UTF8Encoding(...)`), or the test is theater. More broadly: *a validation that doesn't mirror
  the real write/launch path can green-light the very bug it should catch.*

## WUG maintenance (WhatsUp Gold) — RESOLVED, committed
- `source/Vivre.Core/Wug/WugMaintenance.cs` — talks to WUG via the **`WhatsUpGoldPS` PowerShell-
  Gallery module** (NOT the REST API directly) by shelling out to **Windows PowerShell 5.1**
  (`…\System32\WindowsPowerShell\v1.0\powershell.exe`) and running embedded scripts that call
  `Connect-WUGServer` / `Get-WUGDevice` (name/IP → DeviceId) / `Set-WUGDeviceMaintenance`. Runs on
  the **operator's workstation ONLY** — no target/managed box is ever contacted (names map to WUG
  DeviceIds server-side), and there is **NO reboot path**. Holds `RunAsync` (the real set — delegates
  to the internal `RunCoreAsync` seam so the cancel-kill contract is testable with a synthetic script),
  the pre-flight `TestConnectionAsync` / `InstallModuleAsync`, the **read-only STREAMING state read**
  `GetMaintenanceStateAsync` (embedded `StateScript` + `ParseMaintenanceState` →
  `WugMaintenanceStateResult`: a per-input-name, case-insensitive `bool?` tri-state map — null =
  unknown, never faked as not-in-maintenance. In-maintenance = `bestState`/`worstState` equals
  "Maintenance"; the fields are presence-checked via `PSObject.Properties` because on PS 5.1 an
  ABSENT property compares `-eq` to `$false` — a silent false "not in maintenance" otherwise), the
  shared `RunPreflightProcessAsync` launcher (used by TestConnectionAsync, InstallModuleAsync AND the
  state read), `ParsePreflight`, the `WritePs51ScriptAsync` BOM-write helper, and the three stdout
  markers below. **All 5.1 shell-outs strip `PSModulePath` AND write UTF-8-with-BOM** — see the two
  gotchas above.
- **Per-name resolver — identity verify, single-sourced** (`b67ed55`): one shared `ResolveFunctionScript`
  (three PS functions) is spliced ONCE into both the set path (`Script`) and the state read (`StateScript`),
  so they can never diverge on how an input name maps to a WUG device. Outcome is exactly one of
  **MatchedByName / MatchedByIp / NoDevice / Ambiguous / LookupError**. Matching is a normalized,
  case-insensitive, DOT-BOUNDARY compare (`Test-WugNameMatch`) against the device's `name`/`hostName`/
  `displayName` (each PRESENCE-guarded — a missing property can neither satisfy nor throw) plus a
  `networkAddress`-equality clause for IP-literal inputs. It REPLACES the dead `displayName -eq $srv` verify
  (null for FQDN-registered fleets) and the de-facto `$results[0]` pick; the dot boundary rejects prefix
  collisions ("APVSQL1" must NOT match "APVSQL10.domain"). Control flow: name search (error-aware) → exact
  name match → DNS→IP fall-through → exact `networkAddress` match, with NO silent `[0]`. **An errored search
  is a `LookupError` (state UNKNOWN), NEVER a false "no matching device"** — only a clean-empty answer
  everywhere is `NoDevice`, so a struggling server can't masquerade as a fleet of ghosts. On the set path a
  `LookupError`/`Ambiguous` box is excluded from "unmatched" and folds into a FAIL-SAFE honesty report
  (over-reports failure, since re-setting maintenance is idempotent — the one forbidden direction is a
  silent "set" for a box never cleanly looked up).
- **POOLED state read (the speed fix)** — a runspace pool INSIDE `StateResolveLoopScript` (composed into
  `StateScript` after `$resolverText` + `$workerTail`). Concurrency comes from `VIVRE_WUG_CONCURRENCY`
  (absent = 1 = the original, untouched sequential branch); the operator sets it in **Settings ▸ "WhatsUp
  Gold state check — simultaneous lookups"** (`AppSettings.WugStateConcurrency`, default **2**, clamp
  **1–4**), read at call time by `WorkspaceViewModel.GetWugMaintenanceStateAsync` and handed to
  `GetMaintenanceStateAsync`, which `ClampConcurrency`es it into `[1, StateReadMaxConcurrency]`; the in-script
  drain re-clamps 1–4 (defence in depth, so a hand-edited env var can't open an unbounded pool). The shared
  `ResolveFunctionScript` is single-sourced via `$resolverText` — the sequential branch `Invoke-Expression`s
  it into the main scope, each pooled worker EMBEDS the same text (never a second, forked resolver);
  `Process-WugOutcome` (the outcome dispatch + tri-state read + `EmitDevice` + counters) is likewise shared,
  and `__WUGDEV__` lines are written ONLY from the main drain thread (never a worker-thread DataAdded handler).
  - **THE FOUR FAN-OUT TRAPS (named, all honoured):**
    - **T1** — `[System.Net.ServicePointManager]::DefaultConnectionLimit = 32` is set BEFORE the first
      request: .NET Framework defaults the per-host connection cap to **2** and the module never raises it,
      silently throttling any pool otherwise.
    - **T2** — connect **ONCE PER RUNSPACE** (the `if (-not $global:WUGBearerHeaders)` guard), never per
      lookup; each worker reads server/user/pass from the process-global env and builds its OWN session —
      the module's auth globals are NEVER shared across runspaces by reference (the headers dict isn't
      thread-safe; sharing it produced garbage reads under load).
    - **T3** — a **completion-order poll-drain** (emit each result as its handle completes), NOT
      `WaitHandle.WaitAny` (64-handle cap) and NOT submission-order `EndInvoke` (head-of-line blocking would
      starve stdout and trip the stall watchdog).
    - **T4** — `PowerShell.Stop()` is cooperative and can't interrupt a blocked `Invoke-RestMethod`, so there
      is deliberately NO per-lookup Stop plumbing here; the external C# stall watchdog + ceiling stay the
      sole authority over a wedged run.
  - **The cap — default 2, ceiling 4 — and WHY:** the live Gate 0 ramp measured the 1→2 halving as the whole
    win; 2→4→8 stayed flat with per-lookup latency creeping UP (WUG serialises under load), so >4 is pure
    extra load on the one box that monitors the whole fleet for no wall-time gain. Measured live: per-lookup
    ~1.1s (1.0–1.7s); a 324-box run ≈ ~6.5 min sequential, ≈ ~3 min at N=2. **A bulk inventory prefetch was
    measured and PERMANENTLY REJECTED** — one unfiltered pull took 426s for 1469 devices, SLOWER than the
    per-name sequential lookups it was meant to beat (see `docs/vivre-backlog.md` ▸ DONE).
  - **Per-lookup latency tally:** the script records each lookup's elapsed ms in completion order; the summary
    carries `avgLookupMs` + `baselineLookupMs` (mean of the first up-to-5). When the average exceeds 2× the
    baseline it APPENDS "WUG lookups slowed during the run…" to the run summary (plus "consider lowering the
    concurrency setting" at N>1). The C# parser still ignores the two numeric fields — the appended error text
    is the operator-visible signal.
  - **Test seam `VIVRE_WUG_MODULE_OVERRIDE`** (test-only, NEVER set in production): rides the SAME
    `$iss.ImportPSModule(<path>)` path with a lightweight COMMITTED fixture
    (`Vivre.Core.Tests/Wug/Fixtures/WugStubModule.psm1`, copied to the test output) so the pool process tests
    skip the real WhatsUpGoldPS ~8s-per-runspace cold-load; the ONE real-module smoke test omits the override
    so the production `$iss.ImportPSModule('WhatsUpGoldPS')` branch fires and is asserted per worker runspace.
- **Three stdout markers, distinct ON PURPOSE** (never overload one for another):
  - `__WUGP__` (`ProgressMarker`) = a live step line → the activity log; **set path (`RunAsync`) only**.
  - `__WUGDEV__` (`DeviceMarker`, internal) = one per-device state result, streamed by `StateScript`'s
    `EmitDevice` AS each name resolves (matched and unmatched alike). Each line is its own
    `ConvertTo-Json` object, so 5.1 escapes non-ASCII to `\uXXXX` — the payload is pure ASCII on the wire,
    immune to the OEM code page of redirected stdout (never switch to a raw delimited format).
    `RunPreflightProcessAsync` routes these OUT of the summary buffer and hands each to the caller's
    `onDeviceLine` (marker stripped) — even when `onDeviceLine` is null, so the summary parse can never
    mistake a device line for the result line.
  - `__WUGRESULT__` (const `PreflightResultMarker`) = the final authoritative `{ ok, devices[], unmatched[],
    error }` summary, emitted by both the pre-flight and the state `Emit`.
- **Marker-REQUIRED summary parse (`ParseMaintenanceState`) — a false-green guard:** the state parse now
  REQUIRES the `__WUGRESULT__` marker; the old last-braced-line fallback was DELETED. With per-device
  `__WUGDEV__` JSON lines on the wire, that fallback could parse a trailing device line AS the summary
  (no `devices[]`, no `error`) → a fabricated clean-but-empty read, a quiet false green. No marker → the
  fail-open no-result path (unknown-with-error, never "not in maintenance"). `ParseDeviceLine` is kept in
  LOCKSTEP with `AddDevice` (a divergent per-line parser is the likeliest re-entry for the fabricated
  "not in maintenance" bug). **`ParsePreflight` KEEPS its last-braced-line fallback** — it has no streamed
  device lines, so the ambiguity can't arise there.
- **Timeouts — the old `min(60+5·N, 600s)` total cap is GONE**, replaced by two constants:
  - `StateReadStallTimeout` (90s) — a stall watchdog that resets ONLY on a `__WUGDEV__` line (chatter on
    other stdout does NOT reset it) and kills a wedged run, naming the last machine that resolved
    (`ComposeAbortError` → "Stalled after X — 47 of 324 checked (no result for 90s)"); surfaced as
    `WugStallException` (derives from `TimeoutException`).
  - `StateReadCeiling` (45min) — an absolute runaway backstop, sized far above a healthy 324-box run
    (measured ~1.1s/lookup: ~6.5min sequential, ~3min at N=2); the stall timer, not this, is what catches hangs.
- **Aborted read (stall / ceiling / stop) KEEPS the per-device results already streamed** — the partial
  map is snapshot-COPIED under a lock against stragglers still draining from the killed child's async
  output pump (a cross-thread write-during-read on the live `Dictionary` is this codebase's cardinal crash
  class). Unreached rows are stamped the NEW distinct state `WugRowText.NotChecked` = "WhatsUp Gold: not
  checked (read stopped)" — deliberately NOT "unknown" (WUG answered, no definite state) and NOT "no
  matching device" (a name miss). The old "state unknown — {error}" hybrid is gone. All six row strings
  live in **`Vivre.Core/Wug/WugRowText.cs`**, test-locked.
- **Kill-on-cancel (BEHAVIOR CHANGE):** a caller-token cancel now KILLS the `powershell.exe` child in
  BOTH launchers — `RunPreflightProcessAsync` (TestConnectionAsync, InstallModuleAsync, the state read)
  AND the set path's `RunAsync` (via `RunCoreAsync`). Before, a cancelled maintenance SET kept running and
  could still flip WUG maintenance after the UI said "cancelled" (operator-approved, 2026-07-14).
  Regression-tested (cancel → child killed → no further mutation).
- **Result-parse contract (the fix that made errors truthful):** "module missing" is reported ONLY on
  an explicit signal from the script. A timeout / empty output / unparseable output now surfaces the
  **real connection error** instead of a false reinstall prompt. The result line is tagged
  `__WUGRESULT__` so cmdlet chatter can't corrupt the parse, and the script has a backstop trap so a
  crash still emits a structured result (carrying `modulePresent=true`). Validated under real 5.1:
  success → "Connected ✓"; bad creds → "username or password was rejected"; unreachable → "Couldn't
  reach WhatsUp Gold at …"; crash → "Pre-flight error …" — every non-success keeps the module marked
  present.
- `MaintenanceWindow.xaml`(.cs) — the enter/exit dialog. **Test connection** + (hidden-until-needed)
  **Install module** buttons. "Set maintenance" runs the pre-flight FIRST and keeps the dialog OPEN
  until it passes (module present + server reachable + creds valid); only on pass does it close +
  fire the real per-device set fire-and-forget. Reuses the existing `StatusText` line for inline
  messages. The **Reason** field shows only in Enter mode (`e2946de` — a reason is only meaningful
  when entering; retained text restores on switch-back).
- `WugStateWindow.xaml`(.cs) — the right-click **Check WhatsUp Gold state…** dialog (`9569cec`; the
  item appears on BOTH the Health and Patching grids via the shared context menu). Server is
  **read-only, pre-filled from Settings** (no save-back), username/password entered per use; same
  pre-flight gate + Install-module affordance as the maintenance dialog; on pass it fires
  `WorkspaceViewModel.CheckWugStateAsync` fire-and-forget and closes.
- **`CheckWugStateAsync` wiring (the streaming per-row writer):** runs as a PASSIVE operation
  (`BeginOperation(..., registerRows: false)`) so the toolbar Stop LIGHTS (IsBusy) and cancels it — killing
  the child — WITHOUT blocking other sweeps. Per-row writes stream through a `Progress<WugDeviceState>`
  **constructed ON THE UI THREAD** (the dispatcher capture IS the thread-safety mechanism — never write
  `CommandResult` from the stdout reader thread). The stream is the SOLE per-row writer; a post-exit
  reconcile stamps ONLY rows that saw no line (order-tolerant — a straggler landing later overwrites with
  the real result). A generation guard makes a second check supersede the first (old run cancelled + child
  killed, its still-`Pending` rows stamped `NotChecked`). Stop / supersede logs "Stopped — N of M checked"
  (`ComposeStoppedMessage`, test-locked) so an aborted run is never indistinguishable from a completed one.
  Results land per row in the Command result column: in maintenance / not in maintenance / no matching device
  (by IP) / state unknown / **not checked (read stopped)**, + one activity-log summary. **No
  `ConfigureAwait(false)` in `CheckWugStateAsync`** — the dispatcher continuation keeps the post-await
  reconcile + per-row writes UI-thread-safe (same mechanism as `SetWugMaintenanceAsync`); that note STILL
  stands.
- Callers: `WorkspaceViewModel.SetWugMaintenanceAsync` + `CheckWugStateAsync` (over the
  `GetWugMaintenanceStateAsync` wrapper) + `TestWugConnectionAsync` / `InstallWugModuleAsync`.
- **Credential invariant (DO NOT deviate):** the WUG password is a `SecureString` →
  `new NetworkCredential(string.Empty, pw).Password` plaintext → handed to the child **only** via the
  `VIVRE_WUG_PASS` environment variable — never on the command line, to disk, or in a log.
- **SSL:** `Connect-WUGServer … -Protocol https -IgnoreSSLErrors` (self-signed WUG cert) — the
  pre-flight connect-test must match the real run exactly, or it passes/fails differently.
- **Persistence:** only the server address persists (`AppSettings.WugServer`); credentials are NEVER
  saved.
- **Live-confirmed end to end** (10.70.25.111): Test connection → "Connected ✓"; Set → row narrates
  "WhatsUp Gold: maintenance ON/OFF"; and the device shows **Maintenance** state in WUG's own console.

## Software check (installed-software column) — WinRM + DCOM fallback
- `source/Vivre.Core/Software/SoftwareProbe.cs` — the WinRM-first probe (registry Uninstall hives via
  a PS script; never `Win32_Product`). On ANY `IsWinRmUnavailable` failure (Kerberos 0x80090322,
  WinRM stopped, session lost) it reroutes to the injected `IDcomSoftwareReader` (à la `VitalsProbe`);
  if DCOM also fails it throws naming BOTH transports — never a fabricated "not found".
- `DcomSoftwareReader.cs` — read-only StdRegProv-over-DCOM read of the SAME Uninstall hives, ambient
  login only. **Load-bearing RV rules (do NOT copy `DcomLcuBuildReader.InvokeRegRead`, which lumps
  RV=5 into null):** EnumKey RV=0 → enumerate (null `sNames` = benign empty), RV=2 → hive absent
  (benign), RV=5/other → THROW; `Found=false` is legal only when every hive ∈ {0,2} with ≥1
  enumerated. OperationCanceledException rethrows FIRST at every layer (a timeout must surface as
  "check timed out", never "both transports failed"). Structure = `DcomLcuBuildReader`, NEVER
  `DcomVitalsProbe`'s swallow-to-null (Found is a bool that paints the cell red).
- `SoftwareShaping.cs` — pure parity seams: `Match` (DisplayName-OR-Publisher ordinal contains,
  DisplayName-sorted first), `MatchAcrossHives` (first hive with any match wins — never concat+sort),
  `NormalizeServiceState` (Win32_Service "Start Pending" → Get-Service "StartPending").
- VM: `CheckSoftwareRowAsync` gates on `IsGenuinelyOfflineAsync` first (both ping AND ambient DCOM
  dead → clean "Offline" cell, no connection attempt).

## On-box agent
- `source/Vivre.UpdateAgent/Program.cs` — the agent. `AddPackage` mode (DISM-add as SYSTEM, stream %, RebootPending success-check) and `Cleanup` mode. **REBOOT-FREE at the root** — the latent self-reboot + RebootAfter/RebootBehavior plumbing was excised; a grep finds zero shutdown/restart calls.
- `BootBusyGuard.cs`, `BootServicingState.cs`, `Callbacks.cs` — agent boot/servicing-state helpers.
- net48 target (net462 reverted — ValueTuple BCL gap).

## Build & publish (publish.ps1) — how the deployable is produced
- `publish.ps1` (repo root, now `C:\src\Vivre`) — the one command that produces a deployable Vivre. Ruggie runs:
  `powershell -ExecutionPolicy Bypass -File "C:\src\Vivre\publish.ps1" -Zip`
  It wipes the output folder, then runs `dotnet publish source/Vivre.Desktop/Vivre.Desktop.csproj`
  self-contained win-x64 (no .NET runtime needed on the target) into `publish\Vivre-win-x64\`
  (+ a `.zip` beside it with `-Zip`). `-FrameworkDependent` makes a small build that needs the
  .NET 10 Desktop Runtime on the target. There is **no** `--no-build` / `--no-dependencies` /
  `--no-restore` — it is a FULL build of the whole dependency graph from current source.
- **publish.ps1 rebuilds + re-signs + re-bundles the on-box agent automatically. There is NO
  separate agent-rebuild step.** Any change to `Vivre.UpdateAgent` (message strings, new phases
  like "Cleaning") goes LIVE the moment you run publish.ps1. Do NOT flag "agent rebuild debt" —
  it does not exist for the normal publish flow.
- Why that's true (the csproj facts, so they aren't re-derived):
  - `Vivre.Desktop.csproj` —
    `<ProjectReference Include="..\Vivre.UpdateAgent\Vivre.UpdateAgent.csproj" ReferenceOutputAssembly="false" />`.
    The agent is a standalone net48 EXE (not linked into the WPF app), but `ReferenceOutputAssembly="false"`
    keeps it in the build-ORDER graph → it is built first from current source. (`NU1702` is
    suppressed for the intentional cross-TFM reference.)
  - `Vivre.UpdateAgent.csproj` target `SignUpdateAgent` (`AfterTargets="Build"`) Authenticode-signs
    the freshly built EXE; `AppendRuntimeIdentifierToOutputPath=false` pins the output to `…\net48\`
    so the `-r win-x64` publish finds the EXE at a deterministic path.
  - `Vivre.Desktop.csproj` target `CopyUpdateAgentAfterPublish` (`AfterTargets="Publish"`) copies the
    fresh signed `Vivre.UpdateAgent.exe` into the publish folder beside `Vivre.exe`.
- Caveats:
  - **Signing is best-effort.** It signs only if the code-signing cert (thumbprint
    `1A5CE867A4660C271C9C7AA0DD2F923A1FE05953`) is in `CurrentUser\My` on the build machine. On
    Ruggie's dev box it's present → signed. On a machine without the cert it **ships unsigned**
    (still gated by the agent's ACL'd drop dir + SHA-256 self-check, just not Authenticode-signed).

## Build/deploy — repo location (OneDrive trap RESOLVED)
**The repo + publish output now live at `C:\src\Vivre` (out of OneDrive).** This closed the recurring
stale-binary class that ate much of the WUG saga: OneDrive's cloud "placeholder" files used to copy as
stale/empty, so the test box launched OLD code while everyone believed it was fresh; it also caused the
`.git/worktrees` lock and LF/CRLF churn. With the repo on a non-synced path that whole class is gone.
- `.gitattributes` `* text=auto` is in place to keep line-endings stable.
- **Freshness self-check (still a handy general technique** for confirming a test-box copy matches a
  just-built binary): read `Vivre.Core.dll` bytes and look for a marker string unique to the new code —
  `$u16=[Text.Encoding]::Unicode.GetString([IO.File]::ReadAllBytes('<path>\Vivre.Core.dll')); $u16.Contains('__WUGRESULT__')`
  → true = fresh; false = stale, re-copy. No longer a routine necessity now that OneDrive is out of the
  path, but useful when a deploy looks off.

## Desktop / UI
- `ViewModels/WorkspaceViewModel.cs` — the big VM. `InstallRowAsync` (routing inserts at the top), the LCU panel commands, `RebootAndVerifyCommand` (fleet-wide reboot-and-verify on the selected boxes — routes per box via `LcuRouting.RebootVerifyLaneFor`), `UnscannedStageTargets()` (returns 2016 targets that haven't been scanned this session — used by the Stage scan-gate), the filter enum/predicate, the two-bucket completion summary, `_appSettings` access. Also `OnIsUpdateModeChanged` (Health/Patching mode flip + the patch-only-filter reset when entering Health). WUG callers `SetWugMaintenanceAsync` / `CheckWugStateAsync` / `GetWugMaintenanceStateAsync` / `TestWugConnectionAsync` / `InstallWugModuleAsync` live here too. Custom-column sweeps register in `_customColumnSweeps` (CTS + captured spec names, via `RunSweepAsync`'s `onBegin` callback) so `RemoveCustomColumn` cancels a sweep whose every spec is gone; `WrapWithCompletion` does not count cancelled rows (the N/M counter freezes on Stop).
  - `RebootWaveRowAsync` — per-box reboot-and-verify step (routes by `LcuRouting.RebootVerifyLaneFor`; calls `RebootWaveLcuAsync` or `RebootWaveWuaAsync`; post-wave calls `ReportPostRebootOutcomeAsync`).
  - `ReportPostRebootOutcomeAsync` — post-reboot rescan: read-only `ScanAsync` + reboot-pending probe → `RebootOutcomeSelector.Select` → outcome string. Never triggers Install/Uninstall/Reboot.
  - `_waveThrottle` — static `SemaphoreSlim(256)`; concurrency width for the per-box offline-watch loops. Effectively unbounded so all selected boxes watch in parallel — the reboot-and-verify wave uses this (NOT the install/stage `_patchThrottle`), so a slow box's long commit never blocks a fast box's verify/report.
  - `_rebootTriggerThrottle` — static `SemaphoreSlim(12)`; caps simultaneous reboot *issuance* across the fleet. Shared across tabs to protect DCs/DNS/auth from a burst of simultaneous drops.
- `ViewModels/RebootTriggerGate.cs` — `IRebootGate` impl wrapping `_rebootTriggerThrottle` with optional jitter. Released the instant the reboot is issued, never held through the watch.
- **Fleet-wide reboot-and-verify entry point:** the grid right-click **Reboot & verify…** item (`WorkspaceView.xaml.cs` `OnRebootAndVerify` → confirm dialog → `RebootAndVerifyCommand`) is **Patching-mode-only** (gated by `vm.IsUpdateMode`, like the Scan/Install shortcuts). The 2016 LCU action-bar **Reboot Wave** button re-points to the same `OnRebootAndVerify` handler; it now enables on any selection (`RebootWaveButton.IsEnabled = SelectedComputers.Count > 0`), no longer 2016-only.
- `ViewModels/ShellViewModel.cs` — `CloseTab` and tab/list management.
- `WorkspaceView.xaml`(.cs) — ONE view, mode-swapped by `IsUpdateMode` (Health = `IsMachineMode`; Patching = `IsUpdateMode`), with two DataGrids that swap by visibility. The filter chips live in **two separate mode-gated StackPanels** (Health bar = 6 chips; Patching bar = full set incl. Updates, Server 2016, Not scanned, Scheduled). The LCU action bar (Border) is gated to Patching via a 3-condition MultiDataTrigger (ActiveFilter==Server2016 AND HasServer2016 AND IsUpdateMode). Status-pill label renames are **DataTrigger overrides in the Patching Status column only** — never edit the shared `PhaseChipLabelConverter` (it's also used by `ComputerDetailWindow.xaml`, so editing it leaks renames into the detail window / Health context). Cold-start responsiveness (1.14.2): a debounced (~150ms) re-layout on `SizeChanged`/`Computers.CollectionChanged` re-measures+re-arranges the grid so it isn't blank until a manual resize (`19f766b`); and `WorkspaceViewModel.AddComputers` defers the auto-check kickoff (vitals sweep + custom-column fill) to `DispatcherPriority.Background` so the grid paints before the sweep prologue (`0bfd362`).
- `MainWindow.xaml`(.cs) — bottom-dock open/size: the single mode-labeled `ActivityLogToggle` is the ONLY thing that opens the dock (no auto-open on row select); reopen honors the operator's saved `BottomDockHeight` as-is, floored against `WorkspaceGridMinHeight` so the grid can't vanish (`WorkspaceGridRow.MinHeight` bounds the splitter drag too). **Now hosts the completed `ui:NavigationView` shell** (LeftCompact pane + hamburger; Fleet → Health/Patching sub-items; Scripts; Cross-Domain RDP; Settings pinned bottom; mode chips + menu bar removed). The NavigationView refactor incl. Phase 4 is DONE — TODO: capture the as-built shell layout here in detail next time this file is touched.
- `AdaptiveLayoutController.cs` — layout controller.
- `SettingsPage.xaml`(.cs) — Integrations, Help & about. The LCU package folder + "This month's CU" (KB / target UBR) fields live here, plus the **Staged patching machines** card (lists the flagged hosts; per-row Remove + Clear all; re-seeds on expand; a remove/clear calls `MainWindow.ResyncStagedPatchingFlags`).
- `App.xaml`(.cs) — composition root. `App.OnStartup` also raises **`ThreadPool.SetMinThreads(64, 64)`** — load-bearing: the per-host WinRM open is a blocking `Task.Run(runspace.Open)`, and on a low-core box the pool's default min workers (= CPU count, e.g. 2) inject ~1 thread/500ms, serializing the ~28 sweep opens behind the slowest connect and freezing the UI on cold start. The min-floor lets the already-bounded opens run in parallel. **DON'T delete** (won't repro on a many-core dev box). See `docs/cold-start-freeze-and-threadpool-findings.md`.
- Converters: `EnumEqualsConverter.cs`, `UxConverters.cs`, `PhaseChipConverter.cs` (class `PhaseChipLabelConverter`; SHARED — Patching Status column + ComputerDetailWindow; do NOT edit for Patching-only label changes). Help text: `HelpContent.cs`.
- **Dialog sizing standard** (audit `fe4d68e`): modals use `CenterOwner`; fixed-content forms use `SizeToContent` + Min/Max (NoResize OK); content-heavy/list dialogs use `CanResize` + a ScrollViewer with the action buttons in their OWN row OUTSIDE the ScrollViewer (so they're always visible). `SoftwareCheckWindow` uses `SizeToContent="Height"` + `MaxHeight` so it opens fully visible and only scrolls on a too-short screen. Sizing attributes only — **never bind `Run.Text`** (the a0cb80a render-break class).
- `Computer.cs` (lives in **Vivre.Core/Models**, not Desktop — listed here because the UI binds it) — `OsBuild` populated in `ApplyVitals` — the 2016 predicate is `LcuRouting.Is2016(int?)` (Vivre.Core/Updates), the single source of truth for both the panel filter and routing; it is **not** a property on `Computer.cs`. `PatchState` derives from `UpdatePhase` + `RebootRequired`. `IsScheduled => ScheduledNextRun is not null`. `PatchPhase.Cleaned` → `PatchState.Done`. Also `LastInstallInstalledCount` / `LastInstallFailedCount` — runtime-only, non-observable `int?` counts: stamped by `InstallRowAsync` only for a REAL install outcome (Done/PendingReboot with a nonzero count; never a schedule registration or failed attempt), consumed (nulled) by `ReportPostRebootOutcomeAsync` after the post-reboot outcome message reports them once. **`RequiresStagedPatching`** (observable) — the operator's per-box opt-in for the 2016 DISM staging lane; seeded from `AppSettings.StagedHosts` on row add, drives routing + the Staged column. **`LcuVerifiedThisSession`** (runtime-only, non-observable) — set when a 2016 box's CU is confirmed at the target UBR this cycle (verify, 2016 reboot-wave commit, or the pre-dialog already-current check); cleared on re-stage. Lets the staged-update dialog skip an already-current box.
- **Staged-patching toggle — Desktop pieces:**
  - `StagedInstallDecisionDialog.xaml`(.cs) — the "Server 2016 staged update required" dialog: **Stage CU first** / **Install minor updates only** / **Cancel**, the Settings-vs-scan KB-mismatch warning, and the inline minor-only reboot caution (Proceed / Back). Returns a `StagedInstallChoice`.
  - `StagedInstallInteraction.cs` — the View-layer gate **every** Install entry point routes through (`MainWindow.RunInstallFlowAsync`, the right-click *Install selected*, and — as a safe skip-with-guidance fallback — *Install checked*). `ResolveAsync` plans → runs the already-current pre-check + re-plan → shows the dialog → carries out the choice (the flagged action + the normal install on the rest, concurrently). Cancel skips ONLY the flagged boxes; the rest of the fleet still installs. Also `RunStageWorkflowAsync` (the shared chip-Stage workflow: scan-gate + package-readiness loop + stage).
  - `WorkspaceViewModel` staged-patching methods: `PlanStagedInstall`, `ResolveAlreadyCurrentAsync` (the pre-dialog UBR currency check via `_patch.VerifyLcuAsync`, bounded by `_remoteSweepThrottle`, fail-open), `StageLcuForAsync` / `InstallMinorOnlyAsync` (the dialog's two actions), `SetStagedPatching` (toggle the flag + persist to `StagedHosts`), `Server2016Targets()` is now flagged-only, and `HasStagedServer2016` (drives the Staged column visibility, re-tallied on row add + on a `RequiresStagedPatching` change). `InstallRowAsync` has a `minorOnly` param + a flag-aware 2016 branch (non-flagged → WUA).
  - `MainWindow.ResyncStagedPatchingFlags` — re-seeds every loaded row's `RequiresStagedPatching` from `StagedHosts` after a Settings remove/clear, so an edited list never leaves a stale flag.
  - `WorkspaceView` `StagedColumn` — the narrow "Staged" pill column (visible only on flagged 2016 rows; neutral styling, distinct from the amber "STAGED — needs Reboot Wave" tag). A `DataGridColumn` can't bind `Visibility`, so the View drives it from code-behind via the VM's `HasStagedServer2016` (`OnVmPropertyChanged` / `UpdateStagedColumnVisibility`). `BuildContextMenu` adds the **Mark as Staged patching** / **Remove Staged flag** items (2016 + Patching only, acting on the right-clicked row).

## ⚠ Computer.cs observability + the live-filtered grid (load-bearing, reusable)

**Stale-in-an-open-panel = a non-observable property.** If a value shows correctly in a freshly-opened
Machine Details panel/tab but won't update in place after a re-check (e.g. Check Vitals), the property it
binds through isn't raising PropertyChanged. Two flavors: a plain auto-property (`Vitals`, `VitalityReasons`
— fixed `5e6ddee` via `[ObservableProperty]`) or a computed property with no notify (`VitalsSummary`,
`LastRebootDisplay`, `MonthlyCuDisplay`, `LcuPackagesFolder` — still deferred where only the grid reads them).
Fix = make the *container* observable: one `[ObservableProperty]` on `Vitals` re-resolves every `Vitals.*`
reading at once.

**Before making ANY `Computer.cs` property observable, run this 2-question safety check** (the `7d8abd4`
cross-thread crash was an off-thread write to a *live-filtered* property re-shaping the grid's CollectionView
on the wrong thread):
1. **Is the property in the live-filtered set?** (the predicate inputs in `WorkspaceViewModel.cs` ~855-862:
   `Name`, `IsOnline`, `PatchState`, `RebootRequired`, `LastError`, `UpdateError`, `UpdatesAvailable`,
   `MissingUpdates`, `VitalityBand`, `OsBuild`, `UpdatePhase`, `ScheduledNextRun`.) If YES, a change re-shapes
   the grid → it MUST be written on the UI thread (marshal, or route via `IProgress`).
2. **Is the write on the UI thread?** Confirm the call path keeps the UI `SynchronizationContext` (no
   `ConfigureAwait(false)` / `Task.Run` upstream) — and remember callbacks handed to a Core runner run on the
   runner's `ConfigureAwait(false)` context, so those must marshal.

Non-live-filtered + on-UI-thread (`Vitals`, `VitalityReasons`) = safe to make observable — the opposite
direction from the crash.

## Cross-Domain RDP
- `source/Vivre.Desktop/RdpSessionView.xaml.cs` (+ `.xaml`) — the embedded RDP host; owns control creation,
  `LocalScale()` (pinned to `(100,100)` for the FCM fix, `a7b8833` — THE PIN CARDINAL, read at exactly two
  sites: the connect block and `ResizeRemote`; gate greps after any RDP commit: `= LocalScale();` → exactly
  2, `_rdp.UpdateSessionDisplaySettings` → exactly 1), the client-side **ZoomLevel** magnification (logical
  framebuffer, SmartSizing off while zoomed; zoom parks to 100 BEFORE a full-screen entry attempt — mstsc's
  order — and a failed switch is logged + un-latched in both directions), and the **verified re-fit engine**
  (spaced sends, read-back verify + retries, even-both-dims sizes per MS-RDPEDISP, sends deferred while a
  drag or mouse button is live). Control stack: WPF → `WindowsFormsHost` (`RdpHostElement`) → WinForms
  `Panel` → `AxMsRdpClient9NotSafeForScripting` (the v9 OCX).
- `source/Vivre.Core/Rdp/RdpDisconnectClassifier.cs` — pure, unit-tested disconnect classifier
  (keep-by-default, close-by-exception): the tab closes ONLY on `ExtendedDisconnectReasonCode` 12
  (LogoffByUser — measured on both sign-out paths) while connected with no auto-reconnect in flight;
  codes 4/6 (the old wrong-enum silent closes) and ALL unknown codes KEEP the tab, and
  `GetErrorDescription` is reachable only for the error outcome (via `Message` — contract test-pinned).
- `source/Vivre.Desktop/ViewModels/RdpSessionViewModel.cs`, `ViewModels/CrossDomainRdpViewModel.cs` — the RDP
  session + host-tree view-models.
- `source/Vivre.Desktop/CrossDomainRdpView.xaml`(`.cs`) — the Cross-Domain RDP UI (host tree + session tabs);
  per-host settings resolve via `_creds.Resolve(host, RdpTree.AncestorsOf(_tree, host))` in `ConnectTo` — the
  hook a future per-host scale setting would use.
- **Note:** the Failover Cluster Manager context-menu fix is the 100%-scale pin (`a7b8833`); embedded-RDP
  magnification **shipped** (client-side zoom; the session stays at 100% so FCM keeps working) — see
  `vivre-rdp-scaling-and-fcm-findings.md` for the full arc, the re-fit engine, and the instrument lessons.

## Settings / data
- `AppSettings` (Vivre.Desktop, in `AppSettingsStore.cs`) — LCU package folder (`C:\Vivre\VivrePackages`), This-month's-CU (KB + target UBR), defaults KB5094122 / 9234. Also `WugServer` (the only persisted WUG field — credentials are never saved). **`MonthlyCu.ExpectedSizeMb` was REMOVED** — it was display-only; the package is matched by KB + architecture, never size. **`StagedHosts`** — `HashSet<string>` (OrdinalIgnoreCase) of host names flagged for the 2016 DISM staging lane (the source of truth behind `Computer.RequiresStagedPatching`); **`ReadFromDisk` re-normalizes it via `StagedHostMatching.Normalize`** because a JSON round-trip resets the comparer to ordinal. Pure case-insensitive membership helpers live in `StagedHostMatching` (Vivre.Core/Updates).
- `source/Vivre.Core/Computers/ComputerListStore.cs` — the computer list store.
- `source/Vivre.Core/IO/AtomicFileWriter.cs` — crash-safe whole-file write (same-dir temp + `File.Replace`; `File.Move` first-write fallback). Behind BOTH `AppSettingsStore.Save` (`d600009`) and `ComputerListStore.Save` (`1add64f`) — a crash mid-write leaves the prior good file (settings.json / a named list) intact. Callers serialize their own writers.
- `RebootOutcomeMessages.cs` (Vivre.Core) — the 8 ready-to-use reboot-and-verify outcome strings ("Back online · installed N · up to date", "… N remaining", "… N failed", `BackOnlineRescanFailed()` → "Back online · couldn't rescan — re-check", and `BackOnlineRebootUnknown()` → "Back online · couldn't confirm reboot state — re-check"). Wired via the pure, truthfulness-first `RebootOutcomeSelector.Select` (tri-state `bool?` reboot-pending; nullable consume-once install counts — the "installed N" clause is omitted when no meaningful install ran) → `WorkspaceViewModel.ReportPostRebootOutcomeAsync` — a scan failure, probe failure/timeout, or failed updates never read as a clean "up to date".

## Pure decision helpers (Vivre.Core/Updates)
Extracted UI/IO-free predicates, each unit-tested:
- `SoftwareShaping` (Vivre.Core/Software) — the software check's match/sort/service-state parity seams (see the Software check section above).
- `RebootVerifyMenu.ShouldOfferRebootVerify(Computer, bool isUpdateMode)` — per-row visibility of the right-click **Reboot & verify…** item.
- `Lcu2016RowState` — maps terminal/in-flight agent status onto a 2016 staged-patching grid row; enforces the load-bearing **Deferred ≠ Staged** invariant.
- `ScopeToggleRule` — on the Applicable/Installed scope-toggle, preserves a row's existing message for terminal + in-flight states instead of swapping in the target scope's cached scan message.
- `ComponentCleanupClassifier` / `ComponentCleanupMessages` — 2016 component-cleanup outcomes, incl. the `CleanedFilesLocked` access-denied case (DISM exit 5 = EDR/AV holding WinSxS handles → neutral **Cleaned**, not red).
- `ScheduledTaskCancelOutcome` — verify-by-absence for the scheduled-task cancel: the Scheduled chip clears ONLY on an exact full-line `REMOVED` with a clean error stream; anything else keeps it ("task may still fire") — the no-false-green rule on a reboot path.
- `ScheduleRegistrationOutcome` — the register-side ASYMMETRY: an unconfirmed reboot-schedule registration (timed out, dropped mid-request, cancelled mid-request, or any unprovable escape — `IsUnconfirmedFailure` buckets the thrown types) is treated as SCHEDULED ("couldn't confirm — verify on the box"), never silently unscheduled; a row goes dark ONLY on proof the command never ran (connect-phase loss / Kerberos / shell-init) or the box's own failure report.

## Tests
- `source/Vivre.Core.Tests/...` — **897 green** (as of 2026-07-15, WUG pooled-state-read working tree) — run `dotnet test` for the exact count; the increments below are point-in-time history (344 at the WUG resolution; 360 after the pluggable-wave
  refactor; +7 across the reboot-and-verify build; +11 across the smart-scan build; +49 across the
  staged-patching toggle; +61 across the transient WUA retry / no-false-green build — `TransientWuaError`,
  `TransientRetryRunner`, the WinRM + SMB non-clean-search "never up-to-date" tests, and the
  `PatchPhase.Unreachable`→Error mapping; +10 WUG maintenance-state parse tests in `3f8ada1` —
  `WugMaintenanceStateParseTests`: tri-state true/false/null, single-object `devices` shape, marker
  extraction, case-insensitive keys, fail-open on malformed/empty output; +26 software-DCOM-fallback
  tests in `f4fad69`/`fa837e6` — `SoftwareShapingTests` (name/publisher match, cross-match-type sort,
  hive precedence, empty-≠-failed, service-state normalization) + `SoftwareProbeRoutingTests`
  (Kerberos AND session-loss reroute to DCOM, double-failure throws naming both transports — never a
  false Found=false, OCE propagates unwrapped, no-reader passthrough)). Includes the wave behavior tests
  (graceful→forced, not-ready refusal, rollback=red, late-return-still-verifies-green, never-returns=red,
  **per-box independence**, **reboot-gate enter/release**), the LCU classifier + `RebootVerifyLaneFor`
  routing tests, the `RebootOutcomeSelector` + `ReadyConfirmation` tests, the phase→state mapping tests,
  the `RebootOutcomeMessages` tests, the
  `ParsePreflight` result-classification tests (now incl. the safe-default contract + `__WUGRESULT__`
  marker extraction — failure cases must NOT claim the module is missing), the
  **`WritePs51Script_writes_utf8_with_bom`** BOM regression guard (`Vivre.Core.Tests/Wug/`), and
  the **`StagePreconditions`** unit tests (IsAlreadyStaged, IsAlreadyCurrent fail-open, UnscannedThisSession),
  and the **staged-patching toggle** tests — `StagedHostMatching`, `Lcu2016CuMatcher` (`FindCuKb` ambiguity +
  `CuKbs` conservative exclude + .NET exclusion), `StagedInstallPlanner` (`Plan` partition + KB mismatch +
  `PartitionByCurrency` fail-open: all-current → none, mixed → only non-current, null/Unreachable → included),
  and the override-aware `RebootVerifyLaneFor(osBuild, requiresStaging)` routing. **+29 across the WUG
  streaming state-check** (823 → 852): the streaming-parse tests (`ParseDeviceLine` kept in lockstep with
  `AddDevice`; `ParseMaintenanceState` now REQUIRES `__WUGRESULT__` and rejects a trailing `__WUGDEV__`
  line as the summary — the fabricated-clean-read guard), the `WugRowText` string locks (incl. the distinct
  `NotChecked`), `ComposeAbortError` / `ComposeStoppedMessage`, and **6 real-`powershell.exe` process tests**
  (incremental per-line delivery while the child runs, the stall-watchdog kill, caller-cancel kills the
  child in both launchers, and the PSModulePath strip on both launch paths) — the process tests took the
  suite from ~5s to ~23s. **+21 across the identity-verify resolver** (`b67ed55`, 852 → 873): the
  dot-boundary `Test-WugNameMatch`, the MatchedByName/ByIp/NoDevice/Ambiguous/LookupError outcomes,
  error≠no-match, and the set-path fail-safe. **+24 across the pooled state read** (873 → 897): +22
  pool/clamp/fan-out-trap tests (`WugConcurrencyTests` — `ClampConcurrency` bounds, the `StateReadMaxConcurrency`
  ceiling, and the T1/T2 + single-sourced-resolver + module-override string-locks; `WugPoolProcessTests` —
  real-`powershell.exe` pool streaming/connect-once-per-runspace/error-isolation/N=2-overlap/degradation
  tests) + 2 (the real-module smoke test + the set-path parse lock). The pool process tests ride the committed
  stub-module fixture via `VIVRE_WUG_MODULE_OVERRIDE`, so the suite wall-clock stayed in its ~87s ballpark
  instead of paying the real module's ~8s cold-load per runspace.

## Docs in repo
- **Root:** `CHANGELOG.md`, `README.md`, `CLAUDE.md`.
- **`docs/`:** `windows-patching-lane.md` (the WUA / patching lane), `key-file-path-map.md` (this file), `vivre-backlog.md`, `2016-LCU-lane-spec.md`, `2016-LCU-panel-spec.md`, `2016-LCU-red-team-review.md`, `vivre-rdp-scaling-and-fcm-findings.md`, `cold-start-freeze-and-threadpool-findings.md`, `freeze-hunting-playbook.md` (the UI-freeze hunting instrument + protocol), `vivre-audit-findings.md` (the 2026-07-01 five-lens audit — point-in-time, never edited; live status in the backlog).
- **`tools/`:** `Get-VivreFreezeLog.ps1` — the harvester for the freeze/disconnect instrument log lines (see docs/freeze-hunting-playbook.md ▸ Tools); `RemoteRun` — the dev console for exercising remote PowerShell.
- Retired: the nav-refactor plan doc (refactor complete) and the overnight Kerberos status doc (spent) were removed; their content lives in CLAUDE.md / windows-patching-lane.md / this file.

## Restore points
Use `git log` - it is the authoritative restore-point list. (A hand-maintained commit-SHA list used to live here; it decayed on every commit and after a history rewrite, so it was removed in the 2026-06-23 audit.)
