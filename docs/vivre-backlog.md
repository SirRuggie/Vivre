# Vivre — running backlog (deferred items & open threads)

> Working tracker for things found during build work that are NOT yet done.
> As items get fixed, move them to DONE with the commit hash. Add new finds under the right tier.
> **Order below is the recommended do-next order** (Ruggie can override — it's a recommendation,
> not a mandate). Last refreshed: **2026-07-21** (release **1.16.4** — the WUG state-check arc:
> cold-start mass-unknown SSL fix + IP exact-match reclassification, see
> docs/wug-state-check-findings.md; 1.16.1–1.16.3 were same-day fix releases, see CHANGELOG.md).
> Older release narration lives with its DONE entries in docs/archive/vivre-backlog-done-archive.md.
> Everything below is on `master`. **Commit hashes in the DONE list predate a history rewrite and may
> not all resolve — `git log` is the authoritative restore-point list, and the per-entry test counts
> are point-in-time only (current suite is 1054 green as of 2026-07-21).**

---

## ▶ DO NEXT — recommended order

**Audit findings (2026-07-01) — status as of 2026-07-21 (release 1.16.4; suite 1054 green):** the full five-lens audit
record is `docs/archive/vivre-audit-findings.md` (point-in-time, never edited). **Both HIGHs are CLOSED** —
HIGH-1 dead-worker-undetectable (`852662d`) and HIGH-2 one-hung-box-freezes-monitor (`7e2102c`) —
**and every actionable MED is now closed** (the batch-sequential residue closed this pass — see the bounded-loops DONE entry)**:** Stop-during-SMB-copy + settings-save-invisible-in-Release
+ Enable-WinRM no-timeout/sequential (`7e2102c`), cancel-clears-chip-on-failed-unregister +
SCCM-ClientSDK-false-green (`289878f`), the post-reboot false-green cluster (`f965b29`), the install
re-entry `installBegan` race (`832aa7f`), and the orphan `Vivre_Reboot_*` service (`a008747`) — plus
the audit-adjacent install wall-clock incident (`12a5e36`), the TriggerSchedule / atomic-settings /
LastBootTime trio (`d600009`), the atomic computer-list save (`1add64f`), and the session-found
users-online false-green (`f26a7c4`). The audit LOW "stale agent doc comments promise a reboot the
code excised" (audit doc ▸ LOW, Program.cs :58/:826/:911) is **CLOSED** — all three rewritten to the
reported-only truth, plus a fourth of the same class found in the sweep (the `Summarize` docstring's
"the actual reboot is the caller's job").

**Still open** (none is day-to-day work — no urgent items remain):
1. **Credentialed WinRM blocked by ambient Kerberos rejection** — `RoutingPowerShellHost.cs:59`
   fast-fails before the credential parameter is consulted; the cache has no eviction or credential
   dimension. Research-first, remoting-cache zone. PARKED until it bites in practice.
