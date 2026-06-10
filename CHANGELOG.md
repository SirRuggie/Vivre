# Changelog

Notable changes to Vivre, newest first. Loosely follows
[Keep a Changelog](https://keepachangelog.com/) — work-in-progress sits under **Unreleased** until
it ships, then gets a dated heading.

## Unreleased

### Fixed
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
- **New left navigation** — the app now has a WPF-UI `NavigationView` pane (**Computers** · **Scripts** ·
  **Cross-Domain RDP** · **Settings**), collapsed to icons by default with a hamburger toggle (remembered
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
- **Micro-interaction polish** — the command bar is grouped into labelled **Fleet / Operations / Updates**
  clusters; tab ✕ buttons gain a hover circle and tabs a keyboard focus ring (and middle-click closes a
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
