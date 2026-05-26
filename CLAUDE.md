# CLAUDE.md

This file guides Claude Code (claude.ai/code) when working in this repository.

## What this is

**Vivre** — a Windows desktop tool for managing
Microsoft Configuration Manager (SCCM/MEMCM) clients at scale: a tabbed grid of computers you ping,
health-check, and run SCCM client actions / arbitrary PowerShell against. Named for the One Piece
*Vivre Card* — every grid row is a card tracking one machine's life force (see **Help ▸ About Vivre**).

It is a **.NET 10 / WPF** app — the modern rewrite of an older .NET Framework 4.8 WinForms
app. **The legacy app was removed at cutover** (recover from git history if ever needed). Threat
model: single user running against their own SCCM environment — favour usability/maintainability
over enterprise hardening.

## Start here

**Read [REBUILD_PLAN.md](REBUILD_PLAN.md).** It's the design record + build history + the
remaining backlog. **§0 "Current status" at the top is the resume point** — read it first and
update it (status + NEXT + date) at the end of each working session. Keep the doc current when a
decision changes or a feature lands.

## Layout

- `source/Vivre.slnx` — the solution (`.slnx` format, the .NET 10 default).
  - **`Vivre.Core`** (net10.0) — non-UI logic: `Models`, `Net` (ping), `PowerShell`
    (`PSRunspaceHost`), `Sccm` (`ConfigMgrClient`, client actions), `Remoting` (`WinRmEnabler`,
    DCOM), `Credentials`, `Computers` (named-list store), `Scripts` (script library), `Logging`.
  - **`Vivre.Desktop`** (net10.0-windows) — the WPF app, ships as **`Vivre.exe`**: WPF-UI Fluent
    shell, `ShellViewModel` (tabs) + `WorkspaceViewModel` (per tab), `WorkspaceView`, dialogs.
    Composition root in `App.xaml.cs` (manual DI — services built once and injected). The output
    assembly is `Vivre` but the namespaces stay `Vivre.Desktop`.
  - **`Vivre.Core.Tests`** (net10.0, xUnit).
- `tools/RemoteRun` — dev console to exercise remote PowerShell (WinRM) against a host.
- `scripts/` — the curated PowerShell script library (PS7 / `Get-CimInstance`), organised into
  category folders; shipped with the app and seeded into `%APPDATA%\Vivre\Scripts` on first run,
  surfaced via the grid's cascading **Run script ▸** right-click menu.

## Building / running

```
dotnet build  source\Vivre.slnx
dotnet test   source\Vivre.slnx
dotnet run --project source\Vivre.Desktop      # launch the app (Vivre.exe)
```

- .NET 10 SDK; build with `dotnet` (no Visual Studio / MSBuild needed).
- **`dotnet test`/`dotnet build` on the solution does not build `Vivre.Desktop`** (it isn't a
  test dependency) — build it explicitly before launching, or you'll run a stale exe.
- Per-user data: settings/lists/scripts under `%APPDATA%\Vivre\`; logs under
  `%LOCALAPPDATA%\Vivre\logs\` (Serilog, rolling daily).

## Conventions

- **Do NOT add a `Co-Authored-By: Claude ...` trailer (or any AI/Claude attribution) to commit
  messages.** This overrides the default Claude Code behavior — write commit messages with no
  co-author/attribution line.
- Commit only when the user asks; never push to a remote without explicit instruction.
- No empty `catch {}` — surface failures (to the activity log / `LastError`), don't swallow them.
