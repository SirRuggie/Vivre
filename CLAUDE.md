# CLAUDE.md

This file guides Claude Code (claude.ai/code) when working in this repository.

## What this is

**Vivre** — a **.NET 10 / WPF** Windows desktop tool for managing Microsoft Configuration Manager
(SCCM/MEMCM) clients at scale: a tabbed grid of computers you ping, health-check, run SCCM client
actions / arbitrary PowerShell against, and patch through a built-in Windows Update lane. Named for
the One Piece *Vivre Card* — every grid row tracks one machine's life force (see **Help ▸ About Vivre**).
End-user how-to lives in-app under **Help ▸ How to use Vivre** (`HelpContent.cs`), not here.

Threat model: a single admin running it against their own environment — favour usability and
maintainability over enterprise hardening. It's a dense power tool, not a consumer app.

## Start here

This file is the architecture + conventions reference. Two companions:
- **[UPDATE_PLAN.md](UPDATE_PLAN.md)** — the Windows Update (WUA) lane deep-dive and its
  **load-bearing reliability constraints — read it before touching the update or remoting code.**
- **[README.md](README.md)** — the human-facing overview + roadmap.

Keep this file and UPDATE_PLAN current when a decision changes or a feature lands.

## Layout

- `source/Vivre.slnx` — the solution (`.slnx` format, the .NET 10 default).
  - **`Vivre.Core`** (net10.0) — non-UI logic: `Models`, `Net` (ping), `PowerShell`
    (`PSRunspaceHost` — the one place remoting happens), `Sccm` (`ConfigMgrClient`, client actions),
    `Remoting` (`WinRmEnabler` DCOM, `HostRebootProbe`), `Credentials`, `Computers` (named-list
    store), `Scripts` (script library), `Logging`, `Updates` (the WUA lane — see UPDATE_PLAN.md),
    `Vitals` (`VitalsProbe` + the pure `VitalityScorer` — the read-only 0-100 machine health score),
    `Remediation` (`RemediationService` — start a service / free disk / end a process from the Vitals triage view),
    `Deploy` (`DeploymentService` — **stage** a package: copy a file/folder to a temp dir on the
    target, no execution. The admin runs the install themselves. Transport prefers the **SMB admin
    share** (`\\host\C$`, like SCCM/PsExec — fast, single copy) and falls back to **WinRM** (zip →
    chunked transfer → SHA-256 verify → expand) when SMB is blocked. An earlier install-as-SYSTEM
    version was dropped — watching an install over a session that EDR agents tear down mid-install
    proved unreliable; delivering files and letting the admin's scripts install is robust),
    `Software` (`SoftwareProbe` — check whether a named product is installed per machine → the grid's
    Software column; registry-based, read-only),
    `Columns` (`CustomColumnProbe` — run a user PowerShell one-liner per machine → a custom grid column;
    the column manager hides/shows built-ins + adds custom/predefined columns, persisted to AppData),
    `Rdp` (`RdpHostStore` — the Cross-Domain RDP folder/host tree; `RdpCredentialStore` — DPAPI-per-user saved RDP
    logins + the credential-inheritance resolver. UI-free — the embedded RDP ActiveX control + its
    WindowsFormsHost live in Vivre.Desktop only).
  - **`Vivre.Desktop`** (net10.0-windows) — the WPF app, ships as **`Vivre.exe`**: WPF-UI Fluent
    shell, `ShellViewModel` (tabs) + `WorkspaceViewModel` (per tab), `WorkspaceView`, dialogs.
    Composition root in `App.xaml.cs` (manual DI — services built once and injected). The output
    assembly is `Vivre` but the namespaces stay `Vivre.Desktop`.
    - **Cross-Domain RDP is machine-gated:** the View ▸ Cross-Domain RDP item only appears (and only
      opens) when `Environment.MachineName` matches the `ShellViewModel.CrossDomainRdpMachine` const
      (currently `"APVHOP"`). To re-target it to another PC, change that one const.
  - **`Vivre.UpdateAgent`** (net48) — tiny compiled EXE run as SYSTEM on the target to do WUA
    install/uninstall with real progress callbacks; bundled beside `Vivre.exe` (see UPDATE_PLAN.md).
  - **`Vivre.Core.Tests`** (net10.0, xUnit).
- `tools/RemoteRun` — dev console to exercise remote PowerShell (WinRM) against a host.
- `scripts/` — the curated PowerShell script library (PS7 / `Get-CimInstance`), organised into
  category folders; shipped with the app and seeded into `%APPDATA%\Vivre\Scripts` on first run.
  Surfaced via the grid's right-click **Run script…**, which opens the Run Script window (the
  library is grouped by category there).

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

- **Answer concisely, in plain (layman's) terms.** Lead with the bottom line; keep chat replies
  short and jargon-free, and skip the deep technical walkthrough unless asked. (Code, commit
  messages, and docs still use full technical precision — this is about chat replies.)
- **Do NOT add a `Co-Authored-By: Claude ...` trailer (or any AI/Claude attribution) to commit
  messages.** This overrides the default Claude Code behavior — write commit messages with no
  co-author/attribution line.
- Commit only when the user asks; never push to a remote without explicit instruction.
- No empty `catch {}` — surface failures (to the activity log / `LastError`), don't swallow them.
- Friction (a confirm) only on irreversible/production actions — reboot, uninstall, fleet install,
  large delete, closing a tab with work, replacing a loaded list. Keep ping/scan/check/copy one-click.
- All remoting goes through `PSRunspaceHost`; never let a raw SDK exception reach the UI (translate
  it). Don't reintroduce per-poll WinRM shells or the Add-Type WUA COM shims (see UPDATE_PLAN.md).
- Keep **CLAUDE.md** (this file), **UPDATE_PLAN.md**, and **README.md** current when a decision
  changes or a feature lands — they're the human-readable source of truth.
- **After every commit, verify the in-app how-to guide** (`HelpContent.cs`, surfaced as **Help ▸ How
  to use Vivre**). Any user-facing change — a new/renamed action, moved or restyled UI, or a changed
  workflow — likely needs a matching how-to topic. Update it in the same commit (or an immediate
  follow-up) so the guide never describes UI that no longer exists.

## Commit messages

Follow **[Conventional Commits](https://www.conventionalcommits.org/)** — `type: imperative summary`:

- **Type** (required prefix): `feat` (new feature) · `fix` (bug fix) · `docs` (docs only) ·
  `refactor` (no behaviour change) · `perf` (performance) · `test` (tests) ·
  `chore` (build / deps / housekeeping) · `style` (formatting only) · `ci` (pipeline).
- **Subject**: imperative mood ("add", not "added"/"adds"), lower-case after the colon,
  **≤ 72 chars**, **no trailing period**. One logical change per commit.
- **Body** (optional, for non-trivial commits): blank line after the subject, wrap at ~72 cols,
  explain **what & why** — not how. Use `-` bullets for multiple points.
- Still applies: **no `Co-Authored-By` / AI attribution trailer**, commit **only when asked**,
  and commits are **unsigned**.

Examples:
```
feat: add searchable "How to use Vivre" help guide
fix: stop reboot and update message columns from colliding
docs: bring CHANGELOG and UPDATE_PLAN current
```
