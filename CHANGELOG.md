# Changelog

Notable changes to Vivre, newest first. Loosely follows
[Keep a Changelog](https://keepachangelog.com/) — work-in-progress sits under **Unreleased** until
it ships, then gets a dated heading.

## Unreleased

### Added
- **Run operations on different machines at the same time** — scanning or installing on one set of
  machines no longer locks the whole tab: kick off Install on server A, then immediately Scan server B,
  from the toolbar or the per-machine panel. Rows already busy with an operation are skipped with a
  per-row "Skipped — busy (Install running)" note that stays until the next action touches them; the
  status bar narrates multiple operations ("2 operations · Install 3/12 · Scan 7/40"), the fleet band
  sums their progress, completion banners queue one at a time, and Stop cancels everything in the tab
  ("Stop all — N operations running"). Total remote load stays bounded by the same shared budget as
  before — concurrency never adds connections.
- **Check Vitals from the machine details window** — the Vitals tab has its own Check Vitals button that
  reads health + vitals for just that one machine (busy spinner while it runs; disabled while a fleet
  sweep already holds the machine). No more sweeping the whole tab to populate one box.
- **Installed updates are marked in the panel** — after a zero-failure install, that install's updates show
  greyed with an **"Installed — reboot pending"** chip (or just "Installed" when no reboot is needed), their
  checkboxes untick so Install checked can't re-target them, and the summary line adds "· N installed this
  session". A partial failure shows an honest banner ("Install completed with N failure(s) — rescan after
  reboot for exact state") instead of guessing per-row. The panel's "All" button skips marked updates too.
  Display-only; a fresh scan clears it.
- **Vitals on the Patching grid** — the same 0-100 vitality pill the Health grid has, so a sick box stands
  out while patching. Display-only (Patching already runs the vitals sweep); one shared template drives both
  grids so they can't drift apart.
- **"What does the Vitality score mean?"** — a new in-app help topic: the scoring rubric (start at 100; the
  disk / memory / CPU / reboot-pending / uptime penalties; the 80/50 band cut-offs), that the Unhealthy
  filter includes Offline boxes, that CPU/memory are point-in-time samples a re-check clears, and which
  signals are gathered but deliberately not scored.

### Fixed
- **The empty-state cards are truly centered** — the "Get started" card and the "No machines match
  this filter" state now sit dead-centre of the visible content area at every window width, in both
  Health and Patching. The long-standing top-left placement was a workaround for a "DataGrid width
  leaks into the layout" drift that runtime measurement disproved: a new env-gated layout probe
  (`VIVRE_LAYOUT_PROBE=1`, kept as a permanent diagnostic) showed centre-vs-viewport deltas of 0.0
  at four widths in both overlay states — and even at the old commit where the drift was originally
  "verified". That report was an artifact of the since-banned screenshot verification pipeline, not
  a real layout bug.
- **A dead host can no longer crash the app at fleet scale** — abandoning a connection attempt to an
  unreachable machine raced the PowerShell SDK's connection-retry, which then fired into torn-down
  transport state: a NullReferenceException on a raw background thread, which terminates the process
  (confirmed via the Windows Event Log at 318/319 of a 319-machine sweep). Two-part fix: WSMan
  connection retries are disabled (`MaxConnectionRetryCount = 0` — dead hosts now fail fast in ~20s,
  and the retry that raced disposal never exists), and abandoned connections/pipelines are no longer
  disposed while live — cleanup defers until the abandoned task settles. Covered by a regression test.
- **A hung host can no longer stall a sweep indefinitely** — the SCCM-health half of Check Vitals had
  no per-host timeout (now 60s), and the timeouts that existed only *requested* an abort while the call
  could wait minutes more for a zombie connection to acknowledge; timeouts now unblock the sweep
  immediately while teardown completes in the background. Worst case per dead host is ~3 minutes and
  the sweep always completes — no more sitting at 318/319.
- **Custom columns start filling immediately on big fleets** — the column fill shared one
  first-come-first-served budget with the vitals sweep and queued behind all of its rows (~2 minutes of
  blank columns on 319 machines). The budget now guarantees the column fill a small reserved slice
  (4 of the same 32 — the total cap is unchanged), so columns populate from the first seconds.
- **Patching no longer runs a phantom "Custom columns" pass** — every tab loaded the saved
  custom-column definitions, so Patching tabs ran real remote probes for columns their grid can't even
  display. The fill now runs only on Health tabs that actually have columns configured.
- **The amber sweep banner names the right actions per section** — Health reads "Ping & Check Vitals
  resume when finished" instead of borrowing Patching's "Scan & Install".
- **Install no longer defers on phantom "file-rename" reboots** — the update agent's pre-install servicing
  guard was the one remaining place counting `PendingFileRenameOperations`, so clicking Install flipped
  whole fleets to "Reboot pending (file-rename operations queued)" and quietly never started those installs.
  The guard now uses the five real servicing signals only (CBS in-progress / staged pending.xml / packages
  pending / CBS reboot / Windows Update reboot).
- **Download progress counts again** — the MB counter tracked a WUA byte counter that often sits at 0 for
  the whole download; it now derives from the per-update percent when that happens, and totals display as
  estimates ("3/~12 MB") since WUA revises them mid-download.
- **Deselecting a filter chip falls back to All** — unchecking the lit chip used to leave the old filter
  silently applied with no chip lit (an empty-looking grid when nothing matched).
- **Right cluster no longer clips at maximized** — the command-bar width was pinned from mistimed
  measurements (a window's resize event fires before its content re-arranges, and a one-shot startup
  correction never re-fired). The pin now follows the content area's own size change, which is always
  post-layout — correct on launch, maximize/restore, resize, and pane animations.
- **The bottom dock opens at a sane height** — it reopened at whatever height it last had that session with
  no cap, and a splitter drag pinned the machine grid to a fixed height so the dock could visually swallow
  it. It now opens clamped to 40% of the section, remembers your dragged height across sessions, and the
  grid row is re-asserted as flexible on every open/close.
- **The narrow toolbar stays icon-only when you select rows** — the selection swap's re-measure ignored the
  re-expand hysteresis and visibly expanded the collapsed bar; it now applies the same rules as a resize.
- **Scan all / Install all disable on an empty tab** — nothing to act on, so they no longer look clickable.
- **"Scan this machine" regained its Fluent styling** — its inline style lacked a `BasedOn` and wiped the
  button template.
- **The command bar no longer reads as a lighter band** — the workspace section now paints the same surface
  as the tab strip instead of letting the Mica backdrop show through.
- **Tab headers were dead to the mouse** — the ✕ close button, the right-click menu (close / close others /
  close all / rename), double-click rename, and middle-click close all did nothing in both Fleet strips: the
  tab header's keyboard-focus ring disabled hit-testing for everything inside it. Removed the offending
  attribute; all four interactions work again.
- **Reboot Pending column over-reported on every machine** — healthy boxes showed a pending reboot when
  none was due. The detection OR'd in `PendingFileRenameOperations`, which is populated by benign file
  operations (AV definition swaps, installer temp cleanup) and accumulates on long-uptime servers, so it
  fired almost everywhere. Reboot-pending detection now uses the reliable signals only — the CBS and
  Windows Update reboot keys plus SCCM's own `DetermineIfRebootPending` — so the column agrees with the
  ConfigMgr console. Fixed across all three probes (Check Vitals, SCCM health, and the
  monitor/force-reboot recheck).
- **Update-scan concurrency knob now actually works** — `MaxConcurrentScans` was defined but never
  wired to anything; the shared remote-read budget is now sized from it (default 32, unchanged in
  practice).
- **Scheduling an install no longer self-cancels on Stop** — hitting Stop right after a scheduled task
  was registered on a target used to silently unregister it; the schedule is now left in place and the
  outcome is reported.
- **Uninstalls show "Uninstalling"** in the status chip instead of the misleading "Installing".
- **CSV export is formula-injection safe** — values that start with `=` `+` `-` `@` (e.g. a software
  name read back from a target) are neutralised so a spreadsheet can't execute them on open.
- **Cross-Domain RDP tells you when a host has no saved credentials** — connecting now shows a dialog
  pointing at the right-click ▸ Edit… login fields, instead of silently doing nothing.

### Changed
- **Selection actions live in the command bar** — selecting rows transforms the bar in place (the
  Gmail / Explorer pattern): an accent **"N selected"** chip plus **Scan (N)** / **Install (N)** /
  **Clear selection** appear exactly where Scan all / Install all sat (those yield while a selection
  exists); Health mode shows the chip + Clear. The old floating selection bar — and the layout jump it
  caused — is gone entirely, so nothing moves or covers the grid when you select, and a double-click always
  lands on the row you clicked. The compact icon-only mode keeps the count visible; the status bar's
  "N selected" stays as ambient feedback.
- **Double-click opens Details** — double-clicking a machine row (Health and Patching) opens that machine's
  Details window; running scripts is deliberate and stays in the right-click menu only.
- **Saved lists carry the tab name** — Save tab as list… prefills the tab's current title, and opening a
  list names the tab after it. List files on disk are unchanged.
- **Settings reorganised for clarity** — "Integrations" (WhatsUp Gold server + Package library folder, each
  with plain-language helper text and a Browse… picker for the folder), "Help & about" (Info icon, inline
  version line), and a flat **Grid columns ▸ Manage columns…** row alongside the other top-level settings;
  the in-app guide follows the new paths.
- **Exclude updates is a proper dialog** — wrapped helper text, a live "currently excluded" list as you
  type, and leave-blank-to-clear (stated in the dialog).
- **Clicking empty grid space clears the selection** — Explorer-style; clicks on rows, headers, scrollbars,
  or buttons are unaffected.
- **A running vitals check announces itself** — an amber banner above the grid shows the live progress
  ("Checking vitals — 12/48 · 00:32 — Scan & Install resume when finished") for both the auto-check on
  load and manual Check Vitals, auto-dismissing when done; the grid stays fully usable underneath. The
  disabled Scan/Install buttons carry the same live narration as a themed tooltip on hover, so the held
  state is legible at a glance and in detail.
- **The shell now adapts to window width** — below ~1200 px the nav pane auto-collapses to the compact icon
  rail (Health / Patching stay one click away); widen again and it restores your last open/closed choice.
  The toolbar measures whether its labelled buttons genuinely fit and only then drops them to centred
  icon-only buttons (tighter-spaced, with full-label tooltips); the title bar slimmed to a 36 px band.
- **Frameless command bar** — toolbar buttons sit directly on the surface (icon + label, a subtle highlight
  on hover only), Task-Manager style; the Monitor toggle shows accent text on a faint fill when on instead
  of a solid box. The **…** overflow is gone: **Clear results** is a regular toolbar button and **Export to
  CSV** lives in the grid right-click ▸ Export.
- **Fleet ▸ Health and Fleet ▸ Patching replace the single Computers workspace** — the left nav now has
  a collapsible **Fleet** parent with two independent keep-alive destinations: **Health** (ping, vitals,
  SCCM actions — the former "Machines" mode) and **Patching** (Windows Update scanning and install — the
  former "Windows Update" mode). Each section has its own tab strip; switching between them Visibility-toggles
  the inactive strip without destroying it (the `TabControlEx` keep-alive is preserved). Ctrl+M now toggles
  between Health and Patching; the nav highlight follows. Health is the default on launch.
- **Mode chips removed** — the per-tab "Machines / Windows Update" radio chips are gone; mode is fixed by
  the Fleet section a tab belongs to (Health tabs are always in health mode, Patching tabs always in
  patching mode). The Get-Started card's "Switch modes" row now points at the Health / Patching nav items.
- **Menu bar removed** — File / View / Updates are gone; their items moved to where they're used: the tab
  right-click menu (New tab, Clear this tab, Rename, Close…), a **Lists ▾** toolbar button (open / save /
  delete named machine lists), the grid right-click ▸ Export (Export to CSV), an **Update options ▾** button shown in
  Patching (update source / drivers / exclusions), and an **Activity-log** toggle on the status bar. The
  title bar is now just the app title and the window controls.
- **Filter chips all carry icons** — the All / Updates / Done chips gained icons to match Reboot pending /
  Errors / Offline / Unhealthy, and **Remote credentials** on the Settings page is now a collapsible card
  like the other settings groups.
- **New left navigation** — the app now has a WPF-UI `NavigationView` pane (**Fleet** ▸ **Health** / **Patching** ·
  **Scripts** · **Cross-Domain RDP** · **Settings**), collapsed to icons by default with a hamburger toggle (remembered
  across launches). Theme
  (Light / Dark / System, a Windows-11-style "App theme" dropdown), session credentials, auto-check-on-load,
  WUG server / packages folder, and Help / About moved into a dedicated **Settings page**; the Settings and
  Help menus were retired from the menu bar (File and View stay). Switching sections keeps the workspace —
  and any live Cross-Domain RDP session — alive. The bottom status bar is now a full-width strip.
- **Cross-Domain RDP and Scripts are nav sections** — Cross-Domain RDP moved from the View menu into a left-nav
  destination (its live sessions stay kept-alive across nav switches), and a new **Scripts** section is a
  standalone library manager: browse the categorised PowerShell library, edit in a syntax-highlighted editor,
  and add / save / delete scripts. (Running scripts against machines is unchanged — still the grid's
  right-click ▸ Run Script.)
- **Computers workspace polish** — the command bar is now a single clean row; selecting machines raises a **contextual command bar** (Scan / Install scoped to the
  selection) rather than mutating toolbar labels; the workspace tabs gain modern browser-style headers with a
  right-click **Close other / Close all** menu; operation progress is reported in **one** place (the bottom
  status bar — a slim strip plus the live narration and counts) instead of three; and the **Settings** page
  groups set-once options into collapsible sections. The redundant View-menu Machines / Windows Update items
  were retired (the on-canvas chips and Ctrl+M remain).
- **Micro-interaction polish** — tab ✕ buttons gain a hover circle and tabs a keyboard focus ring (and middle-click closes a
  tab); right-click menus throughout pick up Fluent icons; scrollbars everywhere become thin Fluent overlay
  bars; the completion banner fades in, the fleet progress bar eases smoothly to each value, and the
  activity panel fades open; the Run Script category headers animate their chevron; and tooltips now name
  their keyboard shortcuts. Verified in light and dark.
- **Refreshed grid + dialog styling** — the machine and update grids now have structured, theme-aware
  column headers (a fill band, a separator under the headers, and sort arrows) with taller 36px rows and
  horizontal row dividers; the filter chips are taller with semantic icons; dialogs share consistent
  section headers, padding, and a three-level type ramp; and all three tab strips use one Fluent style.
  Works in both light and dark mode.
- **Status chips now adapt to the theme** — the machine status pills, the Vitals chips, the status dots,
  and the activity-log severity colours were fixed RGB (identical in both themes, with weak contrast in
  light mode); they now use theme-aware Fluent colours that stay legible in light and dark, and the
  actionable "Updates available" state stands out in the app accent. The activity log and the per-machine
  detail grids also pick up the Fluent control styling.
- **Sweeps narrate their progress** — instead of a silent spinner, a running sweep now shows the operation,
  count, and elapsed ("Checking vitals — 12/48 · 00:32") beside the progress ring and in the status bar.
  During an update run the fleet band adds elapsed + an N/M counter and holds open briefly after finishing
  so it no longer races the completion banner. The completion banner's colour now reflects the real outcome
  (green all-succeeded, amber partial, red all-failed) rather than guessing from the message text, and
  failing rows get a red error icon moved into view. The bottom status bar is reorganised into
  left / centre / right zones (context · active operation · summary).
- **New-tab "+" sits next to the last tab** — browser-style, instead of pinned to the window's far-right
  edge. With many tabs it scrolls with the strip (scroll right to reach it).
- **Multi-tab sweeps stay responsive and never freeze** — Check Vitals, update scans, software and
  custom-column reads across *all* open tabs now share one app-wide concurrency budget (≈32 hosts at a
  time) instead of each tab flooding WinRM on its own, so tabs fill in together (in waves) rather than one
  tab finishing entirely before the next starts. Each row's combined health+vitals pass also holds a
  single slot end-to-end — fixing a stall where a second tab would show "health unavailable" and then sit
  idle until the first tab's vitals had *all* completed. Activity-log and completion-toast updates are
  marshalled to the UI without blocking, so a heavy multi-tab sweep no longer stutters or freezes the
  window.

### Added
- **Empty-state guidance + mode chips** — a fresh tab now shows a "Get started" onboarding card (paste
  names → ping / check vitals → switch modes, with an Open-help link) instead of a blank grid; the
  activity log shows "No activity yet" until operations run; the Cross-Domain RDP pane prompts you to add
  or connect to a host; and filtering to no matches shows "No machines match this filter" with a Clear
  button. New **Machines / Windows Update mode chips** in the filter bar give the current view an on-canvas
  switch (the View menu and Ctrl+M still work). Verified in light and dark.
- **Grid right-click menu, regrouped** — per-machine actions are now clustered (Run script ▸ · Client
  actions ▸ · Software ▸ · Export ▸ · Schedule ▸) for easier scanning, and **Export ▸ Shown rows + columns
  (CSV)…** puts the full grid export — filter-aware and including any custom columns — on the right-click,
  not just the software report. Same export as File ▸ Export to CSV.
- **Cross-Domain RDP — embedded remote-desktop manager** — a tab (opened from View ▸ Cross-Domain RDP,
  beside your machine tabs) with a foldered tree of hosts and live, tabbed RDP sessions, full-screen, and
  saved per-host/per-folder credentials (Windows DPAPI, per user). Built on the Microsoft RDP ActiveX
  control, so it reaches hosts on other domains — turn NLA off per host if a login is rejected.
  Right-click or drag in the tree to organize; sessions stay connected when you switch tabs.
- **Windows Update lane** — scan, install, and uninstall updates per machine with live progress,
  driven by a compiled SYSTEM agent. Update Source toggle (Windows Update / Microsoft Update /
  Managed-WSUS) + an exclude-by-name list. (See `UPDATE_PLAN.md`.)
- **Grid filter + state chips** — a filter bar on both views: search machines by name and one-click
  quick filters (Updates available · Reboot pending · Errors · Offline · Done), with a live
  "Showing N of M" count. Filters the whole tab and updates mid-sweep (a row that errors appears
  under the Errors chip automatically).
- **Select shown** — one click selects every row the filter currently shows, so you can act on just
  that subset (e.g. filter to Errors → Select shown → Install) without hand-picking rows.
- **Export to CSV** (File ▸ Export to CSV…) — writes the rows currently shown (respecting the filter)
  to a CSV report (machine · online · state · updates · reboot · error · OS · schedule) for a
  maintenance-window write-up / ticket.
- **Pre-install reboot-pending check** — Install first checks the targets: if any already have a
  reboot pending (which can jam WinRM and fail the install), it offers to **reboot those first**,
  **install anyway**, or **cancel** — heading off the WinRM-unhealthy failure instead of reacting to it.
- **Browser-style tab menu** — right-click a tab for **Close other tabs** and **Close tabs to the
  right** (alongside Rename / Close tab); a single confirm covers any tabs that still have work.
- **WhatsUp Gold maintenance mode** — right-click ▸ *WhatsUp Gold maintenance…* puts the selected
  machines (or all in the tab) into/out of WUG maintenance via the `WhatsUpGoldPS` module: pick
  Enter/Exit, enter the WUG server + login, and it maps the names to WUG devices and sets it. The
  WUG credential is prompted each time and never stored (only the server is remembered); it runs
  locally against the WUG server (not on the targets), auto-installs the module for the current user
  if it's missing, and surfaces a clear reason at every step if something fails.
- **How to use Vivre** guide (Help ▸ How to use Vivre, or F1) — a searchable, collapsible in-app
  manual covering both Machines and Windows Update modes, grouped into Getting started / Machines /
  Windows Update / Tips & shortcuts / Troubleshooting; the search filters and auto-expands matches.
- **Schedule ▸ menu** (right-click) — schedule a one-time **install** or **reboot** at a chosen
  time, or **Cancel** a pending scheduled task. Works in either view; the "Scheduled task" columns
  show what's queued.
- **Reboot (force now)** — right-click action on the selected machines, with confirmation.
- **Per-machine detail window** (right-click → Details…) — OS (caption + build), full update state,
  and that machine's activity-log messages; **Show messages** filters the activity log to one machine.
- **Keyboard accelerators** — Ctrl+T/W/L, F2, F5, Ctrl+M, Ctrl+Enter, and Shift+F10 for the
  right-click menu. **Theme** (Light/Dark/System) is now persisted across launches.

### Changed
- Machines ↔ Windows Update mode is selected from the **View** menu (direct items).
- Status is shown by colour **and** shape (glyphs), never colour alone; the activity log gained a
  severity glyph.
- Run Script now opens the grouped Run Script window instead of a deep cascading menu.
- Confirmations added for the irreversible/production actions (fleet install, uninstall, reboot,
  large delete, closing a tab with work, replacing a loaded list); routine actions stay one-click.
- Toolbar reordered so the machine buttons don't shift when switching modes; the tab strip scrolls
  when there are more tabs than fit.

### Fixed
- **From the code review (`REVIEW_FINDINGS.md`):**
  - Uninstall could remove the **wrong** update: the DISM fallback matched a KB against installed
    packages by bare substring (`KB5000` matched `KB5000802`). It now matches the KB as a whole token.
  - DISM package enumeration could **deadlock** the SYSTEM worker if DISM wrote a lot to stderr; both
    DISM calls now drain stderr concurrently.
  - A failure writing the activity-log file could **throw out of a caller's catch block** and bury the
    original error; the file write is now isolated (the in-memory entry is the source of truth).
  - The update agent could exit with **no error line** if a transient file lock hit its progress write;
    the write now retries briefly and never throws.
  - The streaming PowerShell output collection is now disposed (was leaking a wait handle per remote call).
  - The update agent now fails with a clear "config was empty or malformed" message instead of a bare
    NullReferenceException when handed an unreadable config.
  - Regression coverage added for the load-bearing WUA paths: the install/uninstall streaming
    controller (heartbeat filtering, watchdog, typed-exception handling, user-cancel), per-host
    serialization release on fault, the cross-framework agent-config JSON contract, and DISM
    exit-code translation. No behaviour change — these lock the existing behaviour in.
  - The monitor's reboot probe no longer swallows a persistent failure silently — a lost session or
    sustained error now backs off and is logged once (matching the WinRM-unhealthy path), instead of
    leaving the row dark with no trace.
  - Removing machines (Remove Offline / Delete) now prunes their per-host monitor state, so stale
    name-keyed entries don't linger or affect a later re-add.
  - The fatal-error handler writes straight to the log file instead of through the shutting-down UI
    dispatcher, so the one line naming a fatal crash isn't lost during teardown.
- The Windows Update agent is now **hash-verified on the target** (SHA-256) before it runs as SYSTEM,
  so a tampered/replaced binary in the shared temp dir is caught instead of executed.
- Monitor/reboot-probe updates now consistently marshal to the UI thread, and the local-vs-remote
  host check is centralised in one helper (was copy-pasted across five files).
- Remoting failures no longer leak raw SDK strings — they're translated to clear, host-named
  messages ("Lost connection to …", "WinRM unhealthy — reboot the target", "No response from …").
- The monitor no longer hammers reboot-pending / degraded hosts (the cause of WinRM/PSRP poisoning);
  a degraded host self-heals once WinRM responds again (re-tested every few minutes).
- Hung or dead install sessions are caught via heartbeat silence (~90s) instead of freezing on stale
  progress — and a genuinely slow update is never falsely flagged.
- Uninstall surfaces the real per-KB reason (e.g. `0x800F0825` permanent package) and reports an
  all-failed run as an error rather than a green "Done"; cumulative updates are correctly reported
  as non-removable.
- Copy ▸ \<field\> copies the right-clicked row, not a stale multi-selection.
- The Update grid's "Reboot message" and "Windows update message" cells no longer butt together
  (they had no gutter, so trimmed text read as one run-on sentence) — added a right-hand gutter.
- The scan-complete summary counts machines that actually have updates, not every scanned machine.
- Settings no longer silently clears a stored credential when the username is left blank.
