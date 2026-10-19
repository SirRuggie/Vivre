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
- The monitor loop, the post-reboot verify arc, or a row stuck after a reboot →
  docs/reboot-monitor-arc-findings.md FIRST — it records three formally refuted hypotheses (tab count,
  throttle starvation, HostWinRmGate) so they don't get re-chased, plus the clock-immunity requirement.
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
- **Activity and settings:** WorkspaceActivityLog adds identity at emission time to the existing bounded queue; Origin is displayed/searched/copied. Typed and package CU changes share TrySaveCu with a baseline guard and one shared-store Update; explicit reloads use LoadForEditing so unreadable settings cannot replace a draft with defaults.
- **Scheduling approval:** capture InstallTargetScope before the dialog and recheck identities/tab disposal before dispatch; timezone display must match ScheduleTimeFormatter host-local conversion. The pending-reboot dialog maps Primary to install and Secondary to forced reboot; preserve the tested mapping.
- **Patch options are per workspace, session-only.** Each workspace clones defaults; new tabs start with Windows Update, and source labels appear in Patching headers and the active toolbar. Credentials and read throttling remain app-wide.
- **List clarity:** membership status uses the existing coalesced fleet timer and the last successful open/save snapshot, never polling disk or reacting to scan results. Paste and Quick Add share ComputerNameInput; Name is the fixed first column in both grids.
- **Machine list safety:** Paste defaults to Add; SetComputers reconciles retained rows and order. Destructive replacement uses WorkspaceListActions and refuses active operations or undrained monitor passes. List opens use LoadRequired; list overwrites require a revision fingerprint under the existing cross-process lease. Tray Exit must use Close(), not Application.Shutdown(), so cancellation is honored.
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