2. **Details-window CollectionView leak** — **MEASURE FIRST**, do not fix on theory.
3. **Stop button can't stop a monitor-only tab** (found by the 2026-07-11 help audit) — PARKED; the
   app-side fix (rebind Stop's `IsEnabled` to the command, or reword the tooltip) is a separate decision.
   - **The issue:** the toolbar Stop's `IsEnabled="{Binding SelectedTab.IsBusy}"` (MainWindow.xaml:436)
     defeats `CanStop()`'s monitor-only intent, and the Monitor tooltip repeats the false claim ("Stop
     halts it"). The help already documents the truth: the Monitor toggle is the only way to pause
     monitoring.
   - **STILL OPEN, precisely:** the monitor-only mismatch (`CanStop()` true on `IsMonitoring` alone
     while :436 stays dark) and the schedule/cancel scheduled-task loops; the MainWindow.xaml:436
     binding itself is UNCHANGED. (Same root line, opposite symptom: :436 also kept Stop honestly DARK
     during the schedule/cancel loops — see the bounded-loops DONE entry. With those loops now
     60s-bounded, wiring Stop for them is optional polish contingent on this item's decision — fix the
     :436 binding once, properly, if at all.)
   - **TRAP for whoever fixes this (load-bearing):** the schedule/cancel loops' outer-cancel catches
     `return` silently — dead code today since nothing passes a token, but once Stop reaches these
     loops it will quit with NO "Cancelled — N of M processed" activity line. The fix MUST ship a
     VISIBLE abort — add the summary line when wiring the token, or the operator can't tell a completed
     run from an aborted one.
   - **Resolved along the way (WUG streaming pass, 2026-07-14):** a THIRD instance of this pattern is
     CLOSED by construction, not by touching :436 — `CheckWugStateAsync` runs as a PASSIVE
     `BeginOperation(..., registerRows: false)`, so Stop LIGHTS via `IsBusy` and genuinely cancels it
     (killing the `powershell.exe` child), with the abort VISIBLE exactly as the trap demands: it logs
     "Stopped — N of M checked" (`ComposeStoppedMessage`) and stamps unreached rows
     `WugRowText.NotChecked` ("not checked (read stopped)"). That passive-op rail is the right template
     for wiring this item's remaining sites.
4. **Scheduled-reboot "stampede" — CLOSED, CONSCIOUS ACCEPT (operator decision, 2026-07-13). Do not
   re-raise; build nothing.** The facts stand: scheduled reboots have no burst stagger — every
   selected box fires `shutdown.exe /r /f /t 0` locally at the SAME absolute UTC instant
   (`ScheduleTimeFormatter` StartBoundary=…Z), and the wave's `_rebootTriggerThrottle`(12)+jitter
   never touches this path. **Why that is fine:** the wave throttle exists because a wave makes
   VIVRE fire ~20 simultaneous REMOTE calls — 20 simultaneous auths against the DCs. A scheduled
   reboot has none of that: the task already lives on the box, the box reboots itself locally at
   trigger time, and Vivre isn't even running. The DC/DNS/auth burst the throttle protects against
   structurally cannot happen here. What remains is "the boxes I picked go down at the time I
   picked" — which is what a maintenance window IS. **The one condition that would reopen this:**
   batching interdependent boxes into one instant (DCs/DNS together, or a SQL box + its app tier) —
   that is per-run operator judgment, not a code problem.
5. **Shared-settings stomp guard (optimistic concurrency) — DEFERRED, build before real multi-operator
   use.**
   - **In already (the prerequisite):** the write path is `SharedSettingsStore.Update(Action<SharedSettings>)`
     — a **sibling-key-safe read-merge-write** that changes only the keys the delta touches, preserves
     every other key (including a future build's), and **refuses (throws) if an existing file can't be
     read** instead of stomping unread keys with defaults. That closes the *single-writer* wipe vector
     that once blanked `StagedHosts` (a degraded `Load` returned defaults, then a whole-file `Save`
     wrote them back over everything).
   - **What REMAINS — the concurrent-writer race:** writes are still **whole-file last-writer-wins**
     between operators. Two operators editing at once, or one saving while another's Vivre holds an
     older in-memory copy, each still merges onto the file **as they last read it** — so the later
     writer's merge can revert a key the earlier writer just changed. Acceptable for the current
     single-admin reality.
   - **When building it:** add an optimistic-concurrency guard — read a version/mtime inside `Update`,
     re-check it just before the atomic write, and reject-and-reload on mismatch. Adjacent small
     hardening while in there: `AtomicFileWriter` uses a **fixed-name `.tmp`** (`path + ".tmp"`), so two
     processes writing the same file at once can collide on the temp — give it a per-write unique
     suffix. (No migration/copy/stomp-guard shipped with the initial split, by design.)

The RDP Reconnect button (a previous #1) shipped — see DONE. The 2016 staged-patching toggle shipped
(see DONE), and **KB auto-population from a scan is closed — manual only** (decision recorded under
*Settings simplification* below). Beyond the Still-open items above, what remains is the polish /
standalone items further down, each "do only if it recurs / when a signal appears."

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
- **Clear the CU month label when the KB is hand-edited.** The 1.16.0 CU month label (read from the staged
  package — see DONE) is not cleared when the operator manually changes the KB, so a stale label from the old
  package can sit on a newly-typed KB and mislead the team about which cycle they're on. Clear (or re-derive)
  the label whenever the KB field is edited by hand.

### Unverified / reboot-verify follow-ups (from the 1.16.0 Unverified + reboot arc — see DONE)
- **Monitor self-heal for the Unverified state.** A later successful monitor probe updates the reboot dot but
  never rewrites the stale "couldn't confirm — re-check" message or clears the **Unverified** chip, so a box
  that has since become verifiable keeps reading Unverified until the operator acts. Extend the `was == true`
  monitor guard so a subsequent clean probe rewrites the message and chip and the fleet converges on its own —
  no operator action needed.
- **Reboot-pending-probe DCOM fallback for the Kerberos cohort.** The post-reboot reboot-pending probe is
  WinRM-only, so a Kerberos-broken box (WinRM auth rejected) can NEVER be confirmed after a reboot — it is
  **guaranteed Unverified** every cycle. Add a DCOM fallback: the reboot-pending registry legs need no WinRM,
  and the `DcomRebootReadinessProbe` machinery already exists — wiring it in rescues that cohort so its boxes
  can verify like the rest. (Design settled, build pending; related to the parked Kerberos remoting-cache
  work in DO NEXT #1 and the drift-hunt "stale reboot-pending dot on DCOM-only boxes" note below.)

---

## OPEN — polish / smaller standalone items

- **Custom columns are intentionally NOT exported from the Patching grid** (they only render on Health; the shown-rows CSV mirrors the clicked grid) — readdress only if a Patching-grid custom-column export is ever wanted.

- **Auto-heal for "couldn't rescan" Unverified boxes — PARKED, gather signal first.** After a
  reboot-verify, B/C/D Unverified boxes (rescan itself failed) need a manual rescan; 1.16.2
  already self-heals the common variant-A case. Design is DONE + red-teamed (auto-rescan, or a
  simpler one-click "Rescan all Unverified" button — leaning button). Build nothing until a few
  patch cycles show how often this actually recurs; today's cases were largely a broken SCCM
  client on one box. Full design lives in the Action-3 gate discussion.

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
- **SHA-256 package-hash verification for the staged CU package.** Stage integrity is byte-count only today
  (no content hash of the `.msu`/package — see the fan-out item above), so a silently-truncated-but-right-size
  or corrupted copy would pass. Add a SHA-256 verify of the staged package against the source. Small, standalone.
- **Staged-machine list "copy all / export" UX gap.** The Settings staged-patching management card lists /
  Removes / Clears the flagged machines but has no way to copy the whole list out (clipboard or file export),
  so an operator can't easily hand the staged set to a teammate or record it. Add a copy-all / export affordance.
- **Clean UI-sluggishness at ~50 concurrent cleans — watch item (perf).** Since Clean went
  selection-driven (`9133226`, shipped 1.14.x-era — resolution record in
  docs/archive/vivre-backlog-done-archive.md), it cleans ALL selected 2016 boxes (up to ~50), so a lag
  at ~50 concurrent cleans is a **live signal**, not a hypothetical. The measurement apparatus (Probe
  A thread-pool / Probe B cap / Probe C layout, plus the `Width="Auto"` message-column A/B via
  `VIVRE_CLEAN_MSG_FIXEDWIDTH`) is parked on throwaway branch `perf/clean-measure`. Keep it parked;
  only pursue if a real 50-box clean actually lags.
- **Proactive gray-out of known-WinRM-broken boxes** — visually mark boxes that are known to fail WinRM
  so the operator isn't surprised, rather than only learning at action time. Design pass needed.
- **Idle-monitor reachability throttle** — throttle added (d3b5ed0). Verify it holds at scale
  (300 boxes); drop from list once confirmed in practice.
- **Bottom-panel resize freeze with large row counts** — row virtualization added (ce624d4); appears
  fixed. Watch under heavy update-scan load; if it returns, next lever is the `Width="Auto"` columns
  forcing full-width measure. (Measured in the 1.14.2 cold-start hunt: the Auto-width column measure is ~120–180ms/pass — real but minor, NOT a freeze cause.)
- **Scan-timeout edge** — 5-min cap (a997642) may be short for the very worst first-scan boxes; bump to
  10 min (600s) ONLY if real "Scan timed out" false-positives appear.
- **Two schedules on one box overwrite the host-keyed `_scheduledTasks` entry** while the target
  accumulates uniquely-named `Vivre_WUA_{runId}`/`Vivre_Reboot` tasks — the chip's time and a surviving
  task's trigger can diverge (e.g. after a partial cancel). Pre-existing, narrow; found during the
  `289878f` cancel-chip red-team. Fix would key tracking per task, not per host.
- **No Desktop test project** — three of the four `7e2102c` fixes have no unit-test home (the 120s
  reboot-probe timeout, the settings ActivityLog hook, parallel Enable WinRM), and every per-host
  timeout in the app (incl. the existing vitals/health ones) is trust-the-pattern. A
  `Vivre.Desktop.Tests` project + a delaying `IPowerShellHost` fake would unlock coverage for all of
  them at once. Recurring theme across three sessions — worth doing when test appetite is high.

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

- **Update-untick carry-forward → false "No updates selected" (found 2026-07-21) — PARKED, not a day-to-day issue.**
  A rescan can wrongly carry forward Vivre's own post-install untick rather than the operator's, so a re-surfaced update comes
  back unchecked. Confirmed anchors: `WorkspaceViewModel.cs:3560` (install-success untick), `:4693` (checklist rebuild preserves
  tick-by-KB), `:3423` (empty-checklist gate), `:4349` (reboot-wave re-tick, which doesn't share this problem). Open design question on revisit: reconcile with the wave's blanket re-tick, which currently overrides operator unticks too.
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
- **APVPATCHING — "New-ScheduledTaskAction is not recognized" — PARKED by the operator (known; do NOT
  troubleshoot ad hoc).** Scheduling an action on APVPATCHING fails with `New-ScheduledTaskAction` not
  recognized (the ScheduledTasks module cmdlet is unavailable on that host). The operator has parked this
  deliberately — it is a known condition of that one box, not a Vivre bug to chase. Do not investigate or
  "fix" it opportunistically; act only if the operator re-raises it.
- **Per-host RDP display-scale toggle — CLOSED / DISPROVEN, do not build.** The old theory
  (mRemoteNG = WinForms framework DPI scaling; recommended lever = a **per-host display-scale
  toggle**) was disproven: mRemoteNG ships DPI-unaware, and ANY session scale above 100% breaks FCM
  context menus, so the toggle is a dead lever. The magnification itself **SHIPPED in 1.15.0** via
  the OCX's client-side **ZoomLevel** with the session pinned at 100% (THE PIN CARDINAL) — see the
  1.15.0 DONE entries. Full record: `docs/vivre-rdp-scaling-and-fcm-findings.md`; method:
  `docs/freeze-hunting-playbook.md`.

---

## DONE (committed) — recent

- **WUG state-check arc — cold-start mass-unknown + IP substring false-classing — DONE, shipped 1.16.4**
  (`c6463d2` · `a19d150` · `c49b4da`; `4b158c5` was a superseded intermediate). The module re-armed a runspace-bound
  scriptblock cert callback per API call (cold TLS → mass LookupError), and 0-exact IP substring hits read
  Ambiguous; now a compiled delegate + no `-IgnoreSSLErrors` + exact-count classing. Full story: `docs/wug-state-check-findings.md`.

- **Reboot arc — Reboot & Verify reaches patch-failed boxes + Force reboot falls back to DCOM on Kerberos
  boxes — DONE, shipped 1.16.0** (`cabe230` · `e016c4f` · `039ce6f`, on master). (1) **Reboot & Verify is now enabled on a
  box in `Error` that still needs a reboot** — a patch that failed but left the box reboot-required could not be
  sent through Reboot & Verify (the menu gated `Error` out), stranding it; the gate now admits Error +
  reboot-required. (2) **Force reboot falls back to DCOM** when the WinRM auth is rejected on a Kerberos-broken
  box (`0x80090322`) — routed through the existing, already-Kerberos-capable `DcomRebootTrigger`, so the one
  manual Force-reboot action no longer dead-ends on the cohort the reboot wave already handles. (3) The **Reboot
  message column narrates live** through the reboot → offline → back-online sequence. Cardinal: reboot stays
  operator-confirmed and `Win32Shutdown` stays confined to `DcomRebootTrigger`. (Verdict record: MEMORY ▸
  "Kerberos reboot dead-end verdict".)
- **Neutral "Unverified" patch state — couldn't-confirm / couldn't-rescan boxes no longer read green — DONE,
  shipped 1.16.0** (`6f3a4ed`, on master). A box whose post-reboot reboot-pending probe or WUA rescan could not
  complete used to fall through to a green "Up to date"; it now shows a neutral **Unverified** state (not green,
  not red) across all five surfaces it can appear on. Extends the honest-post-reboot
  cluster (`f965b29`) — a couldn't-confirm/couldn't-rescan outcome is now visibly "we don't know," never a false
  green. **Known follow-up gaps are OPEN** (see OPEN ▸ patching features ▸ *Unverified / reboot-verify
  follow-ups*): a later successful monitor probe does not yet self-heal the stale message/chip, and the
  Kerberos cohort's WinRM-only reboot-pending probe leaves those boxes permanently Unverified.
- **2016 CU auto-read from the staged package + CU month label — DONE, shipped 1.16.0** (`794baf4` the month
  label, on master). The 2016 CU KB is now read from the staged `.msu` package (`MsuPackageReader`,
  product-pinned) and offered side-by-side with the existing Settings value as a **read-and-confirm** — never a
  silent overwrite of a deliberately-set KB (the same guardrail that kept KB-auto-population-from-scan manual
  only, above). A **month label** derived from the package annotates the CU so the team can see which cycle it
  is. (OPEN follow-up: clear that label when the KB is hand-edited — see *Settings simplification*.)
- **Personal / shared settings split — DONE, shipped 1.16.0** (`003c963`, on master). Per-operator preferences
  stay personal in `%APPDATA%\Vivre\settings.json`; the shared operational settings (this month's CU,
  LCU/package folders, WUG server, staged-machine list) move to a machine-wide `SharedSettingsStore` at
  `C:\ProgramData\Vivre\settings.json` (Authenticated-Users Modify ACL, fresh uncached read per `Load`). Writes
  go through `Update(Action<SharedSettings>)` — a **sibling-key-safe read-merge-write** that changes only the
  keys the delta touches, preserves every other key (including a future build's), and **refuses (throws) on a
  degraded read** rather than stomping unread keys with defaults (the fix for the save that once wiped
  `StagedHosts`); an `Update`-time reflection guard throws on a credential-shaped field. **This is the
  prerequisite the DO NEXT ▸ stomp guard called for** — concurrent-writer optimistic concurrency and the
  `AtomicFileWriter` fixed-name `.tmp` hardening remain OPEN (DO NEXT #5). No migration/copy shipped with the
  split by design.
- **Settings-window UX — reorg + 2016 CU plain-language relabels + catalog link + numeric-box typing fix —
  DONE, shipped 1.16.0** (no single hash — 1.16.0 Settings polish). The Settings page was reorganized and the
  2016 CU / patching fields relabeled into plain language, with the Microsoft Update **update-history / catalog
  link** that makes the manual KB+UBR lookup a two-click copy (already referenced under *Settings
  simplification*). The numeric Settings boxes (Max simultaneous installs, WUG state-check concurrency) now
  commit **on LostFocus only**, so an intermediate value typed mid-edit no longer clamps/rewrites under the
  operator. Reorg was pure-XAML-safe (MEMORY ▸ "Settings reorg + 2016 CU auto-read verdicts").
- **Health-grid column caps + calm name-resolution offline — DONE, shipped 1.16.0** (no single hash — 1.16.0).
  The health grid's columns gained width caps (no single column blowing out the layout), and a `PingErrorKind`
  name-resolution failure (DNS can't resolve the host — WSAHOST_NOT_FOUND / 11001) now reads as a calm
  **Offline** instead of a red error — a host that no longer resolves is off/decommissioned, not a fault to
  alarm on (MEMORY ▸ "Health-grid false-alarm arc").
- **WUG state-check SPEED — pooled per-name lookups — DONE** (this pass: chunks A + B + a test-speed pass,
  uncommitted at writing; suite 873 → 897). The read-only "Check WhatsUp Gold state…" read now runs its
  per-name `Get-WUGDevice` lookups a few AT A TIME instead of strictly one-by-one — a runspace pool INSIDE
  `StateScript` (`StateResolveLoopScript`), sized by the operator's new **Settings ▸ "WhatsUp Gold state
  check — simultaneous lookups"** (`AppSettings.WugStateConcurrency`, default **2**, clamp **1–4** →
  `StateReadMaxConcurrency`; `VIVRE_WUG_CONCURRENCY` absent = 1 = the untouched sequential branch). Measured
  ~2× win: a 324-box run drops from ~6.5 min to ~3 min at N=2. Streaming is unchanged — rows still fill in
  live, Stop still cancels + kills the child, the 90s stall watchdog + 45-min ceiling still bound it. The
  four fan-out traps are all honoured (T1 `DefaultConnectionLimit=32` before the first request; T2 connect
  ONCE-per-runspace, no shared auth globals; T3 completion-order poll-drain, not `WaitHandle.WaitAny`/
  submission-order `EndInvoke`; T4 the external C# stall watchdog stays the sole wedge authority since
  `PowerShell.Stop()` can't interrupt a blocked `Invoke-RestMethod`); the shared `ResolveFunctionScript` +
  `Process-WugOutcome` are single-sourced into both branches, and `__WUGDEV__` is emitted only from the main
  drain thread. A per-lookup latency tally appends "WUG lookups slowed during the run…" (+ "consider lowering
  the concurrency setting" at N>1) when the average exceeds 2× the first-5 baseline.
  **The cap (default 2, ceiling 4) and WHY:** the live Gate 0 ramp measured the 1→2 halving as the whole win;
  2→4→8 stayed flat with per-lookup latency creeping UP (WUG serialises under load), so >4 is pure extra load
  on the one box that watches the whole fleet for no wall-time gain.
  **This is the SPEED fix that REPLACED the old DO-NEXT-#5 "WUG bulk-fetch prefetch" idea, now DEAD — DO NOT
  re-propose.** The bulk idea (pull ONE paged inventory up front and match in-script, falling back to per-name
  lookups only for the leftovers) was MEASURED and rejected: a single unfiltered bulk pull took **426s for
  1469 devices** — SLOWER than the per-name sequential lookups it was meant to beat (per-lookup ~1.1s live,
  1.0–1.7s; ~6.5 min sequential on 324). Pulling one big inventory loses to N targeted lookups, so the speed
  fix is concurrency on the targeted lookups, not a bulk pull.
  Suite wall-clock stayed in its ~87s ballpark despite the new real-`powershell.exe` pool process tests: they
  ride the `VIVRE_WUG_MODULE_OVERRIDE` seam + a COMMITTED stub-module fixture
  (`Vivre.Core.Tests/Wug/Fixtures/WugStubModule.psm1`, copied to the test output) through the SAME
  `ImportPSModule`-by-path path, skipping the real WhatsUpGoldPS ~8s-per-runspace cold-load; one real-module
  smoke test still exercises the production import. Cardinal clean (read path; no reboot).
- **WUG resolver identity-verify + error honesty — DONE** (`b67ed55`; suite 852 → 873). Shipped without its
  own docs round — this is that round. The per-name resolver was SINGLE-SOURCED into one
  `ResolveFunctionScript` spliced into both the set path (`Script`) and the state read (`StateScript`) so they
  can't diverge on how a name maps to a WUG device. Matching is now a normalized, case-insensitive,
  DOT-BOUNDARY compare (`Test-WugNameMatch`) against `name`/`hostName`/`displayName` (each presence-guarded) +
  a `networkAddress` clause for IP-literal inputs — REPLACING the dead `displayName -eq $srv` verify (null for
  FQDN-registered fleets) and the de-facto `$results[0]` pick; the dot boundary rejects prefix collisions
  ("APVSQL1" ≠ "APVSQL10.domain"). Outcome is exactly one of MatchedByName / MatchedByIp / NoDevice /
  Ambiguous / LookupError. **An errored search reads UNKNOWN (`LookupError`), NEVER a false "no matching
  device"** — only a clean-empty answer everywhere is `NoDevice`, so a struggling server can no longer
  masquerade as a fleet of ghosts. The set path's all-nothing guard is FAIL-SAFE: a `LookupError`/`Ambiguous`
  box over-reports failure (re-setting maintenance is idempotent) rather than silently claiming "set" for a box
  never cleanly looked up. Cardinal clean.
- **WUG streaming state-check arc — DONE** (this pass, uncommitted at writing; suite 823 → 852, +29).
  The read-only "Check WhatsUp Gold state…" read now STREAMS one result per machine (`__WUGDEV__`) as
  WUG answers, instead of going silent and dumping everything at the end. A `StateReadStallTimeout`
  (90s) watchdog — reset ONLY by a device line — catches a wedged run and names where it stopped
  (`ComposeAbortError`), backstopped by a `StateReadCeiling` (45min) runaway cap; both REPLACE the old
  `min(60+5·N, 600s)` total cap that guillotined slow-but-working runs. An aborted read (stall / ceiling /
  Stop) KEEPS the per-device results already streamed (partial map, snapshot-copied under a lock against
  the killed child's draining pump) and stamps unreached rows the new distinct state
  `WugRowText.NotChecked` = "not checked (read stopped)" (never "unknown", never "no matching device").
  Stop is wired via the passive-op rail (`BeginOperation(registerRows: false)`) — it lights, cancels, and
  KILLS the `powershell.exe` child, and logs "Stopped — N of M checked"; a generation guard supersedes a
  first check with a second. The summary parse (`ParseMaintenanceState`) now REQUIRES the `__WUGRESULT__`
  marker — the last-braced-line fallback was DELETED, because with device lines on the wire it could parse
  a trailing `__WUGDEV__` line AS a clean-but-empty summary → a quiet false green; `ParsePreflight` keeps
  its fallback (no streamed lines there). Kill-on-cancel also covers the SET path (`RunAsync` via the
  `RunCoreAsync` seam) — before, a cancelled maintenance set kept running and could still flip WUG
  maintenance after the UI said "cancelled". **Suite 823 → 852** (the pre-arc 810/817 doc claims were
  stale; 823 was the measured 2026-07-14 baseline — chunk 1 → 843, chunk 2 → 852, incl. 6
  real-`powershell.exe` process tests that took the suite ~5s → ~23s). Cardinal clean (read path; no
  reboot).
- **Schedule/Cancel scheduled-task loops bounded per row — DONE** (this pass, uncommitted at
  writing; suite 810 → 817). The audit MED's two remaining sites are fixed: `ScheduleRebootSelectedAsync`
  and `CancelScheduledTaskSelectedAsync` now wrap each row's invoke in a linked **60s** CTS (the
  `d600009` client-action budget — the per-host WinRM gate wait + the 20s connect + the invoke all
  count against it), so a hung box fails ITS OWN row and the loop completes the rest of the selection
  (before: one hung box stalled the list forever — worst case, later boxes' `Vivre_Reboot` tasks were
  never cancelled and still fired). Deliberately **SEQUENTIAL** — `ScheduledNextRun` is a live-filter
  input (`RowMatchesFilter` ▸ Scheduled, WorkspaceViewModel.cs:457) and the bare awaits keep every row
  write on the UI thread; do not parallelize (the `7d8abd4` cross-thread class). New pure
  `ScheduleRegistrationOutcome` (Vivre.Core/Updates, test-pinned) encodes the register-side ASYMMETRY:
  an unconfirmed (timed-out) registration is treated as **Scheduled — "couldn't confirm; verify on the
  box"** (before: the chip stayed DARK over a possibly-armed task — the dangerous direction); a cancel
  timeout NEVER clears the chip ("task may still fire" — invariant pinned in
  `ScheduledTaskCancelOutcomeTests`, extended with the exact-match case/whitespace pins). **The whole
  don't-know CLASS is closed, not just the timeout** (`ScheduleRegistrationOutcome.IsUnconfirmedFailure`,
  test-pinned per door): a MID-INVOKE session drop (`RemoteSessionLostException.AtConnect == false` —
  the type's own contract says work may be in flight), a cancel that trips mid-request, and any untyped
  mid-call escape all light the chip; a row goes dark ONLY on proof the command never ran (connect-phase
  loss, Kerberos rejection, shell-init) or the box's own failure report (HadErrors / a terminating
  in-script error). The cancel loop needed no bucket work — it only ever clears on the verified REMOVED.
  **Correction to the audit record (the audit doc is point-in-time and never edited, so this is the
  only place the truth can live): the audit rated this MED partly on the mitigation "these are
  cancellable IAsyncRelayCommands, so Stop recovers them" — that mitigation was DISPROVEN. The
  handlers were plain async-void with no token, no `BeginOperation`, no `_activeCts` registration:
  Stop never lit for these loops (`IsBusy` stays false) and, if pressed, would have cancelled
  nothing.** Stop's darkness here shares its root line with Still-open #3 (MainWindow.xaml:436) but
  was the HONEST direction, so no Stop change shipped — that remains #3's decision.
- **Patching-lane status snapshot (moved verbatim 2026-07-22 from windows-patching-lane.md ▸ Status):**
  - **Done:** scan; install with live progress; uninstall (WUA + DISM, with the cumulative-update reason
    surfaced); schedule (install + reboot + cancel); the reliability mechanisms above; the per-machine
    detail window; force-reboot.
  - **Verified live on real targets (2026-05):** scan (with counts), install with live progress,
    uninstall (including a cumulative update correctly reporting "can't be removed" / `0x800F0825`),
    a scheduled reboot firing at its set time, and the "WinRM unhealthy" degraded-host flag coming and
    going as the self-heal re-tests. One box flapped WinRM-unhealthy intermittently — confirmed to be
    the back-off / self-heal working as designed, not a regression.
- Older DONE entries (pre-1.16.0 — 1.15.x and earlier, back to the shell refactor) moved verbatim to docs/archive/vivre-backlog-done-archive.md.

---

## KNOWLEDGE DOCS

The doc inventory (every doc, one-line purpose, read-when cue) lives in **docs/README.md** — the single map.
