# Vivre backlog — DONE archive (pre-1.16.0 era)

This is the backlog's DONE (committed) history older than the 1.16.x releases — 1.15.x, 1.14.x, and
the early shell/NavigationView-era work — moved here verbatim (newest-first) on 2026-07-21.
The live tracker is docs/vivre-backlog.md.

- **Embedded RDP magnification + re-fit engine — DONE, shipped 1.15.0** (`a080685` · `f9b014e` ·
  `48eba5b`, on master, from `feat/rdp-clientside-zoom`). Client-side **ZoomLevel** magnification —
  the session stays at 100% (THE PIN CARDINAL: `LocalScale()` = (100,100) in `RdpSessionView.xaml.cs`;
  gate greps `= LocalScale();` → exactly 2 and `_rdp.UpdateSessionDisplaySettings` → exactly 1) — plus
  the verified/retried re-fit engine (spaced sends, read-back verify, even-both-dims per MS-RDPEDISP)
  with the quiet-hands input guard, the full-screen un-latch (a failed switch is logged and the button
  stays clickable, both directions), and the drag-deferred host resize (the 12s border-drag freeze was
  the OCX repainting the 1.5×-zoomed framebuffer per drag tick — a render regression, proven by
  instrumentation after both rival theories were disproven; tag `instrument/ui-freeze-watchdog`).
  Records: `docs/vivre-rdp-scaling-and-fcm-findings.md`; method: `docs/freeze-hunting-playbook.md`.
- **RDP disconnect classifier — DONE, shipped 1.15.0** (`f9a0d94`, on master, from
  `fix/rdp-signout-close`). Pure `RdpDisconnectClassifier` (Vivre.Core/Rdp): keep-by-default — a tab
  closes ONLY on `ExtendedDisconnectReasonCode` 12 (LogoffByUser, measured identically for
  Start ▸ Sign out and `logoff`) while connected with no auto-reconnect in flight; codes 4
  (ServerLogonTimeout) / 6 (OutOfMemory) and ALL unknown codes keep the tab with Reconnect. Replaces
  the `87674c2` check, which read the WRONG enum and was inverted (real sign-outs showed a bogus
  "internal error"; two genuine failures closed silently). `GetErrorDescription` runs for the error
  outcome only (test-pinned). Suite 786 → 810.
- **`413fb9d` — in-app guide accuracy sweep** (2026-07-11, release 1.14.6). Four-worker audit of every
  Help topic against the code: mode-specific filter-chip lists, the bottom panel's toggle-only opening,
  real button labels (Scan all / Install all, full client-action names), honest install targeting
  ("No updates selected" skip), the vitality rubric's −12 WinRM-degraded penalty + amber floor, the
  reboot-wave truth (graceful-then-forced escalation after the 8-min go-offline window — 20-min for
  staged-2016 boxes; these are per-box-TYPE presets, not two stages — ~4½-h no-longer-tracking cap, flagged-only UBR verify, two
  missing outcome strings), Monitor on-by-default (and Stop doesn't stop it), custom-column cancel +
  reboot-service housekeeping now covered. Also fixed the stale "Actions ▾" code comment and the
  MaintenanceWindow intro (name-then-IP). Tests unchanged (786).
- **`7b1f5d1` — custom-column sweep cancels on removal and counts honestly** (2026-07-11; 3 workers +
  1 red-team). Root causes: `RemoveCustomColumn` had no cancellation path (the snapshot sweep ran to
  completion) and `WrapWithCompletion` counted cancelled rows (counter raced to N on Stop). Now: a
  `_customColumnSweeps` registry (CTS + captured spec names via `RunSweepAsync`'s new `onBegin`
  callback; unregistered by reference identity) lets removal cancel any sweep whose EVERY spec is gone
  (partial removal keeps filling the rest); a live-spec guard stops writes to removed columns; stopped
  cells show "cancelled"; and cancelled rows no longer increment any sweep's counter (freezes on Stop —
  display-only, red-team-verified). No Desktop test home; tests unchanged (786).
- **`b534c6b` — horizontal cell padding in the fleet grids** (2026-07-11). One shared `GridCellStyle`
  (BasedOn the WPF-UI default, Padding 8,0 — the library default is 6,0 and its cell template binds
  Padding) on both grids; cell text now aligns with the 8px-padded headers. Zero persistence risk (the
  Columns manager keys off ComputerGrid headers only). Tests unchanged (786).
- **`fa837e6` — software DCOM fallback covers ALL WinRM failures** (2026-07-11). The `f4fad69` catch
  broadened from Kerberos-only to the full `IsWinRmUnavailable` classifier (service stopped, session
  lost); the double-failure message generalized to "WinRM was unavailable…". +2 routing tests — suite
  784 → 786. Smoke target: AZRADMANPLUS (WinRM dead, DCOM alive).
- **`1d19298` — software check shows "Offline" on genuinely-offline boxes** (2026-07-11). The check
  gates on `IsGenuinelyOfflineAsync` (fresh ping + ambient DCOM — never stale `IsOnline`) like the
  custom-columns probe: both channels dead → clean "Offline" cell, no connection timeout, no misleading
  "WinRM unavailable"; refills normally on the next check. Tests unchanged (784).
- **`50a0ab4` — WinRM dead-end messages point at the software check** (2026-07-11). Custom columns,
  health checks and SCCM client actions (LastError + activity line — never the status cells) and Run
  Script (type-conditional) gain the `WinRmDeadEnd.SoftwareRedirect` suffix, gated to
  `KerberosWrongPrincipalException` only (session-loss is transient; a broad gate could point at a
  co-failing feature). Tests unchanged (784).
- **`f4fad69` — software check falls back to DCOM on Kerberos-broken boxes** (2026-07-11; 5 workers +
  2 red-teams). New pure `SoftwareShaping` (Match / MatchAcrossHives / NormalizeServiceState parity
  seams) + `DcomSoftwareReader` (StdRegProv over DCOM against the SAME Uninstall hives the WinRM script
  reads; `DcomLcuBuildReader` structure — never the vitals swallow-to-null: RV=0 enumerate, RV=2
  benign-absent, RV=5/other THROWS; Found=false only when every hive ∈ {0,2} with ≥1 enumerated; OCE
  rethrows first at every layer) injected into `SoftwareProbe` à la `VitalsProbe`; ambient login only,
  `Win32_Product` stays banned. +24 tests (18 shaping + 6 routing) — suite 760 → 784.
- **`afe4be9` — Patching command column header renamed to "Command result"** (2026-07-10, release
  1.14.5), matching the Health grid (both columns bind the same per-row command output). Display
  string only — the Columns manager persists Health-grid headers exclusively, so no saved layout was
  affected. Tests unchanged (760).
- **`9569cec` — WUG state check moved to a grid right-click action** (2026-07-10). New right-click
  **Check WhatsUp Gold state…** on both the Health and Patching grids: a small credential dialog
  (`WugStateWindow` — server read-only from Settings, pre-flight gated, Install-module affordance)
  fires `WorkspaceViewModel.CheckWugStateAsync`, which writes each row's state into the Command
  result column (in maintenance / not in maintenance / no matching device (by IP) / state unknown —
  a failed read folds its error into unknown, never a false "not in maintenance") plus one
  activity-log summary. The interim in-dialog Check state button from `3f8ada1` was removed;
  `MaintenanceWindow` is byte-identical to its pre-button state. Tests unchanged (760).
