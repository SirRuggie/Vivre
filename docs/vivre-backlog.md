# Vivre — running backlog (deferred items & open threads)

> Working tracker for things found during build work that are NOT yet done.
> As items get fixed, move them to DONE with the commit hash. Add new finds under the right tier.
> **Order below is the recommended do-next order** (Ruggie can override — it's a recommendation,
> not a mandate). Last refreshed: **2026-06-23** (post full code + docs audit). Everything below is on
> `master`. **Commit hashes in the DONE list predate a history rewrite and may not all resolve — `git
> log` is the authoritative restore-point list, and the per-entry test counts are point-in-time only
> (current suite is ~600 green).**

---

## ▶ DO NEXT — recommended order

Nothing queued. The RDP Reconnect button (the previous #1) shipped — see DONE. The 2016 staged-patching
toggle shipped (see DONE), and **KB auto-population from a scan is closed — manual only** (decision
recorded under *Settings simplification* below). What remains is the polish / standalone items further
down, each "do only if it recurs / when a signal appears."

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

- **Stage copy fan-out I/O contention (UI sluggish during big batch Stage)** — NOT a UI-blocking copy and NOT
  a wrong-thread hash (both were earlier guesses). The 1.7 GB Stage copy already runs OFF the UI thread
  (`SmbAgentLane.cs:230-246` — `File.Copy` inside `Task.Run(...).ConfigureAwait(false)`), and integrity is
  byte-count only (no SHA-256 of the package). Real cause: **copy fan-out** — up to the install cap hosts each
  `File.Copy` the SAME workstation-local 1.7 GB source concurrently → N parallel source reads + N concurrent
  1.7 GB SMB uploads → workstation disk + uplink saturation; "sluggish, not frozen" is the per-host UI-thread
  progress continuations queueing on the single Dispatcher under that pressure. **Concurrency facts (corrects
  any "hardcoded 10-host cap" notion):** there is NO hardcoded 10 — `PatchOptions.MaxConcurrentHosts = 50`
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
  forcing full-width measure.
- **Scan-timeout edge** — 5-min cap (a997642) may be short for the very worst first-scan boxes; bump to
  10 min (600s) ONLY if real "Scan timed out" false-positives appear.

---

## PARKED — needs a signal/decision before it's worth building

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
