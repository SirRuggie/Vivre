# NAVIGATION_PLAN.md

The plan to move Vivre's shell from the top **workspace tab strip** to a WPF-UI **`ui:NavigationView`**.
Phased and batch-gated — one Sonnet worker per phase, **dual-theme screenshots required before any phase is
marked done**, the PM reviews each phase, and we stop for approval between phases. Keep this file current as
phases land (mark each phase ✅ when committed).

Companion docs: **[CLAUDE.md](CLAUDE.md)** (architecture/conventions), **[UPDATE_PLAN.md](UPDATE_PLAN.md)**
(the WUA lane). Read those before touching update/remoting code.

## Goal

A left `ui:NavigationView` replaces the top tab strip as the app's primary navigation:
- `PaneDisplayMode="LeftCompact"` (icons-only by default), **collapsed at startup** with a hamburger toggle.
- Expand/collapse **persisted to settings**.
- The menu bar **stays** for File/workspace operations (New tab, Open/Save/Delete list, Paste, Export CSV, Exit).

## Decisions (locked 2026-06-09; updated 2026-06-10)

1. **Fleet ▸ Health + Fleet ▸ Patching replace the single Computers workspace** *(updated 2026-06-10)* —
   The former single "Computers" workspace is now a collapsible **Fleet** parent with two independent
   keep-alive destinations: **Health** (health / SCCM actions — formerly "Machines mode") and **Patching**
   (Windows Update — formerly "Windows Update mode"). Mode is fixed by section, not a per-tab toggle.
   The on-canvas mode chips are removed. Ctrl+M now toggles between Fleet sections (Health↔Patching).
2. **No "Reports" nav section.** Reporting today is just two identical CSV exports (`BuildReportCsv`) + a
   software-report CSV + the activity log — too thin to warrant a section. Exports stay where they are.
3. **"Scripts" is a library MANAGER only** — edit / add / remove scripts in an accessible view (reuses
   `ScriptLibrary` over `%APPDATA%\Vivre\Scripts`). **No run-from-here**; running stays the Computers
   right-click ▸ Run script against machines (`ScriptRunnerWindow`, unchanged).
