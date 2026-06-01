# Vivre

A Windows desktop tool for managing Microsoft Configuration Manager (SCCM / MEMCM) clients at
scale. Load a list of computers into a tabbed grid, ping them, pull SCCM client health, and run
client actions or arbitrary PowerShell against the ones you select.

The name is from the One Piece *Vivre Card* — a slip that tracks a person's life force. Every row
in the grid is a Vivre Card for one machine: online or dark, here or there, healthy or wounded.
(See **Help ▸ About Vivre** in the app for the full story.)

Built with **.NET 10 / WPF** ([WPF-UI](https://github.com/lepoco/wpfui) Fluent styling).

## Features

- **Tabbed workspaces** — each tab is an independent set of machines and operations, run side by side.
- **Ping All / Check All** — reachability plus SCCM client health (site code, agent version,
  reboot-pending / missing-updates / install-running / users-online, last reboot), shown as
  colour-coded status dots.
- **Right-click actions** — SCCM client triggers (machine policy, hardware inventory, update scan,
  …), Run PowerShell (one machine, the selection, or all), and Enable WinRM (over DCOM).
- **Run Script** — pick a saved script or paste one; per-machine output lands on each row.
- **Windows Update lane** — scan, install, uninstall, and schedule updates per machine with live
  progress (a built-in BatchPatch-style patcher). See `UPDATE_PLAN.md`.
- **Filter & report** — filter the grid by name or state (Updates / Reboot pending / Errors /
  Offline / Done), **Select shown** to act on just that subset, and **Export to CSV** for a
  maintenance-window write-up.
- **Named machine lists** and a **searchable activity log** (in-app panel + rolling file).
- Session credentials (current login by default, or supply your own in Settings).

## Roadmap

- **Reboot-and-wait** after install (auto-reboot + watch for the box to return) — the on-target
  agent already reboots; only the UI toggle + "waiting…" status is missing.
- (Optional) an **SCCM-deployment update lane**, if updates ever get deployed through SCCM here.
- Accessibility polish deferred as low-value for a single sighted user: full screen-reader naming
  on the grids + automation IDs; light-theme tuning of the script editor's highlight colours.
- **Refactor (someday):** the per-tab `WorkspaceViewModel` is large — it could be split into a
  `HostMonitor` + `PatchController` for readability. Purely cosmetic and lowest priority; do it on its
  own (it touches the load-bearing monitor/reboot logic, now backed by tests). A full multi-agent code
  review (2026-06) addressed everything else it found.

## Build & run

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```
dotnet build  source\Vivre.slnx
dotnet test   source\Vivre.slnx
dotnet run --project source\Vivre.Desktop
```

## Publish / deploy to another machine

Use `publish.ps1` (wraps `dotnet publish` of the Desktop project):

```
.\publish.ps1            # self-contained win-x64 -> publish\Vivre-win-x64\ (no .NET needed on target)
.\publish.ps1 -Zip       # same, plus publish\Vivre-win-x64.zip to copy over
.\publish.ps1 -FrameworkDependent   # small build; target needs the .NET 10 Desktop Runtime (x64)
.\publish.ps1 -Runtime win-arm64
```

Copy the output folder (or the zip) to the target and run `Vivre.exe`. The curated `Scripts\`
library ships in the output and seeds `%APPDATA%\Vivre\Scripts` on first run. (`publish\` is
git-ignored.)

## Repo layout

- `source/` — the solution (`Vivre.slnx`): `Vivre.Core`, `Vivre.Desktop`, `Vivre.Core.Tests`.
- `tools/RemoteRun` — small console to test remote PowerShell against a host.
- `scripts/` — the curated PowerShell script library (PS7 / `Get-CimInstance`), organised into
  category folders. Shipped with the app and seeded into `%APPDATA%\Vivre\Scripts` on first
  run; opened from the grid's right-click **Run script…**. See `scripts/README.md`.
- `UPDATE_PLAN.md` — the Windows Update lane: how patching works and its reliability constraints.

See [CHANGELOG.md](CHANGELOG.md) for version history.
