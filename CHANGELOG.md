# Changelog

Notable changes to Vivre, newest first. Loosely follows
[Keep a Changelog](https://keepachangelog.com/) — work-in-progress sits under **Unreleased** until
it ships, then gets a dated heading.

## Unreleased

### Added
- **Windows Update lane** — scan, install, and uninstall updates per machine with live progress,
  driven by a compiled SYSTEM agent. Update Source toggle (Windows Update / Microsoft Update /
  Managed-WSUS) + an exclude-by-name list. (See `UPDATE_PLAN.md`.)
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
- The scan-complete summary counts machines that actually have updates, not every scanned machine.
- Settings no longer silently clears a stored credential when the username is left blank.