4. ~~**Mode toggle stays as on-canvas chips**~~ — **removed** (see decision 1). Chips replaced by Fleet nav.
5. **Toolbar = Option A** — keep the Fleet / Operations / Updates clusters (from the modernization's M1),
   refined; no Fluent overflow. One-click speed/glanceability matters for a sysadmin tool.
6. **Cross-Domain RDP → a nav item** (confirmed) — promote it from the View-menu tab to a machine-gated nav
   destination.

## Target nav structure

```
┌─ pane (LeftCompact) ─┐
│ ▾ Fleet              │  → collapsible parent; NOT a destination itself
│     Health           │  → health check / SCCM actions (formerly "Machines mode"); default on launch
│     Patching         │  → Windows Update scan + install (formerly "Windows Update mode")
│  ⟨⟩ Scripts          │  → script library manager (edit/add/remove; no run)
│  🔌 Cross-Domain RDP │  → machine-gated (APVHOP); singleton
│  ───────────────────  │
│  ⚙  Settings  (footer)│  → theme toggle (Task-Manager style), credentials, auto-check,
└──────────────────────┘     WUG server, packages folder, software-map, columns, Help, About
```

## Load-bearing constraints — DO NOT BREAK

1. **`TabControlEx` keep-alive is mandatory.** Tab content is kept in the visual tree (`PART_ItemsHolder` +
   one `ContentPresenter` per tab, `Visibility`-toggled) specifically so the Cross-Domain RDP tab's embedded
   **MSTSC ActiveX control + live RDP connections survive tab switches**. `NavigationView`'s default
   `Frame`-based navigation **destroys pages on navigate** and would kill those sessions.
   **→ Use `NavigationView` for the PANE only; host section content in a custom keep-alive container**
   (both the Computers workspace and the Settings/Scripts pages stay alive, `Visibility`-toggled by the
   selected nav item — never rebuilt). Switching Computers→Settings→Computers must NOT rebuild the workspace.
2. **Mode is per-tab state** on `WorkspaceViewModel.IsUpdateMode` — three controls converge on it (View menu,
   Ctrl+M, chips). Do not turn mode into a nav destination.
3. **The bottom dock is global** (window-level Activity + per-machine Updates panel), not per-tab — keep it a
   window-level splitter panel below the nav content.
4. **Cross-Domain RDP is `APVHOP`-gated** (`ShellViewModel.CrossDomainRdpMachine`) and a **singleton** — the
   nav item's visibility binds to `IsCrossDomainRdpAvailable`; selecting it must not create a second instance.

## Phases

### Phase 1 — NavigationView shell + Settings page  ✅ *(done — 1.8.0)*
- Restructure `MainWindow` to host a `ui:NavigationView` (pane: **Computers**; footer: **Settings**).
  LeftCompact, collapsed at startup, expand/collapse persisted (new `AppSettings` flag, e.g. `NavPaneOpen`).
- **Computers** content = the existing workspace **moved intact** (command bar + keep-alive `TabControlEx` +
  bottom dock + status bar). Content host is keep-alive (see constraint 1) — verify a loaded machine
  list + tab state survives a Settings round-trip.
- **Settings page** consolidates: theme toggle Light/Dark/System (Task-Manager-style, persisted), the
  session credential (current `SettingsWindow` content), auto-check-on-load, WUG server, packages folder, and
  links to the existing Columns / software-map / Help / About surfaces (embed where cheap, link where not).
- **Cleanup:** remove the Settings menu and Help menu from the menu bar (their items live in the Settings
  page now); keep File + View menus. **Fix the theme-startup flash** (App.xaml hardcodes `Theme="Dark"`, so a
  saved Light/System briefly flashes dark) if it can be done cleanly.
- Highest-risk phase — the keep-alive under NavigationView is the load-bearing bit.

### Phase 2 — Cross-Domain RDP → nav item  ✅ *(done — 1.8.1; `RequireRdpHost=true` since 1.9.0)*
- Promote RDP from the View-menu tab to a machine-gated nav destination (singleton preserved, MSTSC sessions
  kept alive via the same content-host pattern).

### Phase 3 — Scripts section (manager only)  ✅ *(done — 1.8.1)*
- A standalone library manager: category list + AvalonEdit editor + add / save / delete over `ScriptLibrary`.
  No run / targets / output. The right-click ▸ Run-script path stays as-is.

### Phase 4 — Cleanup + toolbar + UI polish  ✅ *(done — 1.8.2)*
- Refine the Fleet/Operations/Updates clusters per Option A; retire menu items superseded by the nav/Settings;
  final polish.

### Post-phase polish rounds
- **Round 2  ✅ *(done — 1.9.0)*** — Fleet ▸ **Health** / **Patching** nav split (two independent keep-alive
  tab strips; mode chips removed; Ctrl+M toggles sections); menu bar removed; filter-chip icons; credentials
  CardExpander.
- **Round 3  ✅ *(done — 1.9.1)*** — responsive shell: `AdaptiveLayoutController` collapses the nav pane to
  the compact icon rail below ~1200 px (with hysteresis; the user's open/close intent is persisted) and drops
  toolbar labels to centred icon-only buttons only when the measured labelled cluster no longer fits; title
  bar slimmed to 36 px; the … overflow removed; frameless Task-Manager-style command bar; tab-header
  hit-test regression fixed. **LeftMinimal is shelved** — WPF-UI's minimal pane does not render hierarchical
  (Fleet ▸ child) items, so `MinWidth=800` keeps the app out of that state.

- **Round 4  ✅ *(done — 1.9.2)*** — live-usage hardening: selection actions moved INTO the command bar
  (in-place transform; the floating selection bar and its layout shift are gone); double-click opens
  Details; agent reboot-guard / download-progress fixes; bottom-dock height clamp + persistence; Settings
  clarity pass (Integrations / Help & about / Grid columns); vitals pill on Patching; installed-this-session
  marking in the update panel.

- **Round 5  ✅ *(done — 1.10.0)*** — row-disjoint concurrency: a per-row operation registry lets
  operations on disjoint machines run simultaneously within a tab (busy rows are skipped with a
  persistent per-row message); composed narration, summed fleet band, queued completion banners,
  Stop-cancels-all. Remoting hardened at fleet scale: the WSMan connection-retry crash fixed at the
  source (`MaxConnectionRetryCount = 0`) plus deferred abandon-path disposal; real per-host timeouts
  (health 60s / vitals 120s, awaits unblock at the timeout); a reserved slice of the shared read budget
  (4 of 32) keeps passive custom-column fills from starving behind sweeps (cross-tab ordering remains
  FIFO by design — the shared budget caps total remote load). Per-machine Check Vitals added to the
  details window.

## Interim / deferred
- **Mode chips** — superseded: removed in Round 2 (1.9.0); a tab's mode is now fixed by its Fleet section
  (the Health / Patching nav destinations).
- Software-service-map and custom/hidden-columns editing may stay in their existing dialogs (linked from the
  Settings page) rather than being fully embedded, unless cheap to inline.

## Verification (every phase)
- `dotnet build source\Vivre.Desktop\Vivre.Desktop.csproj` → 0 errors / 0 new warnings.
- **Keep-alive proof:** load a machine list, navigate away and back, confirm the list + tab state persist
  (proves content wasn't rebuilt — the RDP-survival guarantee, testable without an APVHOP box).
- **Dual-theme screenshots** (light + dark) of the changed surfaces before the phase is marked done.
- No regressions to the data-grid horizontal scroll, the mode chips, the empty states, or the status bar.
