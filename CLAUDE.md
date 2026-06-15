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

Keep this file, UPDATE_PLAN, and README current when a decision changes or a feature lands.

### Shell / tab hosting (as-built — the `ui:NavigationView` refactor is COMPLETE)
The phased shell→NavigationView plan is done and retired (history in git + CHANGELOG). The shell is a
WPF-UI `ui:NavigationView` (LeftCompact pane: **Fleet ▸ Health / Patching · Scripts · Cross-Domain
RDP**; **Settings** pinned bottom). **Load-bearing constraints — DON'T break:**
- **`TabControlEx` keep-alive is mandatory.** Tab content stays in the visual tree
  (`Visibility`-toggled, never rebuilt) so the Cross-Domain RDP tab's embedded MSTSC ActiveX control +
  live RDP sessions survive tab/section switches. `NavigationView`'s default `Frame` navigation
  destroys pages — so use NavigationView for the **pane only** and host section content in the
  keep-alive container.
- **The bottom dock is window-level/global** (Activity + per-machine Updates), not per-tab.
- **Mode is fixed by Fleet section, not a per-tab toggle** — Health tabs are health mode, Patching
  tabs are patching mode (the old on-canvas mode chips were removed).

## Reference docs (read on demand; don't re-derive)
- docs/key-file-path-map.md — load-bearing file locations
- docs/2016-LCU-lane-spec.md, docs/2016-LCU-panel-spec.md, docs/2016-LCU-red-team-review.md — read before touching the 2016 patch lane
- docs/vivre-backlog.md — current priorities + what's done

## Layout

- `source/Vivre.slnx` — the solution (`.slnx` format, the .NET 10 default).
  - **`Vivre.Core`** (net10.0) — non-UI logic: `Models`, `Net` (ping), `PowerShell`
    (`PSRunspaceHost` — the one WinRM choke point, wrapped by `RoutingPowerShellHost`: on a Kerberos
    `0x80090322` rejection it flips the host to the SMB/DCOM path via the session-scoped
    `HostTransportCache`, and Vitals scores the degradation — see UPDATE_PLAN.md ▸ "Kerberos-broken
    hosts"), `Sccm` (`ConfigMgrClient`, client actions),
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
- **Shelling out to Windows PowerShell 5.1** (the WUG lane in `Wug/WugMaintenance.cs`, and any
  out-of-process `powershell.exe` launch) has two traps `dotnet build` can't catch — a compiled-in PS
  string only fails at *runtime*:
  - **Write the temp `.ps1` as UTF-8 *with* BOM** (use `WugMaintenance.WritePs51ScriptAsync`). 5.1
    reads a BOM-less script as the system ANSI code page, so one non-ASCII char (e.g. an em-dash in a
    message) corrupts and the **whole script fails to parse — it runs nothing and emits nothing**. A
    regression test locks the BOM in. Validate compiled-in PS by running it under **real** 5.1; don't
    "verify" by writing the file with `Set-Content -Encoding UTF8`, which silently adds a BOM and hides this.
  - **Strip the inherited `PSModulePath`** (`psi.Environment.Remove("PSModulePath")`) before launch.
    Vivre's in-process PS7 runspace rewrites this process's `PSModulePath` to PS7 folders; a child
    launched with `UseShellExecute=false` inherits it, so a 5.1 child can't find 5.1 modules.
  - Never flatten a launch/parse/timeout failure into a false negative (e.g. "module missing") — surface
    the real error.
- Keep **CLAUDE.md** (this file), **UPDATE_PLAN.md**, and **README.md** current when a decision
  changes or a feature lands — they're the human-readable source of truth.
- **Bump the app version when you ship a user-facing change.** `<VersionPrefix>` in
  `source/Vivre.Desktop/Vivre.Desktop.csproj` is the single source of truth (the build stamp and the
  **Help ▸ About** box both derive from it via `AboutWindow.RunningVersion()`). Bump it **in the same
  commit** as the change: **minor** (`1.11.0 → 1.12.0`) for a new feature, **patch** (`1.11.0 → 1.11.1`)
  for a fix or small UX tweak. Don't let it drift — every shipped feature/fix should move it. (It stalled
  at 1.10.1 across three features once; that's the failure mode this rule prevents.)
- **After every commit, verify the in-app how-to guide** (`HelpContent.cs`, surfaced as **Help ▸ How
  to use Vivre**). Any user-facing change — a new/renamed action, moved or restyled UI, or a changed
  workflow — likely needs a matching how-to topic. Update it in the same commit (or an immediate
  follow-up) so the guide never describes UI that no longer exists.

## PM Verification Standard (non-negotiable, applies to every task)

Worker agent output is a starting point, not a conclusion. The PM must
independently verify all load-bearing findings and commit work.
Specifically:

- After agents return investigation findings, the PM re-reads every
  file cited and confirms every factual claim against the actual code.
  If a claim cannot be confirmed from the code directly, say so
  explicitly — never forward an agent summary as verified fact.
- After agents complete a build commit, the PM independently re-runs
  both builds and all tests, re-reads every new and modified file, and
  diffs any changed tests to confirm no assertion was weakened or
  removed to make tests pass.
- "Workers said it's green" is never sufficient. The PM's sign-off
  means the PM read it and confirmed it personally.
- If an agent finding and the actual code disagree, the code wins.
  Flag the discrepancy explicitly in the report.

This standard applies to investigations, red-team passes, build commits,
and checkpoint reviews — every task without exception.

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
