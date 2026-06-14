# Vivre — running backlog (deferred items & open threads)

> Working tracker for things found during build work that are NOT yet done.
> As items get fixed, move them to DONE with the commit hash. Add new finds under the right tier.
> **Order below is the recommended do-next order** (Ruggie can override — it's a recommendation,
> not a mandate). Last refreshed: OneDrive relocation DONE (repo now `C:\src\Vivre`); WUG saga +
> dialog audit closed; NavigationView refactor (incl. Phase 4) corrected to DONE.

---

## ▶ DO NEXT — recommended order

### 1. Smart reboot-and-verify flow (building blocks proven, wording ready)
The most-ready high-value feature win — generalize the 2016 lane's CU-commit verify to ALL reboots,
fleet-wide:
- On reboot, watch the box go down, then wait until it's *genuinely fully up* — not just pingable
  (ping answers mid-boot). Use a real "ready" signal (services started / WinRM-or-agent responsive).
- When truly up, auto-rescan and report a plain outcome using the already-written
  `RebootOutcomeMessages.cs` strings ("Back online · installed X · up to date", etc. — defined, not
  yet wired).
- Building blocks confirmed live: AZRADMANPLUS install-over-agent (KB2267602 via SMB → "Up to date").

### 2. Smart scan flow (design settled)
- Scan gates Stage (Stage unavailable until a scan has run on the box).
- Scan auto-populates KB / target UBR (read from scan result) — no manual entry.
- Pre-stage checks: already-current (UBR == target → "Already current", skip) and already-staged
  (RebootPending + StagedThisSession → "Already staged", skip).
- Guided "go get the package" prompt keeps the KB from the scan. Persisted across sessions.
- Pairs naturally with **Settings simplification** below (scan provides KB/UBR, so those Settings
  fields can go).

---

## OPEN — patching features (design mostly settled, build pending)

### "What's still needed" WUA indicator (replaces the rejected fused Full-Patch button)
- Red team killed the fused WUA+Stage button (CBS won't allow it; would need an unauthorized reboot
  between the two ops). Instead: after a box's CU is committed (Verify green), show a WUA scan count so
  the operator knows to run a WUA pass next — not a button pretending two reboot-separated ops are one.

### 2016 DISM routing toggle (red-team prompt drafted, not yet run)
- Small toggle in the 2016 action bar, visible when 2016 boxes present.
- OFF (default) = all 2016 boxes → DISM lane. ON = decide per box (DISM / WUA / skip), for excluding
  India-team boxes etc. Run the red-team pass before building.

### Settings simplification
- Remove KB / Target UBR / Size fields from Settings (scan provides them); keep only the package folder.
  (Do alongside or after Smart scan flow, which is what makes those fields redundant.)

---

## OPEN — polish / smaller standalone items

- **ARC-8: Last-status should mirror the vitals badge on WinRM-broken boxes.** Today a box where DCOM
  worked but WinRM is down can show a vitals badge (e.g. 88) while Last status reads "WinRM n/a".
  Make Last status mirror the badge as "Vitality 88 (Warning)" and keep the WinRM note in Last error.
  (The healthy version of this already renders — see the "Vitality 100 (Healthy)" rows.) Parked; small.
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

---

## DONE (committed) — recent

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
`2016-LCU-red-team-review.md`, `2016-LCU-panel-spec.md`. (`OVERNIGHT_KERBEROS_STATUS.md` and
`first-run-beta-checklist.md` were removed as stale/spent.)
- `key-file-path-map.md` — **refreshed this pass** (OneDrive trap → resolved/relocated; nav → done;
  the two 5.1 shell-out gotchas incl. the BOM bug + validation-trap meta-lesson; 344 tests).
  TODO: capture the as-built NavigationView shell layout next time MainWindow
  is touched.
- `vivre-backlog.md` — **this file, refreshed this pass.**
- `2016-LCU-panel-spec.md` — minor: shows "313 tests" as the historical as-built count (fine as a
  point-in-time record); could note cab-extraction + SSU/LCU ordering + StagedThisSession when next edited.
- `2016-LCU-lane-spec.md` / `2016-LCU-red-team-review.md` — substantively accurate; cycle-specific
  KB/UBR (KB5094122 / 9234) is "this cycle" by design.
- Consider a `vivre-ui-review-gate.md` (static XAML review checklist: never bind Run.Text, no new
  TwoWay binds, display binds OneWay, build-green ≠ render-green, visual-check before commit) — the
  rule that would have caught the a0cb80a broken panel. Fold into CLAUDE.md or stand alone.