- **`3f8ada1` — read current WUG maintenance state (the Core read path)** (2026-07-10).
  `WugMaintenance` gained `StateScript` (read-only `Get-WUGDevice` lookup; in-maintenance =
  `bestState`/`worstState` equals "Maintenance", presence-checked via `PSObject.Properties` so an
  absent field reads unknown — never a false "not in maintenance"), `ParseMaintenanceState`
  (marker-first, fail-open), `WugMaintenanceStateResult` (per-name `bool?` tri-state,
  case-insensitive), and `GetMaintenanceStateAsync` (routed through the shared BOM +
  PSModulePath-safe launcher — same creds/`-IgnoreSSLErrors` invariants, creds never saved). +10
  parse tests (`WugMaintenanceStateParseTests`) — suite 750 → 760. (First surfaced as a
  maintenance-dialog button, replaced by the right-click action in `9569cec`.)
- **`e2946de` — Reason field hidden when exiting WUG maintenance** (2026-07-10). The maintenance
  dialog shows the Reason field only in Enter mode (a reason is only meaningful when entering;
  retained text restores on switching back). Exit still sends the retained reason — the proven wire
  shape; the empty-reason-on-Exit refinement was deliberately dropped pending a live gate. Tests
  unchanged (750 at that point). Cardinal clean across all four commits (read-only; no reboot path).
