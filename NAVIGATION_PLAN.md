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

## Decisions (locked 2026-06-09)

1. **No "Updates" nav section.** Machines ⇄ Windows Update stays a **per-tab toggle** on
   `WorkspaceViewModel.IsUpdateMode` — it is per-tab state, not a destination. The on-canvas chips remain.
2. **No "Reports" nav section.** Reporting today is just two identical CSV exports (`BuildReportCsv`) + a
   software-report CSV + the activity log — too thin to warrant a section. Exports stay where they are.
3. **"Scripts" is a library MANAGER only** — edit / add / remove scripts in an accessible view (reuses
   `ScriptLibrary` over `%APPDATA%\Vivre\Scripts`). **No run-from-here**; running stays the Computers
   right-click ▸ Run script against machines (`ScriptRunnerWindow`, unchanged).
4. **Mode toggle stays as on-canvas chips "for now"** — interim; the mode UX is to be revisited later.
5. **Toolbar = Option A** — keep the Fleet / Operations / Updates clusters (from the modernization's M1),
   refined; no Fluent overflow. One-click speed/glanceability matters for a sysadmin tool.
6. **Cross-Domain RDP → a nav item** (confirmed) — promote it from the View-menu tab to a machine-gated nav
   destination.

## Target nav structure

```
┌─ pane (LeftCompact) ─┐
│  ▣  Computers        │  → the workspace tabs live here; Machines⇄Updates = per-tab chips
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

### Phase 1 — NavigationView shell + Settings page  *(status: in progress)*
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

### Phase 2 — Cross-Domain RDP → nav item
- Promote RDP from the View-menu tab to a machine-gated nav destination (singleton preserved, MSTSC sessions
  kept alive via the same content-host pattern).

### Phase 3 — Scripts section (manager only)
- A standalone library manager: category list + AvalonEdit editor + add / save / delete over `ScriptLibrary`.
  No run / targets / output. The right-click ▸ Run-script path stays as-is.

### Phase 4 — Cleanup + toolbar (Option A)
- Refine the Fleet/Operations/Updates clusters per Option A; retire menu items superseded by the nav/Settings;
  final polish.

## Interim / deferred
- **Mode chips stay** (decision 4) — revisit the mode UX after the shell settles.
- Software-service-map and custom/hidden-columns editing may stay in their existing dialogs (linked from the
  Settings page) rather than being fully embedded, unless cheap to inline.

## Verification (every phase)
- `dotnet build source\Vivre.Desktop\Vivre.Desktop.csproj` → 0 errors / 0 new warnings.
- **Keep-alive proof:** load a machine list, navigate away and back, confirm the list + tab state persist
  (proves content wasn't rebuilt — the RDP-survival guarantee, testable without an APVHOP box).
- **Dual-theme screenshots** (light + dark) of the changed surfaces before the phase is marked done.
- No regressions to the data-grid horizontal scroll, the mode chips, the empty states, or the status bar.