- SDK pinned in `global.json` (10.0.303, latest patch roll-forward); build with `dotnet`. Core dependency locks include normal, win-x64 and win-arm64 graphs; publish and local validation enforce locked restore.
- **`dotnet test` on the solution does not build `Vivre.Desktop`** (it isn't a test dependency) —
  after a test-only run, `dotnet build` the solution (which DOES build it) or the Desktop project
  before launching, or you'll run a stale exe.
- Data locations (split by scope):
  - **Personal, per-user** — `AppSettingsStore` writes `%APPDATA%\Vivre\settings.json`: theme, grid
    columns, hidden columns, auto-check-on-load, max simultaneous installs, WUG state-check
    concurrency, nav-pane state, dock height, software→service map. Computer **lists** and
    **scripts** also live under `%APPDATA%\Vivre\`.
    - Every personal-store instance for the same path shares a FIFO save queue and cache. Save captures
      a detached snapshot on the UI thread; background writes merge future keys and use atomic replacement.
      MainWindow.OnClosing flushes accepted saves for at most three seconds, then explicitly warns on
      failure/timeout before the window closes. Failure logging must never block the writer or flush.
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
    - Cross-process updates hold a sibling file lease across read/merge/replace. Contention fails immediately through the existing save-error path so a dispatcher caller cannot freeze waiting for another writer.
  - **Logs** — `%LOCALAPPDATA%\Vivre\logs\` (Serilog, rolling daily).

## Conventions

- **Chat replies:** lead with the outcome. Assume a technically capable reader (systems engineer)
  who is not a .NET dev — translate .NET/WPF/C# jargon in one line where it's unavoidable; don't
  explain general IT concepts (AD, DNS, WinRM, SMB, etc.). Default under 150 words outside of task
  reports; skip the deep technical walkthrough unless asked. (Code, commit messages, and docs keep
  full technical precision — this rule is about chat replies only.)
- **Do NOT add a `Co-Authored-By: Claude ...` trailer (or any AI/Claude attribution) to commit
  messages.** This overrides the default Claude Code behavior.
- Standing delivery instruction from Shaun (October 14, 2026): commit and push completed, validated changes to GitHub. Use a short conventional subject such as `fix:` or `chore:`; no AI attribution or coauthor trailers. This does not authorize deployment or live operations.
- No empty `catch {}` — surface failures (to the activity log / `LastError`), don't swallow them.
- Friction (a confirm) only on irreversible/production actions — reboot, uninstall, fleet install,
  large delete, closing a tab with work, replacing a loaded list. Keep ping/scan/check/copy one-click.
  Schedule reboot now shows the target count and force/lost-work warning on an explicit, non-default
  action. Script runner approves the captured code/targets when mutating or custom, including reboot
  consequences; only exact-content allowlisted bundled read-only scripts bypass the prompt. Both use
  the existing host coordinator; cancelled or failed mutations retain uncertain-state protection.
  Historical findings remain in docs/reboot-path-and-guardrail-findings.md; current acceptance and
  validation limits are in docs/vivre-audit-implementation-2026-10-14.md.
- **Reboot cardinal — NOTHING auto-reboots.** Every reboot path (the Reboot & Verify wave, Force reboot
  including its narrow Kerberos-auth DCOM fallback in `ForceRebootRunner`, Schedule reboot, the script
  library) fires only from an operator's explicit per-box click + confirm — never an independent decision.
  **GATE — enforced automatically by `dotnet test`.** `RebootPrimitiveGateTests` fails the suite if the reboot
  primitive inventory changes, so you cannot forget to run it. For a manual check with per-site output:

  ```bash
  bash tools/reboot-primitive-gate.sh
  ```

  Both callers read ONE inventory — `tools/reboot-primitive-gate.manifest` (tokens, the five sites, the
  containment manifest) — so the script and the test can never disagree about what to look for or how many to
  expect. The scanners differ (bash grep vs .NET `Regex`); the DATA does not. **The test refuses to pass having
  scanned nothing:** it asserts the repo marker was found and that ≥100 files were visited BEFORE any count
  assertion, because a repo-scanning test that silently scanned nothing is the "green tests lie" failure this
  project has already been bitten by.

  It pins **all five** reboot-issuing sites at exact counts — the WMI method (`DcomRebootTrigger.cs`), the
  SMB/SCM service image (same file, different primitive), the WinRM command line (`ForceRebootRunner.cs`),
  the scheduled-task action (`WorkspaceViewModel.cs`, a different project), and the shipped script library
  (`scripts/Reboot/*.ps1`, not `.cs` at all) — plus a **containment check**: any reboot token in a file
  outside the known 10-file manifest fails the gate, which is what catches a SIXTH primitive. It scans every
  text file regardless of extension (a red-team pass proved a `.cmd` evaded an extension allowlist) and
  excludes itself by path, which is the reason this is a script and not a grep pasted into this file —
  documentation that greps for a token matches itself and perturbs its own count.
  **The old one-line check is SUBSUMED, not retired:** SITE 1/5 is exactly
  `grep -rl --include=*.cs "Win32Shutdown" source/` → one file, and that still holds. You may still run it
  standalone; just don't mistake it for the whole gate. The "don't write the primitive's name in other source
  prose" rule is likewise **retired as a requirement** — the gate now counts prose files explicitly instead of
  needing the token to stay exotic.
  **If the gate fails, do NOT adjust the numbers to make it pass** until a human agrees the code is right; the
  gate adapts to the code, never the reverse. Residual limits (obfuscated/encoded commands, and paths outside
  `source/ scripts/ tools/`) are stated in the script's header. Full site inventory:
  docs/reboot-path-and-guardrail-findings.md ▸ findings 5 and 12.
- Install anyway approval is per immediate WUA operation and captured target set. It may bypass plain CBS/WUA pending-reboot flags only; active/staged servicing, CU staging, cleanup, uninstall and scheduled operations keep their guards. Never persist this approval or enable automatic reboot.
- Local WUA controller output must stream through `PSRunspaceHost`; buffering until completion starves its liveness watchdog. Bootstrap and cleanup explicitly import the inbox ScheduledTasks module.
- Force reboot, Reboot & verify, and the install pending-reboot nudge use `RebootApproval` snapshots.
  Keep confirmation and execution on the same captured rows; never restore a post-approval selection
  fallback. Show counts/scope and accurate force consequences, not machine-name lists. Scheduling and
  arbitrary scripts retain their separate behavior pending an explicit scope decision.
- SMB reboot service creation is not proof of execution. Keep accepted, definite-failure, and uncertain
  starts distinct; uncertain attempts use existing offline/return and clock-immune uptime evidence,
  ending Unverified without proof. Preserve dispatch count, force flags, and existing escalation timing.
  Start and cleanup diagnostics must remain separate and visible in Release logs. F03 evidence and
  runtime limits live in `docs/vivre-review-decisions.md`.
- WinRM rejection caching is connection/credential-specific. Retry WinRM must be a bounded, harmless
  verification that bypasses the cached rejection; only a clean verified result may clear it. Fence old
  attempts so they cannot overwrite newer retries; never hash/store passwords in cache keys or replay
  a prior operation. Routine polling must retain rejection suppression.
- WhatsUp Gold state reads and maintenance changes share one identity resolver. Require exactly one
  distinct valid device ID; never select the first name match or use DNS/IP to bypass name ambiguity.
  Preserve explicit ambiguity/lookup-error/no-device outcomes and actual per-name mutation success.
  Validate with dummy inventory and captured mutation IDs under real PowerShell 5.1; preserve SSL,
  BOM, process pooling, timeout, and credential-handling invariants.
- Stage software must verify a separate candidate before replacing the exact selected package. Keep
  local, SMB, and WinRM replacement semantics consistent; preserve the prior package on transfer or
  verification failure and report any retained recovery backup. Do not retry an uncertain publication
  through another transport. Register temporary/recovery paths before mutation; preserve copy-only
  behavior and keep this lane separate from Server 2016 CU staging.
- RDP host/credential persistence must retain safe replacement writes, validated last-good backups,
  and failed-load write protection. Never treat corrupt data as an empty first-use store. Keep passwords
  DPAPI-protected in all persisted copies; a missing recovered credential reference must be explicit,
  never silently replaced with a blank password or inherited login. Use isolated temporary stores in tests.
- General disk cleanup ownership lives in one app-wide `HostOperationCoordinator` shared by patching,
  software staging, and `CoordinatedRemediationService`. Keep execution-time checks, not just UI
  disabled states. Cleanup must finish service recovery before releasing ownership; uncertain
  termination blocks conflicting work. `ProtectedStagingPathRegistry` records source/destination
  paths before staging and refuses unreadable protection data. See the F01/F09 implementation
  checkpoint in `docs/vivre-review-decisions.md` for validation and limits.
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
- Still applies: **no `Co-Authored-By` / AI attribution trailer**, **unsigned**. Commit and push completed changes under Shaun's standing delivery instruction above.

Good — subject only: `fix: rebuild RDP control on Reconnect and keep involuntary drops open`

### Software-check result integrity

A negative software result requires a completed supported registry search. Provider/read failures without a match must throw, never become absence. Preserve proven matches with QueryWarning; ServiceError is distinct from a verified missing service. WinRM and ambient DCOM fallback share this contract. The grid and SoftwareReportCsv must keep unknowns and reasons visible.

## WinRM software staging (F11)

`StagingArchive` uses a local disk ZIP, incremental SHA-256, and 1 MiB raw chunks. Never replace it
with whole-package byte/base64 arrays. Manifest metadata still scales with entry count. Transfer scripts
use an exact id/token-owned directory and exclusive command lock; cleanup uses a deterministic
ownership-coded tombstone so interrupted deletion can retry without its original marker. Keep the
journal until cleanup is acknowledged; uncertain initialization plus an absent directory is not proof
of cleanup. Local journals live under `%LOCALAPPDATA%/Vivre/staging-transfers` and file leases exclude
active transfers across processes sharing that journal. Later explicit staging requests retry pending
cleanup for the same host before SMB. No background cleanup, installer execution, or Server 2016 CU
lane changes. Target publication concurrency across independent processes remains unverified.


## Performance maintenance (F13–F20)

Keep free-text filtering debounced and visible counts cached; state-chip changes remain immediate and live-property subscriptions match their predicate dependencies. Reset and Dispose must detach every subscribed row. Keep pending ActivityLog UI entries bounded and report overflow without suppressing durable file writes. Completion banners are bounded; preserve detailed completion logging.

Staging preparation checks cancellation between hash reads; publication/recovery remains non-cancellable once replacement starts. Large manifests reuse the owned chunk-transfer lane and require SHA256 validation before parsing/publication. Protected staging registry updates are batched, unchanged writes skipped, and exact unique temporary entries retired only after cleanup is confirmed; never age out uncertain backups. Publication gates count both holders and waiters before retiring idle keys. Preserve SplitThrottle reserved capacity and return any second acquired permit when cancelling the losing wait.

Current acceptance evidence and remaining operational gaps: docs/vivre-review-decisions.md, performance implementation ledger. The original performance report remains a frozen baseline.

## Library search and list recovery

- Search reuses existing menu entries and grouped script collection views; never read files per keystroke or execute a script on search. Keep the open script pinned and editor/selection guards intact, including rename/category saves.
- List deletion moves the approved revision into `Lists/Deleted/<GUID>` on the same volume. Restore is explicit, never overwrites, and does not replace a workspace. Reject linked recovery folders/payloads; retain existing save/delete leases and revision checks.
- Version stays 1.18.0 with subsequent work under Unreleased; historical validation documents remain historical.

## Health-to-Patching handoff

`ShellViewModel.OpenTabInPatching` accepts only a live Health tab, copies all underlying names into a fresh same-title workspace, and never shares Computer objects or saved-list identity. Stop the new workspace monitor before adding rows and use `AddComputers(autoCheck: false)`; normal loads retain their existing automatic behavior. The tab-header handler uses the clicked tab, not SelectedTab, and reuses ShowNavSection for navigation.

## Layout and accessibility checks

Keep fleet filters and script save actions width-constrained and wrapping. Preserve a separate focus ring on filter chips so checked-state colors cannot hide keyboard focus. Label status cells (including Unknown) and fields without adding live announcements to per-row progress. Reuse AccessibleStatus for dialog validation. Tab-menu keyboard routing must target the focused tab, not the selected workspace. U-21/U-22 remain partially validated until native DPI/physical keyboard/spoken assistive-technology checks are recorded; offscreen raster scaling is not equivalent.