- **Users Online honest unknown — DONE** (`f26a7c4`, on master; the last 1.14.4 commit). The health
  script's `$userLoggedOn` collapsed a FAILED `Win32_Process` query into a definite false
  (`-ErrorAction SilentlyContinue` + `@().Count -gt 0`) — rendering a green ✓ "No" (the "safe to
  reboot, nobody's on" signal) on a box whose query merely failed. Now the vitals pattern: `$null`
  seed + `-ErrorAction Stop` in an isolated try/catch + uncast emission; `SccmClientInfo.UserLoggedOn`
  is `bool?` via a new `GetNullableBool`; a failed read renders the grey "?" "Unknown" while a
  genuinely user-free box keeps its definite green "No" (the false-vs-unknown boundary + script-shape
  pins locked by tests). IsHealthy untouched; zero VM/XAML change (`Computer.UserLoggedOn` was already
  `bool?`, converters already tri-state). Help gained the grey-"?" clause. **750 tests** (+4).
  Cardinal clean.
- **Orphan `Vivre_Reboot_*` service reaper — DONE** (`a008747`, on master). The SMB/SCM reboot
  fallback's best-effort delete can lose the race with the reboot, leaving a Stopped demand-start
  LocalSystem `Vivre_Reboot_<32hex>` service (`cmd /c shutdown …`) with zero trace (the failure went
  to a Release-stripped `Debug.WriteLine`). New list-load reaper: `OrphanRebootServiceReaper` (own
  minimal advapi32 set — NO StartService/ControlService/CreateService extern, handles opened WITHOUT
  `SERVICE_START`, so starting anything is impossible by construction) sweeps each loaded host once
  per session over the same ambient-NTLM SCM channel (ping-gated, 10s-bounded, cap 8, ApplicationIdle,
  gated by auto-check-on-load) and deletes only exact-name + confirmed-Stopped matches
  (`RebootServiceReapPolicy`, pure, lowercase-hex-anchored). Investigation premise corrections: the
  in-app Vitals-triage start vector was ALREADY closed (the triage list is Auto-start-only; the orphan
  is demand-start) — the reaper is defense-in-depth vs GPO healing / manual `sc start`. **746 tests**
  (+21 cases). Cardinal clean, triple-locked.
- **`installBegan` race closed — producer-side latch — DONE** (`832aa7f`, on master; backlog #3 /
  audit MED). The install re-entry guard's flag was written only inside the UI-posted `Progress<T>`
  callback while RETRY attempts read it on a thread-pool continuation — on a double-transient install
  the guard could read a stale false and re-dispatch an install that had begun (false "up to date",
  dropped count). Barrier-only fixes rejected: the write was scheduling-delayed (not yet executed),
  not merely invisible. New Core `InstallBeganLatch : IProgress<HostPatchStatus>` latches
  synchronously on the PRODUCING thread before `InstallAsync`'s task completes, then forwards to the
  UI `Progress<T>`; the guard reads `latch.Began` — race eliminated by ordering. Honest framing: WUA's
  `IsInstalled=0` filter + the agent's `BootBusyGuard` already made a true double-APPLY vanishingly
  unlikely; the harm was the reporting lie. **725 tests** (+8 cases incl. the
  synchronous-set-on-the-reporting-thread contract). Cardinal clean.
- **Computer-list save crash-safe — DONE** (`1add64f`, on master). `ComputerListStore.Save` wrote the
  named list in place (`File.WriteAllLines`); a crash mid-write corrupted it. Now writes via
  `AtomicFileWriter` with a format-preserving lines→string conversion (the existing Save→Load
  round-trip tests cover it). **717 tests.**
- **Parallel Stop-cancellable client actions + atomic settings save + LastBootTime note — DONE**
  (`d600009`, on master). Three items, investigated + red-teamed. (1) **SCCM client actions**
  (Machine Policy / Heartbeat / Inventory / Update Scan / Update Eval) were a sequential foreach with
  a DEAD RelayCommand token (never in `_activeCts` — Stop couldn't cancel and might not even light)
  and an unbounded execute phase — one hung box stalled the rest, and an escaping
  `RemoteShellInitException` aborted the whole sweep. Now the Enable-WinRM template, WinRM-adapted:
  parallel per-row on the shared `_remoteSweepThrottle` (passive acquire), per-row linked-CTS **60s**
  (red-team-corrected from 30s — the per-host WinRM gate wait counts against the budget), passive
  `BeginOperation` so Stop works, full catch ladder incl. a typed `RemoteShellInitException` →
  "WinRM busy" Warn and a catch-all. (2) **settings.json write is atomic** — new Core
  `AtomicFileWriter` (same-dir temp + `File.Replace`; `File.Exists`-guarded `File.Move` first-write
  fallback; 3 branch tests) behind `AppSettingsStore.Save`'s unchanged lock/catch — a crash mid-write
  can no longer torn-write away `StagedHosts`. (3) **LastBootTime bare catch** — premise corrected
  (isolated to one cosmetic field, honest null, not in the verdict): documenting comments + the
  previously-missing positive `GetDateTime` parse tests. **717 tests** (+5). Cardinal clean.
- **Honest post-reboot outcome — DONE** (`f965b29`, on master; audit MED cluster). Three connected
  fixes to "Reboot & verify"'s message: (1) tri-state `bool?` reboot-pending probe — a probe
  failure/timeout now reads "Back online · couldn't confirm reboot state — re-check" with a grey "?",
  never a green "up to date"; (2) a 120s per-call cap on the previously-UNBOUNDED
  `IsRebootPendingAsync` call (a hung SCCM client could pin the row ~4¾ hours); (3) consume-once
  nullable install counts — "installed N" appears only when THIS session's install actually
  installed/failed something, is reported once, and is omitted otherwise (a standalone reboot no
  longer claims "installed 0"; an old failure can't resurface). **712 tests** (+13). Cardinal clean.
- **Docs synced to the 2026-07-08 audit-fix session — DONE** (`1db69d2`, on master). Backlog,
  key-file-path-map, and windows-patching-lane aligned with `12a5e36`/`852662d`/`7e2102c`/`289878f`
  (docs only, no code).
- **Honest cancel chip + SCCM ClientSDK sentinel + settings/enabler guards — DONE** (`289878f`, on
  master). Four items, investigated + red-teamed first. (1) **Cancel scheduled task verifies by
  absence** (unregister `Vivre_*` then re-query; emits `REMOVED`/`REMAINING: <names>`): the chip
  clears ONLY on `!HadErrors` + an exact trimmed full-line `REMOVED` (new pure Core classifier
  `ScheduledTaskCancelOutcome`, 7 tests incl. the name-contains-REMOVED trap); on failure the chip
  stays, the row reads "Cancel failed — task may still fire", and the surviving task is named in the
  activity log — a failed unregister of a `Vivre_Reboot` task can no longer hide behind a false
  "cancelled" (audit MED, reboot-adjacent). (2) **SCCM ClientSDK sentinel** (audit MED): a
  corrupt/denied `ROOT\ccm\ClientSDK` used to render as fully compliant (empty queries → green
  "Updates Missing" check + "Healthy"); `CCM_SoftwareUpdate` now fails loudly into a
  `ClientSdkFailed` flag → grey "?" cells + "ClientSDK unavailable" + the Errors filter, never
  false-green; `CCM_Application`/`CCM_Program` deliberately stay SilentlyContinue (legacy class can be
  absent on healthy clients) and the reboot method keeps its isolated catch (isolation now pinned by
  an ordering test). (3) **WinRmEnabler null result code** now throws instead of reading as success
  (`InterpretCreateReturn`, table-tested; also closes a raw-NRE escape). (4) **Corrupt settings.json
  resilience**: `ReadFromDisk` seats defaults + rethrows once on content-shaped failures only —
  fixes a previously-undiagnosed STARTUP CRASH (the unguarded WorkspaceViewModel ctor Load ran before
  `window.Show()`) and the session-long re-throw cascade; transient IO locks deliberately propagate
  unseated so an AV lock can never end with defaults overwriting the real file. Review pass
  empirically verified the two riskiest PS assumptions against real 5.1. **699 tests** (+18).
  Cardinal clean.
- **Monitor reboot-probe bounded (audit HIGH-2 CLOSED) + three MEDs — DONE** (`7e2102c`, on master).
  (1) **HIGH-2:** the monitor's reboot-pending probe was the ONLY unbounded remote await in the
  per-box monitor work — a wedged CCM `DetermineIfRebootPending` provider froze every pass's
  `Task.WhenAll` fleet-wide, forever. Now a 120s linked CTS (vitals template) with the token threaded
  INTO the probe (so the invoke unblocks and both gate slots release); a timeout is swallowed quietly
  — degraded 5-min back-off + one Warn, NO row-state write (a slow box is never painted failed).
  (2) **Stop during the SMB copy no longer launches the SYSTEM agent** —
  `ThrowIfCancellationRequested` between copy-complete and `service.Create`; the null-guarded finally
  cleans the dropped files (audit MED). (3) **Settings-save failures surface in Release** — static
  `IActivityLog` hook on `AppSettingsStore` (covers all five construction sites), Error-level entry;
  previously Debug-only, compiled out (audit MED). (4) **Enable WinRM**: 20s CIM timeouts (session +
  operation + token) + a 25s caller-side belt, 8-parallel under a new throttle, registered as a
  PASSIVE operation so Stop cancels it without blocking other sweeps; a Stop reads "Cancelled" —
  never "failed" — because CIM cancellation surfaces as CimException, not OCE (audit MED). Accepted
  behavior change: the monitor pauses during an Enable run like every tracked op. **681 tests.**
  Cardinal clean.
- **Dead update agent detected in the WinRM watcher (audit HIGH-1 CLOSED) — DONE** (`852662d`, on
  master). With the wall-clock gone (`12a5e36`), an agent that died WITHOUT writing a terminal line
  (crash, EDR kill, task time limit) left the watcher heartbeating on its behalf forever — the row
  spun "Installing…" and held an install slot until Stop. Now: (1) **watcher-side task-state death
  probe** — during a quiet stretch (the existing 15s heartbeat gate) the watcher probes
  `Get-ScheduledTask` in-session (zero extra WinRM shells); a not-Running observation arms
  `$taskGone`, the next tick's drain + re-probe confirms (any drained line disarms — proof of life),
  then it emits a terminal "stopped without reporting a result" error ~16s after last output; fail-
  open on a null query, gated on `$progressSeen` so pre-start stays owned by the 2-minute startup
  check. Mirrors the SMB lane's `service.Query()` guard. (2) **Lane-side non-terminal-final guard**
  — a stream that ends without a terminal status returns an honest "ended without a final result"
  failure instead of freezing on a mid-run phase (message pinned non-transient so the runner can't
  re-dispatch). (3) **ExecutionTimeLimit split** — 12h for watched run-now installs (wedge backstop
  only), 6h kept for ScheduleAt runs, which execute with no watcher attached. **681 tests** (+4 incl.
  PS parse-validity for both bootstrap variants). Cardinal clean.
- **Install wall-clock timeout removed + watcher startup check latched — DONE** (`12a5e36`, on
  master). The 3-hour per-host wall clock cut off actively-progressing installs mid-run (two live
  boxes at 80%/32%), and its teardown deleted the progress file under the still-running watcher —
  whose unlatched 2-minute startup check then painted "Worker did not start writing progress within
  2 minutes" over the honest "Timed out" (while the install actually kept running as an orphaned
  SYSTEM process). Fixes: (1) Install/Uninstall sweeps pass an INFINITE per-host timeout (like 2016
  Clean up); the 90s silence watchdog remains the session safety net (agent-death detection followed
  in `852662d`). Scan/Schedule/Stage/Verify keep their bounds. (2) The watcher startup check is
  latched behind `$progressSeen` — "did not start" can only fire before any progress was ever
  relayed; a file deleted mid-run by cleanup exits the tail quietly. (3) `operationEnded` late-line
  gate in InstallRowAsync/UninstallRowAsync — lines draining from a stopping pipeline can no longer
  overwrite the terminal row state. Validated under real PowerShell 5.1 (parse harness). **676
  tests** (+4). Cardinal clean.
- **Clean decoupled from the Staged gate — now selection-driven — DONE** (`9133226`, merged to
  master). Clean (DISM component cleanup) was gated to Staged/flagged 2016
  boxes only (via `StagePreconditions.IsStageTarget`, shared with Stage/Verify, plus a second gate in the button
  handler's `EnsureStageTargets`). Research confirmed the gate was incidental product-scoping, NOT a safety
  dependency — DISM `/startcomponentcleanup` is self-contained (no staged package/marker/flag needed), and the
  code already treats Clean as a prerequisite you run BEFORE staging to free space, so gating it to
  already-flagged boxes was backwards. Now selection-driven: nothing selected → all 2016 boxes (flagged AND
  unflagged); some selected → those; non-2016 excluded; staged-state ignored. New pure selector
  `ComponentCleanupTargets` (8 tests). Execution path untouched (same SMB lane, same throttle, same
  serialization). Stage/Verify unchanged (still flagged-only). Cardinal rule verified intact — Clean still never
  reboots on any box (`Win32Shutdown` only in `DcomRebootTrigger.cs`). Tooltip/Help/dialog text corrected.
  **674 tests** (+8).
- **Skip doomed health/vitals/custom probes on genuinely-offline boxes (Health grid) — DONE + LIVE-VERIFIED** (`dad8f79`, on master). **The issue:** on the Health sweep, health/vitals/custom-column probes fired at EVERY loaded row with no reachability gate, so a ping-down box ate a ~20s WinRM open-timeout (+ vitals DCOM-fallback timeout) before resolving to "Offline", and left a stray "TimedOut" (an operator custom column) + "Reading vitals…" in its cells. (Patching grid was already clean — it doesn't run the Health vitals sweep.) **The fix (Option A, operator-approved):** at the sweep entry, if ping fails, do an AMBIENT DCOM reach check (`WmiHostProbe.CanReachAsync(host, credential: null)` — the SAME identity `DcomVitalsProbe` uses); if BOTH ping and ambient-DCOM fail → mark the box Offline directly (clean "Offline" written to status + vitals + custom cells, overwriting stale litter) and SKIP all three probes. **THE LOAD-BEARING CONSTRAINT — do NOT gate on `IsOnline`:** `ProbeReachabilityAsync` only attempts DCOM when explicit creds are set (`_credentials.Current != null`), so on ambient login `IsOnline == false` for EVERY ping-down box — gating on it would wrongly skip a ping-down-but-DCOM-reachable Kerberos box and RE-BREAK the DCOM-vitals fix (`4c88c69`). Using the ambient DCOM result instead, a DCOM-reachable Kerberos box's ambient probe succeeds → NOT skipped → still gets DCOM vitals. **Per-sweep (not sticky)** — recomputed each pass, so a recovered box refills. Pure predicate `ReachabilityGating.ShouldSkipAsOffline(pingReachable, dcomReachable)` (= `!ping && !dcom`) unit-tested (both-down → skip; ping-down-but-DCOM-up → do NOT skip; ping-up → do not skip). **Reboot-pending probe untouched.** Thread-safe (same live-filtered UI-thread writes as before). Cost: an ambient DCOM probe on an offline box REPLACES the far more expensive ~20s WinRM+vitals cascade → net speedup. **666 tests** (+4). Cardinal clean. **Live-verified:** offline app servers (APAGISSERVER1/APAPORTAL1/APARDATASTORE1/APATRC-WS1) read "Offline" fast on both grids, no litter, sweep visibly faster; and a WinRM-broken-but-online box (APVVISIONB-SQL2) still showed its full DCOM vitals — NOT wrongly skipped. **Known edge (accepted, does not exist on this fleet):** a box that blocks ICMP + blocks DCOM/WMI but answers WinRM would be wrongly skipped — operator confirmed no such boxes exist; if one ever appears it's a separate targeted fix.
- **Blank vitals snapshot no longer treated as a genuine reach — killed the false "Offline since… waiting" re-trigger — DONE + LIVE-VERIFIED** (`b557b86`, on master). **The bug (found while verifying `032293f`):** `VitalsProbe.GetVitalsAsync` RETURNS a blank flagged snapshot (doesn't throw) when a box rejects Kerberos AND the DCOM fallback also fails; the auto-check path called `ApplyVitals` unconditionally, and `ApplyVitals` set `IsOnline = true` AND `WasConfirmedOnline = true` with no empty-read guard — so a genuinely-offline Kerberos-broken box (that was reachable earlier this session, cached SmbDcom) got marked online+managed off a ZERO-DATA snapshot, and on the monitor's offline confirm → `previous == true && WasConfirmedOnline` → the "Offline since… waiting for it to come back" message fired on an offline box. Same CLASS of bug `032293f` targeted (a box getting reach credit it didn't earn), different vector (blank vitals, not ping). **The fix (Option A):** new pure `MachineVitals.IsGenuineReach => !IsEmpty` (IsEmpty checks the OS-data fields, deliberately excluding transport metadata, so a blank KerberosRejected snapshot is IsEmpty==true → not a reach; a PARTIAL DCOM read is not empty → IS a reach); gate the two "reached it" writes in `ApplyVitals` on `v.IsGenuineReach`. Fixes BOTH the new false-managed AND a pre-existing false-online, same two lines. **The rest of `ApplyVitals` still runs on an empty snapshot** — the `WinRmDegraded`/Connection-callout surfacing depends only on `v.WinRmHealth`, not the gated writes, so the Kerberos degraded state is preserved. **Partial DCOM read still counts as managed** (reboot-wave "waiting" tracking on DCOM-vitals boxes preserved). **NOT the parked "ping = online" root cause** — a narrow, contained bug. Thread-safe (only added a condition around existing UI-thread writes). **662 tests** (+4). Cardinal clean. **Live-verified** (with `dad8f79`, on the fresh exe) — the offline boxes no longer show the false "waiting" message.
- **DCOM vitals gaps filled — stopped auto-services + logged-on user now populate on Kerberos-broken / WinRM-down boxes — DONE** (`4c88c69`, on master). **The gap:** on a box reachable only over DCOM (WinRM down / Kerberos 0x80090322), `DcomVitalsProbe` populated disk/mem/cpu/boot/reboot/OS but left `StoppedAutoServiceCount`/`StoppedAutoServices`/`UserLoggedOn`/`LoggedOnUsers` empty — the boxes you most need to triage showed a blank stopped-services list + no logged-on-user dot. **The fix:** two read-only `root\cimv2` queries added to `DcomVitalsProbe.ReadSync`, wired into the existing `MachineVitals` ctor (no model/UI/scorer change), mirroring the WinRM path's exact definitions. (1) Stopped services: `Win32_Service WHERE StartMode='Auto' AND State<>'Running'` — count ALL, names first 15. (2) Logged-on user: `Win32_Process WHERE Name='explorer.exe'` + per-instance `GetOwner` → `UserLoggedOn = count>0`, `LoggedOnUsers` = distinct/sorted owners. **Deliberately explorer-owner, NOT `Win32_ComputerSystem.UserName`** (UserName is blank for RDP; explorer-owner catches console AND RDP). **`GetOwner` instance-method overload proven, not assumed** — same `session.InvokeMethod(namespace, CimInstance, method, params, options)` overload `DcomRebootTrigger.cs:129` already uses over DCOM. **Independent try-blocks** + a `GetOwner` inner try (a denied owner-lookup still sets `UserLoggedOn` from the process count → the "Users online" dot survives; only names go empty). **Scorer unchanged** — reads neither field, so the 0–100 score does NOT shift; only the triage breakdown fills in. Thread-safe (none live-filtered; `ApplyVitals` already copies them on the UI thread). Pure `VitalsShaping` helper unit-tested. **658 tests** (+5). Cardinal clean. **Scope:** fixes built-in VITALS fields over DCOM; does NOT make CUSTOM columns work over DCOM (see PARKED). **Note:** `LoggedOnUsers` (names list) has no UI binding today — it's populated for parity; the visible wins are the stopped-services detail list + the Users-online dot.
- **Off-from-start boxes now read a calm "Offline" (not "Offline since [launch]" + a WinRM/SMB error) — DONE** (`032293f`, on master). **The bug:** Vivre equates "answers ICMP ping / a DCOM probe" with "online", so a powered-OFF server whose management controller (iDRAC/iLO/BMC), reused IP, or DNS answered ping got marked online once, and when ping later dropped the monitor wrote "Offline since HH:mm — waiting for it to come back…" using `DateTime.Now` (≈ launch time). Separately, "Scan all" attempted WinRM+SMB against every row including ping-down boxes (`ScanRowAsync` gated only on `IsPatching`), producing the red "Can't reach over WinRM or SMB — not manageable right now (…network path not found…)". Both read as failures on a box that is simply powered off. **The monitor ALREADY guarded "Offline since" with `if (previous == true)` — but `previous==true` is too weak (one bare ping satisfies it).** **The fix — two composable parts sharing a pure Core seam (`ReachabilityGating` — `ShouldTrackOfflineReturn` / `ScanShouldShortCircuitOffline`):** (1) new non-observable `Computer.WasConfirmedOnline` flag, set TRUE only at genuine REMOTING-success points — NOT the bare-ping path — and the "Offline since… waiting" guard narrowed to `previous == true && WasConfirmedOnline`; a ping-only box now reads a calm "Offline" (activity log "Offline —" not "Went offline —"), pill stays Idle not Error. (2) `ScanRowAsync` short-circuits a confirmed-offline box (`IsOnline == false`, or a null-probe box that a quick ping confirms down) to a calm "Offline" and skips the doomed WinRM/SMB attempt — applies to explicit operator scans too (operator's decision). **Both protected cases preserved:** a managed box rebooted for patching still shows "Offline since… waiting" (it was scanned/vitals'd/rebooted → flag true); a pingable-but-WinRM-broken box (Kerberos-down) still shows the real "Can't reach over WinRM or SMB" error (it's online → Fix 2 doesn't short-circuit → the genuinely-useful error survives). **7 set-points wired** (health ConfigMgr + WinRM-no-client, vitals, reboot-pending probe, non-Error scan, non-Error install, force-reboot) — the reboot-pending-probe + force-reboot points were ADDED beyond the original 4-point spec to close a case-2 gap (auto-check-off + reboot-a-never-scanned-box would otherwise lose its "waiting" message; the reboot runs over WinRM so it proves management). **Deliberate conservative tradeoff:** a scan/install that reached a box but ended in `Phase.Error` won't set the flag on its own (to avoid falsely flagging the "can't reach" case) — a rare false-negative traded for zero false-positives (never mislabel a BMC-only box as managed); the other set-points cover such a box in practice. **NOT changed (deeper cause, out of scope):** Vivre's core "ping = online" definition — a fleet-wide ripple (green dot, OnlineSummary, filters), see PARKED. Thread-safe: `WasConfirmedOnline` non-live-filtered/non-observable; no new off-thread live-filtered write. **653 tests** (+9 pure gating cases). Cardinal clean.
- **AdaptiveLayoutController warnings now reach the real log sink — DONE** (`69bb55f`, on master). The two `Serilog.Log.Warning(ex, …)` calls in `AdaptiveLayoutController.cs` (~181, ~624 — NavPane read/persist failures) wrote to the static `Serilog.Log.Logger`, which is **never configured anywhere in the codebase** → they defaulted to Serilog's silent no-op and were dropped in every build. Fix: inject the optional `IActivityLog` (the concrete `ActivityLog`, which writes both the in-app Activity panel and the rolling file at `%LOCALAPPDATA%\Vivre\logs\`) via the constructor, wired at the one call site (`MainWindow.xaml.cs`) from the composition-root `Log = activity`; both calls now `_activity?.Warn(null, …)` with the exception folded into the message. Zero `Serilog.Log` references remain in the file. Same pattern as the SMB fix below — the real sink, not the dead static logger. Control flow unchanged (non-fatal catches). CHANGELOG deliberately skipped (trivial internal UI-preference diagnostics, no user-facing behavior). **644 tests.** Found while doing the SMB teardown fix — the static-logger no-op was the shared root cause.
- **SMB helper-service teardown failures now surface to the activity log — DONE** (`09f6f02`, on master). `SmbAgentLane.TeardownServiceAsync` reported a failed `Vivre_WUA_*` teardown only via `Debug.WriteLine`, which Release builds STRIP — so a failed cleanup (leftover per-run service, `DeleteService` error) vanished with zero trace, against "no empty catch / surface failures". **The trap avoided:** the tempting minimal fix (static `Serilog.Log.Warning`, which the codebase uses in AdaptiveLayoutController) writes to nowhere — `Serilog.Log.Logger` is never configured (see the AdaptiveLayout entry). So the only REAL sink is `IActivityLog`, threaded minimally via optional trailing params App → `PatchService` → `WuaUpdateLane` → default `SmbAgentLane` (no caller breaks). Teardown catch now `_activity?.Warn(host, "…{Vivre_WUA_* name}… {ex.GetType().Name}: {ex.Message}")` at WARN (a reaped-next-run leftover isn't an operation failure). Runs in the `finally` after the result is produced — no rethrow, no fold into the operation result (a failed teardown still can't fail a patch/scan that succeeded). **644 tests.** Cardinal clean. (Low severity — the per-run service is harmless and the next run reaps it — but it was a real blind spot.)
- **Patching grid shows a "Cancelled" breadcrumb instead of blanking the column — DONE** (`6afb150`, on master). Cancelling a scheduled task nulled `UpdateMessage`, so with the Activity panel closed the operator saw no evidence anything happened; Fleet Health's "Last status" column showed a cancel breadcrumb but Patching has no visible Last-status column (hover tooltip only). Fix: on the cancel SUCCESS branch, write the SAME literal string Health uses instead of null — via a shared pure `ScheduledTaskMessage.CancelStatus(hadErrors)` helper so `LastStatus` and `UpdateMessage` are **identical by construction** and can't drift ("Scheduled task cancelled", or "Cancel had errors" on a remote error). Breadcrumb stays until the box's next real op naturally overwrites it (`ApplyStatus` is only called from real scan/install/uninstall paths; the monitor never blanks it; the scheduled re-derive can't fire because cancel nulls `ScheduledNextRun`). Success-branch-only — a FAILED cancel keeps the still-truthful "…scheduled for…" text (the task IS still scheduled). Deliberately did NOT mirror Health's transitional "Cancelling…" flash (writing it pre-await would leave a false "Cancelling…" stuck on a failed cancel). `RebootMessage` untouched. **644 tests.** Cardinal clean.
- **Scheduled-task timezone — anchored to the operator's timezone (no more per-box drift) — DONE + LIVE-VERIFIED** (`76dd713` the fix · `f49be1e` the deterministic-test rewrite; on master). **The bug:** scheduled install + reboot triggers were built as a bare wall-clock string with no offset (`-At '<yyyy-MM-ddTHH:mm:ss>'`) and evaluated ON THE REMOTE BOX, so each target read the picked time in ITS OWN local zone — a UTC Azure box fired at a different absolute instant than an Eastern box. **The fix (Option A):** the picked time is treated as the Vivre HOST's local wall-clock, converted to an absolute instant on the host, and assigned DIRECTLY to `$trigger.StartBoundary` (NOT passed to `-At` — PowerShell's `[DateTime]` cast on a `…Z`/offset string strips the intent; a raw StartBoundary string survives, and Task Scheduler honors an explicit boundary as an absolute instant regardless of the box's own zone). Both trigger sites fixed: install (`WuaUpdateLane.cs`) and reboot (`WorkspaceViewModel.cs`). Shared pure helper `ScheduleTimeFormatter.FormatStartBoundaryUtc` (zone-injectable internal core + `TimeZoneInfo.Local` public overload — single source of truth, can't drift). Local bookkeeping (`ScheduledNextRun = at`) stays host-local; only the string sent to the remote is absolute. The three operator-facing "scheduled for…" messages gained a **"(your time)"** label (DST-proof — deliberately avoids `TimeZoneInfo.Local.StandardName`, which wrongly reports "Standard" in summer). HelpContent how-to + CHANGELOG updated.
  - **Test lesson — the first test was CIRCULAR.** It derived `expected` from the same `GetUtcOffset` math the helper runs, so it would have passed a backwards/symmetric conversion. Caught in review and rewritten to a fixed-offset test (a DST-free custom zone) asserting hand-computed literal UTC digits, pinning BOTH directions (negative-offset rows ADD the offset, the +9:30 row SUBTRACTS it) so a sign flip can't survive.
  - **Corrected finding (the earlier brief had the direction backwards):** a UTC box fires the picked time EARLY, not late. For a "2 PM Eastern" pick, a UTC box fires ~10 AM ET summer (UTC-4) / 9 AM ET winter (UTC-5) — NOT "6 PM Eastern." Magnitude (4–5h) right, sign wrong. A box WEST of the operator (e.g. Pacific) fires late instead.
  - **Live-verified** on a real UTC Azure box (AZR*) + an Eastern box: both showed identical `<StartBoundary>2026-07-01T14:00:00-04:00</StartBoundary>` — same absolute instant (the UTC box carried the operator's `-04:00`, NOT its own `+00:00`, proving the box's own zone no longer leaks in). Task Scheduler stored the boundary in explicit-offset form (`-04:00`) rather than the `…Z` form the code emits — same instant, correctness unaffected. Read via `Export-ScheduledTask` XML, NOT Task Scheduler's "Next Run Time" (it re-localizes for display).
- **Cold-start UI freeze on large lists — RESOLVED** (1.14.2: `19f766b` grid re-layout, `0bfd362` sweep deferral, `ea70c2f` `ThreadPool.SetMinThreads(64,64)`). Opening a ~319-box list on a cold start froze the UI 7–38s (scaled with the slowest WinRM connect). Root cause — proven by instrumentation after **six** disproven theories — was **serial thread-pool worker injection on a low-core box** (default min workers = CPU count = 2; ~28 blocking `Task.Run(runspace.Open)` opens injected ~1/500ms, serialized behind the slowest connect), **NOT** pool exhaustion / grid / sweep. Fix: raise the min worker floor so the already-bounded opens run in parallel. Full record + the don't-re-chase list + the don't-delete-the-one-liner note in `docs/cold-start-freeze-and-threadpool-findings.md`. 636 tests; cardinal clean.
- **Stale reboot message never cleared — FIXED** (`3b6d9f3`, on master). The `RebootMessage` field held three
  past-event notices ("Reboot complete — back online {time}", "Back online {time}", "Forced reboot sent") that
  had **no clearer**, so they lingered indefinitely into unrelated later operations (observed: "Reboot complete
  — back online 10:22" still showing on a box that had moved on to installing). Fix: a pure helper
  `RebootMessageText.IsTransientRebootNotice` identifies the three past-event strings; scan/install/uninstall
  now clear them at the point each commits to running. Deliberately scoped — the chokepoint is the **three
  named operation methods**, NOT the shared `RunOnePatchHostAsync` wrapper (which also wraps reboot-and-verify;
  clearing there would wipe the "Reboot complete" message *during* the verify flow). The two **current-state**
  notices ("Offline since…", "WinRM temporarily unavailable…") were left untouched — they have their own
  condition-based clearers and an unconditional clear could blank a still-valid one. Confirmed `RebootMessage`
  is **not** in the live-filtered set (no grid-reshape / marshalling concern). 636 tests (+10
  `RebootMessageText` cases); cardinal clean. Reboot-and-verify still shows its completion message at the time
  it completes (the post-reboot rescan bypasses `ScanRowAsync`). Cosmetic message-lifecycle only — no patching
  behavior changed (intentionally not documented in `windows-patching-lane.md`).
- **Partial-failure false-green pill — FIXED** (`10defc4`, on master, live-verified). An install completing
  with `FailedCount > 0` was showing a green "Up to date" (or amber reboot-pending) pill — a violation of the
  no-false-green rule. Root cause confirmed at two layers: the agent's install `Summarize` picked the phase
  from `rebootPending` only (the uninstall path had a `failed>0 → Error` guard, install didn't), and the VM
  funnel `ApplyStatus` set `UpdatePhase` from `status.Phase` without reading `FailedCount`. Fix: a
  `failuresAreErrors` opt-in flag on `ApplyStatus`, passed `true` at exactly the install-final and
  uninstall-final call sites — when set and `FailedCount > 0`, the phase is forced to Error (structurally
  unreachable from scan/cleanup/reboot-verify, so no false reds). The agent's install `Summarize` was given
  the same all-failed guard as uninstall. Enforces **ERROR > REBOOT-PENDING > UP-TO-DATE**; the reboot dot
  still lights alongside the Error pill. 626 tests (+1 Core precedence lock: `"Error"` + reboot-pending →
  Error); cardinal clean. Live-confirmed: AZREASTMAILRL — the box that showed the false-green — now reads red
  Error; successful boxes still green. (Full doctrine in `docs/windows-patching-lane.md` ▸ "Install/uninstall
  failures are never green either".)
- **Fleet-recompute storm — progress-tick slice shipped** (`18d3d3b`, on master). The high-frequency path is
  fixed: a per-row `UpdateProgress` tick now raises ONLY `FleetProgress` instead of the full 9-property
  `RaiseFleetChanged()` — confirmed (in `ApplyStatus`) that `UpdateProgress` is the sole high-frequency property
  funneling through `OnComputerStateChanged` (`UpdatePhase` is re-written same-value per tick = no-op, so
  `PatchState` doesn't re-fire; `UpdateMessage` isn't handled by the storm path). Caught + rejected a worker's
  wrong "FleetProgress is unused / pure waste" claim — it's consumed in `MainWindow.xaml.cs` code-behind (status
  progress-bar animation), so nothing was deleted, only the progress-tick path re-routed. 625 tests green;
  cardinal clean; visual-checked at fleet scale. Remaining rare-event cleanup (Has* double-walks, PatchState
  parse cache) is split out under the open performance hunt cluster above.
- **Patch-sweep cross-thread crash on a transient WU-reach retry — RESOLVED** (on master).
  Surfaced *despite* the June-13 threading fix (`7c7b5f78`, which was correct but incomplete).
  Vector: `WorkspaceViewModel.SetTransientRetryingState` wrote `Computer.UpdatePhase` (a grid live-filtered
  property; `PatchState` derives from it) **directly on the thread-pool thread** — it is the `onRetrying`
  callback `TransientRetryRunner.RunAsync` invokes *after* `await attempt(...).ConfigureAwait(false)`, so it
  runs on the runner's context, not the sweep's UI continuation. Off-thread, the write re-shapes the live
  `_computersView` CollectionView on the writing thread → "the calling thread cannot access this object".
  Gated on a transient `0x80072EE2` retry (which a concurrent Stage batch's copy-fan-out I/O saturation made
  near-universal), and shared by **install AND scan** (both wire the same `onRetrying`). Fix: marshal the
  write to the Dispatcher (`SetTransientRetryingState` → `Application.Current.Dispatcher.InvokeAsync`), plus a
  DEBUG thread-affinity guard on the live-filtered-property writers (`Computer.OnUpdatePhaseChanged` /
  `OnRebootRequiredChanged`, via an injected `LiveFilteredWriteIsOnUiThread` check wired in `App.OnStartup`,
  keeping Vivre.Core WPF-free). **Lesson:** "no `ConfigureAwait(false)` on the VM sweep" is *insufficient* —
  any callback the VM hands a Core runner (`onRetrying`/`buildExhausted`/`attempt`) runs off-thread; route via
  `IProgress` or marshal. (3-pass investigation: pass 1 wrongly blamed a stale build; pass 2 proved the main
  sweep continuation stays on UI; pass 3 found this callback vector. 611 tests green.)
- **Embedded RDP — Reconnect button fixed (dead → live)** (`87674c2`, on master): Reconnect now tears
  down and rebuilds the MSTSC ActiveX control (`TearDownControl` + `CreateControl`); involuntary drops
  keep the tab open with a Reconnect button (sign-out-vs-drop was distinguished via
  `ExtendedDisconnectReason` 2/4/6 — **SUPERSEDED in 1.15.0**: that check read the WRONG enum and was
  inverted; see the RDP disconnect classifier DONE entry); `EnableAutoReconnect` + `GrabFocusOnConnect` are wired. Full-screen
  reflows the session to monitor resolution (`MonitorPixelSize`) and restores on exit. See
  `vivre-rdp-scaling-and-fcm-findings.md`. (This was the previous DO-NEXT #1.)
- **Embedded RDP — Failover Cluster Manager context menus fixed** (`1ce1abf`, on master): pinned the RDP
  session display scale to 100% (`DesktopScaleFactor=DeviceScaleFactor=100`), sidestepping the documented
  FCM >100%-scaling menu-collapse bug. Session was measured at 150% (the cause) vs mRemoteNG's 100%. Fills +
  FCM-safe; trade-off was a compact (native-100%) image. Magnification to match mRemoteNG later
  SHIPPED in 1.15.0 via the OCX's client-side ZoomLevel, session still pinned at 100% (see the 1.15.0
  DONE entry + `docs/vivre-rdp-scaling-and-fcm-findings.md`).
- **Update download-size accuracy — WUA-first + catalog override for inflated express CUs** (`d39c0e3`,
  merged to master). **Root cause:** Vivre read `IUpdate.MaxDownloadSize`, WUA's worst-case aggregate — an
  express CU reported **21,926 MB** vs the real **2,435 MB** full package. **Fix:** show `MaxDownloadSize` for
  every normal update (Defender / drivers / SQL / .NET / normal CUs — instant, no network, matches BatchPatch);
  substitute the Microsoft Update Catalog full-package size only when `Max` is absurd (>10 GB); dash only when
  both fail. Catalog lookup gated to absurd rows only (`NeedsCatalogLookup`) → **zero** catalog calls on a normal
  fleet scan. Self-contained (direct HTTPS GET + HtmlAgilityPack parse of the catalog's `_originalSize` byte
  count — no PowerShell module, no shell-out). New: `MicrosoftUpdateCatalogService`, `CatalogPageParser`,
  `UpdateSizeResolver`, `ArchFromTitle`; `SoftwareUpdate` Min/Max bytes; scan + agent emit
  `MinSizeBytes`+`MaxSizeBytes`. 596 tests; cardinal clean.
  - **BatchPatch per-machine figure (e.g. 1,446 MB):** investigated — it's the express **per-device** download,
    which is NOT present in WUA scan metadata (only the inflated aggregate is); getting it requires an on-box
    download-evaluation. **Decision:** show the conservative full-package size (catalog), not the per-machine
    express delta. Express parity is a possible future feature (resolve-once-and-cache) if ever wanted.
- **Transient WUA reach-failure retry — no false-green** (`ea1d078` · `bd490a0` · `7676980` · `ec6adfa` ·
  `4e34f02` · `cfba5e8`; **merged to master** — see `f2fd28b`/`7d8abd4` in `git log`). **Root cause
  proven** from `APVWUG`'s `WindowsUpdate.log`: `0x80072EE2` is a **transient SLS (service-locator) timeout
  at service-registration, BEFORE search** (http status `0` during the failed run, clean `200` an hour
  later; Windows' own 3 internal retries exhausted by a ~2m38s blind window). **The BatchPatch trap it
  fixes:** a non-clean search masquerading as fake-green "no applicable updates" — the rule is now **a
  non-clean search NEVER reads as up-to-date** ("0 updates" = up-to-date ONLY on a clean `orcSucceeded`).
  - **Both faces** handled, keyed on the HRESULT not the phase: (1) a thrown transient HRESULT, and (2) a
    search returning `SucceededWithErrors` / 0-updates-with-a-non-success-HResult. Transient family
    `0x80072EE2` + `0x80240438` (+ the WININET/WU_E_PT siblings); auth/config/4xx/install errors excluded.
  - **All four paths:** WinRM scan, WinRM install, SMB-agent scan, SMB-agent install (the agent's read-only
    `ResultCode` check → terminal Error line → surfaced by `SmbAgentLane` → retried by the VM runner, so
    Kerberos-broken boxes get the same retry).
  - **(a) Fresh per-attempt timeout** (the load-bearing fix): each scan attempt gets its OWN 300s budget
    (NOT one shared across attempts + backoffs, which killed attempt 2 before attempt 3 ran). Worst case
    for a fully-stuck box ≈ **24 min**, showing "retrying (n/3)…" throughout, then "Can't reach WU".
  - **(b)** jittered backoff (60s + up to 15s) so a fleet-wide outage doesn't retry in lockstep; **(c)**
    install re-entry guard (a transient after install began surfaces terminal, never re-runs → never drops
    the installed count). Pure unit-tested `TransientWuaError` + `TransientRetryRunner`. **488 tests green.**
    No reboot path introduced.
- **ARC-8 — verified already handled (no change needed).** Last status already mirrors the vitals badge:
  `WorkspaceViewModel.ApplyVitals` sets `LastStatus = "Vitality {score} ({band})"` whenever a score exists,
  and a DCOM-up/WinRM-down box still gets a score, so it reads e.g. "Vitality 88 (Warning)" — not "WinRM n/a".
  The WinRM detail lives in the Machine Details connection callout (`WinRmStateCaption` / `WinRmDegraded`),
  not in Last status. (The "WinRM n/a" seen on a *custom* column is correct, separate behavior —
  `WorkspaceViewModel.cs:1510`.) The old backlog symptom was stale; closed on evidence.
- **2016 DISM routing toggle — staged patching is now opt-in per box** (`08a9f9f` · `9489f74` · `2876ecd` ·
  `754a18d` · `bfb7bba` · `516d3fb` · `3125b0b`; **merged to master**; Settings UX follow-up `e590d2e`).
  **The default is now normal Windows Update for ALL Server 2016 boxes** — only a
  box the operator explicitly flags (right-click ▸ *Mark as Staged patching*, or *Settings ▸ Staged patching
  machines*) uses the DISM staging lane. This **inverts** the old "OFF (default) = all 2016 → DISM" design to
  opt-in, which the red-team pass settled.
  - **Routing** honors `Computer.RequiresStagedPatching` (⇄ persisted `AppSettings.StagedHosts`,
    OrdinalIgnoreCase). Non-flagged 2016 → WUA; `Server2016Targets()` and `RebootVerifyLaneFor` are flag-aware.
  - **Decision dialog** ("Server 2016 staged update required") on Install / Install all when a flagged box
    isn't staged: *Stage CU first* / *Install minor updates only* / *Cancel* (Cancel skips the flagged boxes
    only — the rest of the run still installs), with a Settings-vs-scan KB-mismatch warning. Minor-only
    excludes **every** CU-titled KB so the broken Express-delta CU never goes via WUA.
  - **Already-current pre-check** (fail-open) reuses `VerifyLcuAsync` → `DcomLcuBuildReader`: a box already at
    this month's UBR skips the dialog and installs its minor updates via WUA.
  - **Surface:** right-click Mark/Remove (2016 + Patching only), a narrow **Staged** pill column (hidden when
    nothing flagged), and the Settings management card (list / Remove / Clear all, re-syncs loaded rows).
  - Pure unit-tested `StagedInstallPlanner` (+ `PartitionByCurrency`) and `Lcu2016CuMatcher`. **427 tests green.**
- **Smart scan flow — Stage guards + Settings size-field removal** (`3a35292` · `ef795de` · `6350957` · `0718f7a`). The 2016 Stage step is now scan-gated and self-skipping: scan-this-session gate (`LastScannedApplicable != null`; a post-reboot rescan satisfies it), already-staged skip (RebootRequired && StagedThisSession), already-current skip (a pre-Stage UBR read — same call Verify makes — fails OPEN on a null read). Pure unit-tested `StagePreconditions` (Vivre.Core); removed the display-only "Approx. package size (MB)" Settings field. 378 tests green; merged to master.
  - **Descoped (not built):** KB / target-UBR auto-population from scan. The investigation found **target UBR is not present in any WUA scan result**, so auto-populating it is infeasible; KB stays a manual Settings field. The remaining (optional) KB-only auto-fill is below.
- **Fleet-wide reboot-and-verify** (`473585d` · `a7f456f` · `18c7eaf` · `7323a8d` · `a9922f7` ·
  `c96a265` · `50e1a87` · `7e4c1dd`; Patching-only gating fix `300ee4e`). Generalized the 2016 Reboot
  Wave to ALL boxes — after an operator-confirmed reboot, each box is watched offline → genuinely-ready
  (TCP-445 then a per-box confirm strategy) → auto-rescanned → a plain outcome reported.
  - **Verify by OS:** 2016 = UBR check in-wave first (a rolled-back box is caught as failed), then a WUA
    rescan appended as a "what's still needed" note; everything else = WUA rescan only (0 applicable = up
    to date). `IPostRebootConfirmation` (`UbrConfirmation` / `ReadyConfirmation`) + `BasicReachabilityReadinessProbe`,
    routed per box by `LcuRouting.RebootVerifyLaneFor`.
  - **Outcomes wired:** `RebootOutcomeMessages` (now incl. `BackOnlineRescanFailed`) via the pure
    truthfulness-first `RebootOutcomeSelector`; install counts carried on `Computer.LastInstall*`.
  - **140-box scale:** unbounded per-box watch (`_waveThrottle` 256) so a slow 2016 commit never blocks a
    fast box; reboot *issuance* capped (`_rebootTriggerThrottle` 12 + jitter via `IRebootGate`) to protect
    DCs/DNS/auth.
  - **Entry:** right-click **Reboot & verify…** (Patching-mode only); 2016 panel button re-points to it.
    Operator-confirmed only; the rescan/outcome path is read-only (no autonomous reboot; agent untouched).
    **367 tests green; visual-checked; merged to master.**
- **"What's still needed" WUA indicator — delivered with fleet-wide reboot-and-verify.** After a 2016
  box's CU commits (UBR verified green), the post-reboot rescan appends the remaining WUA-applicable count
  ("N update(s) still applicable — run a WUA pass" / "up to date"), so the operator knows to run a WUA
  pass next — no fused button needed.
- **Relocate repo + publish output out of OneDrive → `C:\src\Vivre`.** Killed the stale-binary class
  (OneDrive placeholder copies launching old code), the `.git/worktrees` lock, and the LF/CRLF churn.
  `.gitattributes` `* text=auto` in place; signing cert confirmed still found. (The path map's
  build/deploy section is updated to reflect the new location.)
- **NavigationView shell refactor — COMPLETE, including Phase 4** (landed ~early June 2026;
  the shell series — `90cb524` (Fleet Health/Patching split + menu-bar removal), `eebfd10` (adaptive/frameless shell), `aa3790d` (in-command-bar selection bar); Phases 1–4 in the preceding 1.8.x commits). MainWindow restructured around `ui:NavigationView`: LeftCompact pane +
  hamburger (collapsed by default); **Fleet** parent → Health / Patching sub-items (each its own tab
  strip + keep-alive state); Scripts; Cross-Domain RDP; Settings pinned bottom. The Machines/Windows
  Update **mode chips were removed entirely** (replaced by nav sub-items); the menu bar was removed,
  items remapped to toolbar / tab context menus / status bar. **Phase 4 = DONE:** toolbar reorg
  (Task-Manager-inspired), Settings page Expanders, contextual multi-machine selection bar, workspace
  tab patterns.
- `fe4d68e` — consistent dialog sizing across all 16 popups (no clipping, sensible min/max). Modals
  CenterOwner; fixed forms SizeToContent+Min/Max; content-heavy CanResize+ScrollViewer w/ buttons
  outside it; SoftwareCheckWindow SizeToContent=Height+MaxHeight (opens fully visible). Visual-checked.
- **`756fa9d` — WUG maintenance pre-flight + the two-gotcha fix (the saga's end).** Window-
  level pre-flight (Test connection + module/creds check; dialog stays open until module-present +
  reachable + creds-valid, then closes + fires the real per-device set). Fixed the false "module not
  installed" via: (1) Gotcha-1 `PSModulePath` strip so the 5.1 child sees installed modules; (2)
  Gotcha-2 BOM-write helper `WritePs51ScriptAsync` (UTF-8 **with** BOM) so PS 5.1 actually parses the
  script — the REAL root cause; (3) truthful parse contract — "module missing" only on an explicit
  signal, real connect/creds errors surfaced, `__WUGRESULT__` marker + backstop trap. 344 tests
  (incl. the BOM regression guard). Live-confirmed end to end (10.70.25.111 → device shows Maintenance
  in WUG's own console). Credential/SSL/persistence invariants untouched; NO reboot path.
- `087b748` — retired the two "Scheduled task" columns; folded action + time into the update message.
- Part A / Part B — Health/Patching chip-bar split + LCU bar gated to Patching; status-pill + message
  naming standard; "Not scanned"/"Scheduled" chips. 328 tests.
- `9d3f82a` — scan/install SMB-agent fallback on generic WinRM failure (per-attempt, auto-returns to
  WinRM; install fallback connect-time-gated). Live on AZRADMANPLUS.
- WinRM-unavailable clean gate + guidance (no-fallback ops) — investigation → honest gate, live-verified
  (Run Script, ConfigMgr bulk action with SQL2 skipped cleanly while F3+WS1 still actioned, custom
  columns all plain "WinRM n/a", no SSPI codes).
- `64337d1` SMB reboot fallback (Kerberos 1191) · `4436b18` fleet-wide ConfigureAwait threading fix ·
  `a997642` scan timeout 90s→300s · `d3b5ed0` monitor throttle · `ce624d4` row virtualization ·
  `b078014`/`5631a61` LCU lane + clean 2016 panel · `a0cb80a` (render-broken, superseded).

### Production result (the actual job)
- **APVVISIONB-F3 / -SQL2 / APATRC-WS1** — verified 14393.9234 ✅ (2016 full-package DISM lane works
  end to end). **AZRADMANPLUS** (WinRM genuinely down) — full SMB-agent fallback proven live
  (scanned → KB2267602 → installed → "Up to date"). The reliability gap BatchPatch covered is covered.
