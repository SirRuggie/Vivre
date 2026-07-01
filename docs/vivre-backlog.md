# Vivre — running backlog (deferred items & open threads)

> Working tracker for things found during build work that are NOT yet done.
> As items get fixed, move them to DONE with the commit hash. Add new finds under the right tier.
> **Order below is the recommended do-next order** (Ruggie can override — it's a recommendation,
> not a mandate). Last refreshed: **2026-07-01** (scheduled-task timezone fix DONE + live-verified;
> cancel breadcrumb; SMB teardown + AdaptiveLayout logging; off-from-start "Offline" messaging, DCOM
> vitals gaps filled, offline-probe skip — all this session; **released 1.14.3**). Everything below is on
> `master`. **Commit hashes in the DONE list predate a history rewrite and may not all resolve — `git
> log` is the authoritative restore-point list, and the per-entry test counts are point-in-time only
> (current suite is ~666 green).**

---

## ▶ DO NEXT — recommended order

**Audit findings (2026-07-01):** a full five-lens read-only audit ran on 1.14.3 — see
`docs/vivre-audit-findings.md`. No CRITICALs; 2 HIGHs (dead-worker-undetectable WinRM lane;
one-hung-box-freezes-monitor) + several MEDs, all triaged with a recommended fix order. Pull items
from there into scoped handoffs; nothing auto-queued.

