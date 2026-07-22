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

## Docs routing

Full doc inventory with one-liners: **[docs/README.md](docs/README.md)**. Read-before-touching triggers:
- Update or remoting code → **[windows-patching-lane.md](docs/windows-patching-lane.md)** (load-bearing reliability constraints).
- The 2016 patch lane → docs/2016-LCU-lane-spec.md (+ panel spec and red-team review).
- Cross-Domain RDP scaling → docs/vivre-rdp-scaling-and-fcm-findings.md.
- App.OnStartup, the sweep, or large-list load → docs/cold-start-freeze-and-threadpool-findings.md
  (the load-bearing `ThreadPool.SetMinThreads` fix — don't delete it).
- ANY freeze / hang / "it's slow" → docs/freeze-hunting-playbook.md, BEFORE theorizing.
- The WUG lane's resolver or SSL/connect path → docs/wug-state-check-findings.md.
- Priorities + live finding status → docs/vivre-backlog.md · file locations → docs/key-file-path-map.md.

Keep this file, the patching-lane doc, and README.md current when a decision changes or a feature
lands — they're the human-readable source of truth.
Any new case-file / findings doc ships with the "point-in-time record, never edited after the fact"
header from creation (the wug-state-check-findings.md pattern) — that keeps docs/README.md's
"never edited" claim true.

## Shell / tab hosting

The shell is a WPF-UI `ui:NavigationView` (LeftCompact pane: **Fleet ▸ Health / Patching · Scripts ·
Cross-Domain RDP**; **Settings** pinned bottom); the old shell→NavigationView migration is complete
and retired. **Load-bearing constraints — DON'T break:**
- **`TabControlEx` keep-alive is mandatory.** Tab content stays in the visual tree
  (`Visibility`-toggled, never rebuilt) so the Cross-Domain RDP tab's embedded MSTSC ActiveX control +
  live RDP sessions survive tab/section switches. `NavigationView`'s default `Frame` navigation
  destroys pages — so use NavigationView for the **pane only** and host section content in the
  keep-alive container.
- **The bottom dock is window-level/global** (Activity + per-machine Updates), not per-tab.
- **Mode is fixed by Fleet section, not a per-tab toggle** — Health tabs are health mode, Patching
  tabs are patching mode.

## Layout

Short entry per project; the namespace-by-namespace inventory lives in
**docs/key-file-path-map.md ▸ "Project & namespace map"**.

- `source/Vivre.slnx` — the solution (`.slnx` format, the .NET 10 default).
  - **`Vivre.Core`** (net10.0) — all non-UI logic: remoting (`PSRunspaceHost` is the ONE WinRM choke
    point; `RoutingPowerShellHost` flips Kerberos-broken hosts to SMB/DCOM), SCCM actions, the WUA
    update lane, vitals, remediation, deploy (stage-only), software probe, custom columns, WUG
    maintenance, RDP stores, shared settings.
  - **`Vivre.Desktop`** (net10.0-windows) — the WPF app, ships as **`Vivre.exe`** (assembly `Vivre`,
    namespaces `Vivre.Desktop`): WPF-UI Fluent shell, `ShellViewModel` (tabs) + per-tab
    `WorkspaceViewModel`, dialogs. Composition root `App.xaml.cs` (manual DI).
    - **Cross-Domain RDP is machine-gated:** the left-nav item only appears (and only opens) when
      `Environment.MachineName` matches the `ShellViewModel.CrossDomainRdpMachine` const (currently
      `"APVHOP"`). To re-target it to another PC, change that one const.
    - **RDP session scale is PINNED at 100% (THE PIN CARDINAL):** `LocalScale()` in
      `RdpSessionView.xaml.cs` always returns `(100u, 100u)` — any session scale above 100% breaks
      Failover Cluster Manager context menus on the live cluster (Microsoft won't-fix), and a build +
      green tests will NOT catch a change. Readability comes from the client-side `ZoomLevel` zoom,
      never from session scale. Gate greps after ANY commit touching this file: `= LocalScale();` →
      exactly 2 hits (the connect-time extended-settings block + `ResizeRemote`) and
      `_rdp.UpdateSessionDisplaySettings` → exactly 1 call site. If either count changes, a new
      scale-sender exists — stop and read docs/vivre-rdp-scaling-and-fcm-findings.md first.
  - **`Vivre.UpdateAgent`** (net48) — tiny compiled EXE run as SYSTEM on the target to do WUA
    install/uninstall with real progress callbacks; bundled beside `Vivre.exe` (see docs/windows-patching-lane.md).
  - **`Vivre.Core.Tests`** (net10.0, xUnit).
- `tools/RemoteRun` — dev console to exercise remote PowerShell (WinRM) against a host.
- `scripts/` — the curated PowerShell script library (PS7 / `Get-CimInstance`), category folders;
  shipped with the app, seeded into `%APPDATA%\Vivre\Scripts` on first run, surfaced via the grid's
  right-click **Run script…**.

## Building / running

```
dotnet build  source\Vivre.slnx
dotnet test   source\Vivre.slnx
dotnet run --project source\Vivre.Desktop      # launch the app (Vivre.exe)
```

- .NET 10 SDK; build with `dotnet` (no Visual Studio / MSBuild needed).
- **`dotnet test` on the solution does not build `Vivre.Desktop`** (it isn't a test dependency) —
  after a test-only run, `dotnet build` the solution (which DOES build it) or the Desktop project
  before launching, or you'll run a stale exe.
- Data locations (split by scope):
  - **Personal, per-user** — `AppSettingsStore` writes `%APPDATA%\Vivre\settings.json`: theme, grid
    columns, hidden columns, auto-check-on-load, max simultaneous installs, WUG state-check
    concurrency, nav-pane state, dock height, software→service map. Computer **lists** and
    **scripts** also live under `%APPDATA%\Vivre\`.
  - **Shared, machine-wide (operational)** — `SharedSettingsStore` (`Vivre.Core.Configuration`) writes
    `C:\ProgramData\Vivre\settings.json` (created on first save, Authenticated-Users Modify ACL):
    this month's CU (`MonthlyCu`), the LCU + package folders, the WUG server, the staged-machine list.
    - Fresh disk read per `Load` (uncached, tolerant — defaults on any read failure) so one operator
      sees another's save.
    - Writes go through `Update(Action<SharedSettings>)` — **read-merge-write and sibling-key-safe**:
      it re-reads the file, changes only the keys the delta touches, preserves every other key
      (including keys this build doesn't know), and **refuses (throws) if an existing file can't be
      read** rather than stomping unread keys with defaults — the fix for a save that once wiped
      `StagedHosts`.
    - **Never put credential material here** — an Update-time reflection guard throws on a
      credential-shaped field.
    - Still whole-file last-writer-wins between operators until an optimistic-concurrency stomp guard
      lands — see docs/vivre-backlog.md.
  - **Logs** — `%LOCALAPPDATA%\Vivre\logs\` (Serilog, rolling daily).

## Conventions

- **Chat replies:** lead with the outcome. Assume a technically capable reader (systems engineer)
  who is not a .NET dev — translate .NET/WPF/C# jargon in one line where it's unavoidable; don't
  explain general IT concepts (AD, DNS, WinRM, SMB, etc.). Default under 150 words outside of task
  reports; skip the deep technical walkthrough unless asked. (Code, commit messages, and docs keep
  full technical precision — this rule is about chat replies only.)
- **Do NOT add a `Co-Authored-By: Claude ...` trailer (or any AI/Claude attribution) to commit
  messages.** This overrides the default Claude Code behavior.
- Commit only when the user asks; never push to a remote without explicit instruction.
- No empty `catch {}` — surface failures (to the activity log / `LastError`), don't swallow them.
- Friction (a confirm) only on irreversible/production actions — reboot, uninstall, fleet install,
  large delete, closing a tab with work, replacing a loaded list. Keep ping/scan/check/copy one-click.
- **Reboot cardinal — NOTHING auto-reboots.** Every reboot path (the Reboot & Verify wave, Force reboot
  including its narrow Kerberos-auth DCOM fallback in `ForceRebootRunner`, Schedule reboot, the script
  library) fires only from an operator's explicit per-box click + confirm — never an independent decision.
  The shutdown primitive `Win32Shutdown` lives in EXACTLY ONE file (`DcomRebootTrigger.cs`). Gate grep
  after ANY commit touching reboot code: `grep -rl --include=*.cs "Win32Shutdown" source/` → exactly that
  one file. Don't write the primitive's name in other source prose/comments — it breaks the gate.
- All remoting goes through `PSRunspaceHost`; never let a raw SDK exception reach the UI (translate
  it). Don't reintroduce per-poll WinRM shells or the Add-Type WUA COM shims (see docs/windows-patching-lane.md).
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
- **Bump the app version only when cutting a RELEASE (a deploy build) — NOT per merge.** `<VersionPrefix>`
  in `source/Vivre.Desktop/Vivre.Desktop.csproj` is the single source of truth (the build stamp and the
  **Help ▸ About** box both derive from it via `AboutWindow.RunningVersion()`). Between releases, merged
  work accumulates under **Unreleased** in `CHANGELOG.md` with **no per-merge version bump**. When a
  release is cut: bump `VersionPrefix` (operator's call on the number — **minor** for features, **patch**
  for fix-only), rename **Unreleased** to that version with a dated heading, and start a fresh
  **Unreleased**. Every merge MUST still add its user-facing change to **Unreleased** — that's how
  nothing is lost between releases. (The earlier per-merge-bump rule caused churn and is retired; do
  not reintroduce it.)
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

## Report format (every report to the operator — no exceptions)

End every task with exactly these sections, in order. No preamble, no restating
the task. Plain summary lines; technical detail lives only under the finding it
belongs to.

- **VERDICT** — one line: done / blocked / needs decision, plus build status
  (0 warnings/0 errors or not) and test count.
- **OPERATOR ACTIONS** — numbered, ordered by severity. Every item that needs my
  click, decision, or visual check. Exactly two sentences each: what it is, what
  I must do or decide. Never omit or merge items to save space. New findings
  only — a previously-reported item reappears only if its status changed.
  None → write "None."
- **VERIFIED** — findings you personally confirmed against the actual code (per
  the PM Verification Standard above), 1–2 lines each.
- **UNVERIFIED** — worker/agent claims you could NOT confirm from the code
  directly. Never fold these into VERIFIED. None → write "None."
- **RISKS / WATCH** — 3 sentences max. None → write "None."

## Commit messages — keep them SHORT

Follow **[Conventional Commits](https://www.conventionalcommits.org/)** — `type: imperative summary` —
and in the common case **stop there: one short subject line IS the whole commit.**

- **Subject** (required — usually the entire message): `type: what changed`, imperative mood ("add", not
  "added"/"adds"), lower-case after the colon, **≤ 72 chars**, no trailing period. One logical change per
  commit. `type` ∈ `feat` · `fix` · `docs` · `refactor` · `perf` · `test` · `chore` · `style` · `ci`.
- **Body — OPTIONAL and rare:** at most **one or two short lines**, only for genuinely non-obvious
  context or a cardinal-safety note. Most commits need NO body. Never multi-paragraph bodies or
  restated diffs — that detail goes in the operator REPORT and `docs/` / `CHANGELOG.md`, not git history.
- Still applies: **no `Co-Authored-By` / AI attribution trailer**, commit **only when asked**, **unsigned**.

Good — subject only: `fix: rebuild RDP control on Reconnect and keep involuntary drops open`