Nothing queued. The RDP Reconnect button (the previous #1) shipped — see DONE. The 2016 staged-patching
toggle shipped (see DONE), and **KB auto-population from a scan is closed — manual only** (decision
recorded under *Settings simplification* below). What remains is the polish / standalone items further
down, each "do only if it recurs / when a signal appears."

---

## RESEARCH — open questions to confirm (no fix yet — confirm the behavior first)

- **Why some OS cumulative updates show a "—" (dash) for size — e.g. KB5094126 on Win11 24H2 / build 26100
  (observation, not a fix — working as designed).** The dash means `UpdateSizeResolver.ResolveDisplaySize`
  ([UpdateSizeResolver.cs] line 46) returned null = "no trustworthy size to show." For a 24H2 / Server-2025
  express/checkpoint CU, WUA reports a wildly-inflated worst-case `MaxDownloadSize` (tens of GB), so Vivre
  deliberately ignores it (showing it would be a lie) and falls back to the Microsoft Update Catalog for the
  real `.msu` size; if that lookup can't answer (the catalog isn't reachable from the patching host, or the KB
  doesn't match a catalog row) the failed lookup is cached "unavailable" for the session — so the cell stays a
  dash. (Less-likely alternative cause: WUA reported no size at all, Min = Max = 0, in which case the catalog
  is never consulted — it's only used for the >10 GB inflated case.) This is intentional: a dash, never a wrong
  number. **If we ever want fewer dashes:** confirm which case KB5094126 actually hits (inflated-Max-but-catalog-
  empty vs 0/0), then decide whether to (a) extend the catalog lookup to the 0/0 case and/or (b) diagnose why
  the catalog returns null for these CUs from the patching host (reachability vs row-match). Files:
  `UpdateSizeResolver.cs`, `MicrosoftUpdateCatalogService.cs`, `WorkspaceViewModel.ResolveCatalogSizesAsync`.

---

## OPEN — patching features (design mostly settled, build pending)

### Settings simplification
- `ExpectedSizeMb` (the display-only "Approx. package size (MB)" field) is **DONE** (removed;
  package matched by KB + arch, never size).
- KB and Target UBR **cannot** be removed: Target UBR is not present in any WUA scan result so it
  can't be derived automatically, and KB must remain overridable for off-cycle patches. Keep KB,
  Target UBR, and the package folder.
- **KB auto-population from a scan — CLOSED, manual only.** The strong (every-scan auto-write) version
  is a bad idea — it clobbers the single-value `MonthlyCu` across mixed/old-cycle fleets and overwrites a
  deliberately-set KB — and the weak ("use this scan's CU" button) version isn't worth it: UBR must be
  set by hand every cycle anyway (not in any scan result), and the Settings **update-history URL**
  now makes the manual KB+UBR lookup a two-click copy. The `Lcu2016CuMatcher.FindCuKb`
  heuristic built for the staged-patching dialog exists if this is ever revisited, but the decision is:
  manual only.

---

## OPEN — polish / smaller standalone items

- **Check whether a server is currently IN maintenance mode (read it, not just set it).** Vivre can put
  machines into / out of WhatsUp Gold maintenance (`WugMaintenance` + `MaintenanceWindow`), but there's no way
  to SEE the current state. Add a read — e.g. a per-row indicator or a status line in the maintenance dialog.
  **Research first:** (1) confirm what "maintenance mode" should mean here — most likely WUG maintenance, given
  the existing integration, but it could also mean an SCCM maintenance window, so clarify the intent when picked
  up; (2) confirm the source even exposes a read — does the `WhatsUpGoldPS` module / WUG REST have a device
  maintenance-state query (a `Get-WUGDevice` property, or a get-maintenance call)? If yes, surface it; if not,
  decide whether a direct REST call is worth it. Note the same credential/SSL invariants as the existing WUG
  path (creds never saved; `-IgnoreSSLErrors`). Files: `WugMaintenance.cs` (the 5.1 shell-out +
  `Get-WUGDevice` / `Set-WUGDeviceMaintenance`), `MaintenanceWindow.xaml(.cs)`,
  `WorkspaceViewModel.SetWugMaintenanceAsync`.
- **Stage copy fan-out I/O contention (UI sluggish during big batch Stage)** — NOT a UI-blocking copy and NOT
  a wrong-thread hash (both were earlier guesses). The 1.7 GB Stage copy already runs OFF the UI thread
  (`SmbAgentLane.cs:230-246` — `File.Copy` inside `Task.Run(...).ConfigureAwait(false)`), and integrity is
  byte-count only (no SHA-256 of the package). Real cause: **copy fan-out** — up to the install cap hosts each
  `File.Copy` the SAME workstation-local 1.7 GB source concurrently → N parallel source reads + N concurrent
  1.7 GB SMB uploads → workstation disk + uplink saturation; "sluggish, not frozen" is the per-host UI-thread
  progress continuations queueing on the single Dispatcher under that pressure. **Concurrency facts — NOT A TASK; the "make concurrency configurable" notion is a MYTH (CLOSED),
  the install/stage cap is already operator-settable:** there is NO hardcoded 10 — `PatchOptions.MaxConcurrentHosts = 50`
  (`PatchOptions.cs:78`), and the install/stage throttle is the operator-settable **Max simultaneous installs**
  (per-tab `_patchThrottle`, default 50; `MaxConcurrentScans = 32` is separate). Cross-tab coupling is
  architectural (one Dispatcher + singleton `PatchService`/`PSRunspaceHost`; scan/monitor/reboot throttles are
  `static`) but `_patchThrottle` is per-tab, so installs are NOT cross-tab gated. Fix direction (future): a
  dedicated I/O-aware copy cap separate from the install cap; and/or stage to one share and have targets pull;
  and/or sequence the large copies.
- **Proactive gray-out of known-WinRM-broken boxes** — visually mark boxes that are known to fail WinRM
  so the operator isn't surprised, rather than only learning at action time. Design pass needed.
- **Idle-monitor reachability throttle** — throttle added (d3b5ed0). Verify it holds at scale
  (300 boxes); drop from list once confirmed in practice.
- **Bottom-panel resize freeze with large row counts** — row virtualization added (ce624d4); appears
  fixed. Watch under heavy update-scan load; if it returns, next lever is the `Width="Auto"` columns
  forcing full-width measure. (Measured in the 1.14.2 cold-start hunt: the Auto-width column measure is ~120–180ms/pass — real but minor, NOT a freeze cause.)
- **Scan-timeout edge** — 5-min cap (a997642) may be short for the very worst first-scan boxes; bump to
  10 min (600s) ONLY if real "Scan timed out" false-positives appear.
- **SMB-agent teardown is a silent swallow in Release builds** — `SmbAgentLane.TeardownServiceAsync`
  (`SmbAgentLane.cs` ~414) reports a failed teardown only via `Debug.WriteLine`, which the compiler strips
  from Release builds — so in the shipped build a teardown failure (a leftover per-run helper service, a
  `DeleteService` error) disappears with no trace, against the "no empty catch / surface failures" rule. Low
  severity: the per-run-named service is harmless and the next run reaps it. Fix: inject an `IActivityLog`
  (or Serilog) into `SmbAgentLane` and log at trace/warn instead of `Debug.WriteLine`, keeping the
  "don't replace the operation result" intent. (Found in the 2026-06-23 audit.)

> **HANDLE WITH CARE — read before touching anything in the two hunt clusters below.** These items
> (especially the live-filtered grid, the load-bearing `PatchState`, the bulk-add path, and the per-row
> notification cascade) sit in the EXACT code that caused the past cross-thread crash. There are real
> concerns here — this is precisely the "one small thing I didn't fully check, so it caused X, Y and Z"
> class of regression we are NOT willing to repeat. So before tackling ANY of them:
>
> - **Research and observe FIRST.** Read the full call path end-to-end, map every property -> aggregate and
>   collection -> subscription dependency, and confirm thread affinity (the live-filter writers MUST stay on
>   the UI thread). Do not pattern-match a "quick fix" — these need deeper thought and observation of the
>   actual code before any edit.
> - **Beware the obvious-fix traps already found:** disposing the install-throttle / WinRM-gate semaphores
>   breaks in-flight installs (they are held by reference on purpose); a naive `Clear` + range / `Reset` on the
>   grid or the update checklist skips the per-row `PropertyChanged` re-subscription (the `Reset` ->
>   `NewItems == null` trap) and silently breaks live row updates.
> - **One at a time.** Tackle a SINGLE item, verify it (build + full tests + a visual check in the running app
>   at fleet scale), and COMMIT it on its own before starting the next. Do not batch these.
- **From the 2026-06-23 drift/stale hunt — not yet fixed (the easy 8 were done; these need more thought):**
  - **Post-reboot message shows the wrong install count.** `ReportPostRebootOutcomeAsync` (`WorkspaceViewModel.cs`
    ~3780) reads `LastInstall*` from the most recent install this session, not from the operation that triggered
    the wave — so a standalone Reboot & verify reads "installed 0", and two installs report the second's counts.
    The pill is correct; only the message text. Fix needs deciding how to tie counts to the wave (reset after
    use, or carry per-operation). Medium.
  - **DCOM vitals omit stopped-services + logged-on user.** `DcomVitalsProbe` doesn't populate
    `StoppedAutoServices` / `StoppedAutoServiceCount` / `UserLoggedOn` / `LoggedOnUsers`, so a Kerberos-broken /
    WinRM-down box shows an empty stopped-services list in triage (and blank Users-Online from a Vitals-only
    run). Needs new DCOM/WMI queries. Medium.
  - **Stale reboot-pending dot on DCOM-only boxes (narrow edge).** `ApplyVitals` only clears `RebootRequired`
    when `v.RebootPending` is non-null; `DcomVitalsProbe` returns null when all three reboot reads fail, so a
    Kerberos-broken box in Health mode can keep an amber dot after it rebooted (Check All clears it). Low.
  - **"Last reboot" grid column vs Readings card can disagree on DCOM hosts.** `ApplyVitals` keeps the prior
    `LastBootTime` when a DCOM read returns null while the Readings card shows "—". Low / cosmetic.
  - **Latent / no current impact:** RebootWave "Overdue — offline N min" wording before the box has actually
    gone offline (slow shutdown); `WuaUpdateLane` skips `SafetyCleanupAsync` on a currently-unreachable
    mid-stream Kerberos error; `HostWinRmGate` assumes a single `PSRunspaceHost`; `VitalsProbe` DCOM-fallback
    swallows the DCOM exception with no trace; RDP FullScreen not re-applied after Reconnect;
    `CrossDomainRdpViewModel.Dispose` skips the `HasSessions` notification; the Deploy / SoftwareCheck / Columns
    fire-and-forget dialog tasks have no top-level fault surface.
- **From the 2026-06-23 performance / leak hunt — not yet fixed (the one safe leak fix shipped; these touch the
  live-filtered grid / load-bearing `PatchState` hot path — the area behind the past cross-thread crash — so they
  need a dedicated, TESTED pass, NOT a quick knock-out):**
  - **Loading a big list is janky.** `AddComputers` / `SetComputers` add rows one at a time; each
    `ObservableCollection.Add` fires the full `OnComputersChanged` + `RaiseFleetChanged` cascade synchronously on
    the UI thread, so loading 300+ stutters and 500+ can freeze for seconds. A batched/suppressed add is the fix —
    BUT a naive `Clear`+range/`Reset` skips the per-row `PropertyChanged` re-subscription in `OnComputersChanged`
    (the `Reset` → `NewItems==null` trap), which would break live row updates. Needs a careful design + test. Medium. **Update (1.14.2):** the cold-start *freeze* that was conflated with this is FIXED — its real cause was thread-pool serial worker-injection (see `docs/cold-start-freeze-and-threadpool-findings.md`), NOT the row-add. The row-add cascade itself measured ~65–83ms for 319 rows — small; this stays a minor "smoother load" nicety, not a freeze.
  - **Fleet-recompute storm — remaining cleanup — DEFERRED, do not build without a trigger (rare-event only; the high-frequency progress-tick flood is
    already fixed — see DONE ▸ "Fleet-recompute storm — progress-tick slice shipped").** Now that a per-row
    `UpdateProgress` tick raises just `FleetProgress`, what's left fires ONLY on the rare phase-change events (a
    handful per row, not the per-tick flood), so the payoff is low. **Update (1.14.2) — DEFERRED:** the `UpdateProgress`-only → `FleetProgress`-only slice shipped (CHANGELOG "Smoother grid during large patch sweeps"). The remaining `Has*` double-walk + `PatchState`-parse-cache slices are technically still open, but cold-start instrumentation (2026-06-29, 319-box worst-case sweep) measured `FleetRecompute` ticks at ~0ms each — a near-zero gain inside the live-filtered crash zone. **Do not build these without a trigger. Revisit ONLY if** the patch sweep becomes *measurably* janky again, or someone is already editing `WorkspaceViewModel` for another reason and can fold them in cheaply. Both still sit in the load-bearing
    live-filtered grid area — **HANDLE WITH CARE** applies — and each should be its own dependency-verified,
    tested pass if/when it proves worth doing:
    - **`Has*` double-walk cleanup.** `HasFleetSummary`→`FleetSummary`, `HasVitalsFleetSummary`→`VitalsFleetSummary`,
      `IsPatchOperationOrFleetHeld`→`IsPatchOperationActive`, and `FilterStatus`→`VisibleRowCount` each re-walk the
      list (or the filtered view) a second time just to check a length/count. Collapse the redundant double
      evaluation so one `RaiseFleetChanged` doesn't pay for the same walk twice. Low payoff now. Low.
    - **`PatchState` `Enum.TryParse`-per-read cache.** `DerivePatchState` parses the phase string on every read
      (`Computer.cs` ~312). Same tier — only fires on phase changes now, not the progress flood; any cache MUST be
      invalidated when `UpdatePhase` / `RebootRequired` change (the staleness trap), so it needs care, not a quick
      memoize. Low.
  - **Lower-impact (perf):** `RaiseCanExecuteForSweepCommands` re-checks whole-list conditions + 10 command states
    per completed row (mitigated by short-circuit); the focused machine's update checklist repopulates row-by-row
    (O(n²) re-hook) instead of in bulk; the grids don't enable column virtualization. (The 60-second relative-time
    no-op refresh that was listed here was the one safe item and is now fixed.)
  - **Low-impact (leaks / hygiene):** old install-throttle `SemaphoreSlim` not disposed on a cap change; per-host
    WinRM-gate semaphores + the catalog `HttpClient` never disposed (bounded / app-lifetime).
  - **Checked and CLEARED (false alarms — do NOT chase):** "detail window leaks per open/close" (WPF weak events +
    the requery timer is stopped on close); "selecting rows freezes the grid" (WPF batches selection to one event);
    "the embedded-RDP host control leaks handles" (disposed on close; a manual dispose would break Reconnect);
    "Stop poisons the update-size cache" (the cancel token isn't passed to that lookup).

---

## PARKED — needs a signal/decision before it's worth building

- **Custom / predefined columns over DCOM (the "WinRM n/a" / "WinRM is broken" on Kerberos-broken boxes).** Distinct from the DCOM VITALS fix (`4c88c69`, DONE) — that filled the built-in vitals fields over DCOM, but the grid's custom columns and the predefined "Logged-on user" column each run their OWN private WinRM one-liner, have no DCOM fallback, and keep showing "WinRM n/a" / "WinRM is broken on this box…" on a Kerberos-broken box. **Why parked / hard:** a user-defined custom column runs ARBITRARY PowerShell, and arbitrary PS can't run over DCOM (DCOM does specific WMI queries, not general script) — so most custom columns fundamentally CAN'T fall back to DCOM. The PREDEFINED columns (known queries like "Logged-on user") might be special-cased to a DCOM/WMI equivalent — a separate, maybe-feasible investigation; the "Logged-on user" one could reuse the explorer-owner-over-DCOM query the vitals fix just proved works. **Build only if** "WinRM n/a" on those columns becomes a real day-to-day annoyance, and if so scope it to PREDEFINED columns only. Raised as a "wondering" during the vitals work, not a committed item.
- **Scheduled-task SECOND mode — "each machine's local time" (per-box wall-clock).** A deliberate, labeled companion to the shipped "operator's time = same absolute instant fleet-wide" mode: this mode would fire the picked time at that wall-clock on EACH box's OWN local zone, so a fleet across zones fires at N different absolute instants. Best for "2 AM local quiet hours honored per-box" when the operator does NOT want one synchronized moment. **Parked deliberately:** the base case had to be proven first (now done + live-verified), and this is a distinct feature — it changes the picker (a mode toggle / two clearly-labeled options), the "scheduled for…" message (must state WHICH mode fired), and the trigger string (this mode emits the bare no-offset local string — literally the just-fixed old behavior, made intentional and labeled). **Labeling trap:** "local time" alone is ambiguous (whose local?). Proposed framing: **"2:00 PM my time (here)"** vs **"2:00 PM on each machine"**, each with a one-line consequence sub-line. Needs its own design + red-team pass. Note: the code already knows how to build the no-offset string (it's what the old bug produced), so the work is the mode toggle + labeling + routing the two modes to the two string forms — NOT new trigger plumbing.
- **"ping = online" is Vivre's core reachability definition — the deeper cause behind the off-from-start "Offline" fix (`032293f`).** That fix worked AROUND it (a "was ever genuinely managed" flag) rather than changing it. The root cause is that `ProbeReachabilityAsync` equates an ICMP ping / DCOM host-probe success with "online", so a powered-off server whose BMC/iDRAC/iLO, reused IP, or DNS answers ping reads as online. **Changing the core definition (require a real remoting success to count as "online") is a fleet-wide ripple** — it moves the green online dot, `OnlineSummary`, the online/offline filters, and every consumer of `IsOnline` — out of proportion to the messaging fix already shipped. **Build only if** BMC-answering-but-off boxes become a recurring real problem AND the messaging fix proves insufficient. Otherwise leave it; the managed-flag workaround is the right-sized answer.
- **Pre-flight DISM-vs-WUA detection** — no reliable predictor found (DO absence is fleet-wide golden
  image, not a predictor; try-WUA-then-fail wastes 1+ hr; failure isn't tied to consistent boxes).
  Current working answer: just run DISM for 2016 boxes you own (the toggle above). Revisit only if a
  cheap reliable signal appears.
- **Script execution / other ops over SMB on Kerberos-broken boxes** — the read-only investigation
  landed and the chosen near-term answer shipped (clean honest gate with plain WinRM-unavailable
  guidance, live-verified). Deferred richer option: scheduled-task-over-SMB delivery to actually RUN
  scripts on Kerberos boxes. Build only if the gate proves insufficient in practice (don't build
  arbitrary-script-over-SMB-as-SYSTEM without a strong reason).
- **Force-reboot-over-RPC/SMB** as an additional wave fallback path — only if the DCOM + SMB reboot
  paths prove insufficient at scale.
- **Embedded RDP magnification** — the remote renders compact vs mRemoteNG's bigger/readable image. Proved
  SmartSizing will **NOT** upscale inside `WindowsFormsHost`; mRemoteNG's size comes from pure-WinForms
  framework DPI scaling its WPF-airspace host can't replicate. Recommended next lever: a **per-host
  display-scale toggle** (real scale default, force-100% on cluster/FCM boxes). Full findings + all candidate
  paths in `docs/vivre-rdp-scaling-and-fcm-findings.md`. (The throwaway `[RDP fill diag]` scaffold + the
  ÷1.5 / undock+explicit-Size experiments are parked in `git stash` for a future Fit-To-Panel attempt.)

---

## DONE (committed) — recent

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
  keep the tab open with a Reconnect button (a deliberate sign-out is distinguished from a drop via
  `ExtendedDisconnectReason` 2/4/6); `EnableAutoReconnect` + `GrabFocusOnConnect` are wired. Full-screen
  reflows the session to monitor resolution (`MonitorPixelSize`) and restores on exit. See
  `vivre-rdp-scaling-and-fcm-findings.md`. (This was the previous DO-NEXT #1.)
- **Embedded RDP — Failover Cluster Manager context menus fixed** (`1ce1abf`, on master): pinned the RDP
  session display scale to 100% (`DesktopScaleFactor=DeviceScaleFactor=100`), sidestepping the documented
  FCM >100%-scaling menu-collapse bug. Session was measured at 150% (the cause) vs mRemoteNG's 100%. Fills +
  FCM-safe; trade-off is a compact (native-100%) image. Magnification to match mRemoteNG is PARKED (see
  above + `docs/vivre-rdp-scaling-and-fcm-findings.md`).
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
  `4e34f02` · `cfba5e8`; **on branch `feat/transient-wua-retry` — operator merges + pushes**). **Root cause
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

---

## KNOWLEDGE DOCS — current set + refresh status
Project knowledge now holds exactly: `key-file-path-map.md`, `vivre-backlog.md`, `2016-LCU-lane-spec.md`,
`2016-LCU-red-team-review.md`, `2016-LCU-panel-spec.md`, `vivre-rdp-scaling-and-fcm-findings.md`
(under `docs/`), plus the top-level `CLAUDE.md`, `README.md`, `CHANGELOG.md` — and `windows-patching-lane.md` (now under `docs/` too).
All were refreshed in the **2026-06-23** code+docs audit against the as-built code:
- `key-file-path-map.md` — refreshed: `Is2016` corrected to `LcuRouting` (not `Computer.cs`), the decaying
  "Recent commits / restore-point" list removed (use `git log`), the duplicated PS 5.1 gotchas reduced to a
  cross-reference to CLAUDE.md, and the new pure-decision helpers added (`RebootVerifyMenu`, `Lcu2016RowState`,
  `ScopeToggleRule`, `ComponentCleanupClassifier`).
- `2016-LCU-lane-spec.md` / `-panel-spec.md` / `-red-team-review.md` — refreshed: the install path is
  `dism.exe /add-package` on expand.exe-extracted `.cab`s (not `Add-WindowsPackage`), the lane is **opt-in
  per box** (`RequiresStagedPatching`), and the build-sequencing "future work" / resolved red-team risks were
  retired. Cycle-specific KB/UBR is "this cycle" by design.
- `vivre-rdp-scaling-and-fcm-findings.md` — refreshed: the Reconnect work is now DONE (was the deferred item);
  the magnification investigation + candidate paths remain the load-bearing record.
- `windows-patching-lane.md` — refreshed: the agent's five operation modes (Install / Uninstall / Scan / AddPackage /
  Cleanup), the scan-on-`RemoteSessionLostException` SMB fallback, and the component-store cleanup lane.
