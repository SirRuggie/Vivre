# Vivre — key file-path map

> **Project knowledge note:** The load-bearing files, so new chats don't re-derive them. Paths are relative to the repo root (SirRuggie/Vivre).

## Patch lane / 2016 LCU lane
- `source/Vivre.Core/PowerShell/PSRunspaceHost.cs` — WSMan connect/execute; `MaxConnectionRetryCount=0` on both sites (the WSMan retry crash fix). **Also the PSModulePath contaminator — see gotcha below.**
- `source/Vivre.Core/Updates/WuaUpdateLane.cs` — the normal Windows Update lane; owns the agent bytes + `SmbAgentLane`. Install selector lives here (an install is multi-call).
- `FullPackageLcuLane` (Vivre.Core/Updates) — the Server 2016 full-package CU lane. `StageAsync`, `VerifyAsync`, `ComponentCleanupAsync`.
- `PatchService.cs` / `IPatchService` (Vivre.Core) — per-host serialization owner (the `_inFlight` guard the LCU lane reuses). The LCU lane lives INSIDE PatchService so Stage/Cleanup/Wave can't collide with a WUA install on the same box.
- `RebootWave.cs` / `IRebootWave.cs` (Vivre.Core) — the wave state machine. Graceful→8min→force escalation, scoped to operator-selected + confirmed boxes only.
- `DcomRebootTrigger` (Vivre.Core) — the ONLY reboot primitive in the C# codebase (wave-only, DCOM `Win32Shutdown`); SMB reboot fallback for Kerberos boxes (64337d1).
- `DcomLcuBuildReader` (Vivre.Core) — reads the UBR over DCOM for Verify.
- Reboot-readiness probe (3 signals, fail-safe: unreadable = not-ready) and TCP-445 reachability probe.

## ⚠ Two gotchas that make a Windows PowerShell 5.1 shell-out misbehave (load-bearing, reusable)

These BOTH bit the WUG feature and together ate most of a debugging session. Any NEW code that
writes a temp `.ps1` and shells out to **Windows PowerShell 5.1** must respect BOTH or it will fail
in confusing, non-obvious ways. They are independent — fixing one does not fix the other.

### Gotcha 1 — PS7 contaminates `PSModulePath` (module reads as "not installed")
Vivre hosts an **in-process PowerShell 7 runspace** (`PSRunspaceHost`). When the PS7 SDK initializes
that runspace it **rewrites the host process's `PSModulePath`** to PS7's module folders
(`…\Documents\PowerShell\Modules`, `…\Program Files\PowerShell\Modules`, the SDK's own). Any child
process started with `UseShellExecute=false` **inherits that contaminated path** — so a shelled-out
**5.1** child looks only in PS7's module folders, NOT `WindowsPowerShell\Modules`, and
`Get-Module -ListAvailable` comes back **empty for a module that is actually installed**.
- **Symptom:** a 5.1 module reads as "not installed" from inside Vivre, though a plain 5.1 shell +
  `Import-Module` finds it fine. (The original false "WhatsUpGoldPS isn't installed" report.)
- **Fix — do this for ANY 5.1 shell-out:** after building the process env, call
  `psi.Environment.Remove("PSModulePath")`. With no inherited value the 5.1 child rebuilds its native
  path (CurrentUser + AllUsers `WindowsPowerShell\Modules` + `$PSHOME\Modules`) — exactly like a plain
  admin shell. Harmless no-op if it isn't set.
- **Applied at:** both `RunPreflightProcessAsync` AND `RunAsync` in `WugMaintenance.cs`.

### Gotcha 2 — BOM-less UTF-8 script → PS 5.1 reads it as ANSI → it won't even PARSE
This was the REAL root cause of the WUG saga (after Gotcha 1 was already fixed). **Windows PowerShell
5.1 treats a `.ps1` with no byte-order-mark as ANSI (Windows-1252), not UTF-8.** `File.WriteAllText` /
`WriteAllTextAsync` with no explicit encoding write UTF-8 **without** a BOM. So any non-ASCII byte in
the script — an em-dash `—` (UTF-8 `E2 80 94`), curly quotes, `✓`, etc. — is misread as 2–3 garbage
ANSI characters, corrupting the token stream so **the whole script fails to parse before a single line
runs**. Nothing executes, nothing is emitted, and the C# side defaults to a wrong/empty result.
- **Symptom:** parse errors from a temp `Vivre_*.ps1` — "Unexpected token 'X'", "the string is
  missing the terminator", "missing closing '}'" — all pointing at a line that contains a non-ASCII
  character. The script never produces output, so a downstream feature silently shows its default
  (here: the false "isn't installed").
- **Fix:** write the temp script as **UTF-8 WITH BOM** — .NET
  `new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)` (BOM bytes `239,187,191` = `EF BB BF`).
  PS 5.1 sees the BOM and reads the file as UTF-8, so non-ASCII survives.
- **In Vivre:** both 5.1 launchers write through ONE shared helper **`WritePs51ScriptAsync`**
  (`WugMaintenance.cs`) so a new call site can't silently regress it; locked by regression test
  **`WritePs51Script_writes_utf8_with_bom`**. Any new 5.1-shell-out script writer must use that helper
  (or the same BOM encoding).
- **VALIDATION TRAP that hid this for a whole session (meta-lesson):** a PowerShell validation harness
  that writes the test script with `Set-Content -Encoding UTF8` **adds a BOM in 5.1** — so it tests a
  file the real C# launcher (no BOM) NEVER produces, and **passes while the real thing fails**. When
  validating a shelled-out script, reproduce the EXACT bytes the real launcher writes
  (`new UTF8Encoding(...)`), or the test is theater. More broadly: *a validation that doesn't mirror
  the real write/launch path can green-light the very bug it should catch.*

## WUG maintenance (WhatsUp Gold) — RESOLVED, committed
- `source/Vivre.Core/Wug/WugMaintenance.cs` — talks to WUG via the **`WhatsUpGoldPS` PowerShell-
  Gallery module** (NOT the REST API directly) by shelling out to **Windows PowerShell 5.1**
  (`…\System32\WindowsPowerShell\v1.0\powershell.exe`) and running embedded scripts that call
  `Connect-WUGServer` / `Get-WUGDevice` (name/IP → DeviceId) / `Set-WUGDeviceMaintenance`. Runs on
  the **operator's workstation ONLY** — no target/managed box is ever contacted (names map to WUG
  DeviceIds server-side), and there is **NO reboot path**. Holds `RunAsync` (the real set), the
  pre-flight `TestConnectionAsync` / `InstallModuleAsync`, the shared `RunPreflightProcessAsync`
  launcher, `ParsePreflight`, the `WritePs51ScriptAsync` BOM-write helper, the `ProgressMarker`, and
  the `__WUGRESULT__` result-line marker. **Both 5.1 shell-outs strip `PSModulePath` AND write
  UTF-8-with-BOM** — see the two gotchas above.
- **Result-parse contract (the fix that made errors truthful):** "module missing" is reported ONLY on
  an explicit signal from the script. A timeout / empty output / unparseable output now surfaces the
  **real connection error** instead of a false reinstall prompt. The result line is tagged
  `__WUGRESULT__` so cmdlet chatter can't corrupt the parse, and the script has a backstop trap so a
  crash still emits a structured result (carrying `modulePresent=true`). Validated under real 5.1:
  success → "Connected ✓"; bad creds → "username or password was rejected"; unreachable → "Couldn't
  reach WhatsUp Gold at …"; crash → "Pre-flight error …" — every non-success keeps the module marked
  present.
- `MaintenanceWindow.xaml`(.cs) — the dialog. **Test connection** + (hidden-until-needed) **Install
  module** buttons. "Set maintenance" runs the pre-flight FIRST and keeps the dialog OPEN until it
  passes (module present + server reachable + creds valid); only on pass does it close + fire the
  real per-device set fire-and-forget. Reuses the existing `StatusText` line for inline messages.
- Caller: `WorkspaceViewModel.SetWugMaintenanceAsync` (+ `TestWugConnectionAsync` /
  `InstallWugModuleAsync`).
- **Credential invariant (DO NOT deviate):** the WUG password is a `SecureString` →
  `new NetworkCredential(string.Empty, pw).Password` plaintext → handed to the child **only** via the
  `VIVRE_WUG_PASS` environment variable — never on the command line, to disk, or in a log.
- **SSL:** `Connect-WUGServer … -Protocol https -IgnoreSSLErrors` (self-signed WUG cert) — the
  pre-flight connect-test must match the real run exactly, or it passes/fails differently.
- **Persistence:** only the server address persists (`AppSettings.WugServer`); credentials are NEVER
  saved.
- **Live-confirmed end to end** (10.70.25.111): Test connection → "Connected ✓"; Set → row narrates
  "WhatsUp Gold: maintenance ON/OFF"; and the device shows **Maintenance** state in WUG's own console.

## On-box agent
- `source/Vivre.UpdateAgent/Program.cs` — the agent. `AddPackage` mode (DISM-add as SYSTEM, stream %, RebootPending success-check) and `Cleanup` mode. **REBOOT-FREE at the root** — the latent self-reboot + RebootAfter/RebootBehavior plumbing was excised; a grep finds zero shutdown/restart calls.
- `BootBusyGuard.cs`, `BootServicingState.cs`, `Callbacks.cs` — agent boot/servicing-state helpers.
- net48 target (net462 reverted — ValueTuple BCL gap).

## Build & publish (publish.ps1) — how the deployable is produced
- `publish.ps1` (repo root, now `C:\src\Vivre`) — the one command that produces a deployable Vivre. Ruggie runs:
  `powershell -ExecutionPolicy Bypass -File "C:\src\Vivre\publish.ps1" -Zip`
  It wipes the output folder, then runs `dotnet publish source/Vivre.Desktop/Vivre.Desktop.csproj`
  self-contained win-x64 (no .NET runtime needed on the target) into `publish\Vivre-win-x64\`
  (+ a `.zip` beside it with `-Zip`). `-FrameworkDependent` makes a small build that needs the
  .NET 10 Desktop Runtime on the target. There is **no** `--no-build` / `--no-dependencies` /
  `--no-restore` — it is a FULL build of the whole dependency graph from current source.
- **publish.ps1 rebuilds + re-signs + re-bundles the on-box agent automatically. There is NO
  separate agent-rebuild step.** Any change to `Vivre.UpdateAgent` (message strings, new phases
  like "Cleaning") goes LIVE the moment you run publish.ps1. Do NOT flag "agent rebuild debt" —
  it does not exist for the normal publish flow.
- Why that's true (the csproj facts, so they aren't re-derived):
  - `Vivre.Desktop.csproj` —
    `<ProjectReference Include="..\Vivre.UpdateAgent\Vivre.UpdateAgent.csproj" ReferenceOutputAssembly="false" />`.
    The agent is a standalone net48 EXE (not linked into the WPF app), but `ReferenceOutputAssembly="false"`
    keeps it in the build-ORDER graph → it is built first from current source. (`NU1702` is
    suppressed for the intentional cross-TFM reference.)
  - `Vivre.UpdateAgent.csproj` target `SignUpdateAgent` (`AfterTargets="Build"`) Authenticode-signs
    the freshly built EXE; `AppendRuntimeIdentifierToOutputPath=false` pins the output to `…\net48\`
    so the `-r win-x64` publish finds the EXE at a deterministic path.
  - `Vivre.Desktop.csproj` target `CopyUpdateAgentAfterPublish` (`AfterTargets="Publish"`) copies the
    fresh signed `Vivre.UpdateAgent.exe` into the publish folder beside `Vivre.exe`.
- Caveats:
  - **Signing is best-effort.** It signs only if the code-signing cert (thumbprint
    `1A5CE867A4660C271C9C7AA0DD2F923A1FE05953`) is in `CurrentUser\My` on the build machine. On
    Ruggie's dev box it's present → signed. On a machine without the cert it **ships unsigned**
    (still gated by the agent's ACL'd drop dir + SHA-256 self-check, just not Authenticode-signed).

## Build/deploy — repo location (OneDrive trap RESOLVED)
**The repo + publish output now live at `C:\src\Vivre` (out of OneDrive).** This closed the recurring
stale-binary class that ate much of the WUG saga: OneDrive's cloud "placeholder" files used to copy as
stale/empty, so the test box launched OLD code while everyone believed it was fresh; it also caused the
`.git/worktrees` lock and LF/CRLF churn. With the repo on a non-synced path that whole class is gone.
- `.gitattributes` `* text=auto` is in place to keep line-endings stable.
- **Freshness self-check (still a handy general technique** for confirming a test-box copy matches a
  just-built binary): read `Vivre.Core.dll` bytes and look for a marker string unique to the new code —
  `$u16=[Text.Encoding]::Unicode.GetString([IO.File]::ReadAllBytes('<path>\Vivre.Core.dll')); $u16.Contains('__WUGRESULT__')`
  → true = fresh; false = stale, re-copy. No longer a routine necessity now that OneDrive is out of the
  path, but useful when a deploy looks off.

## Desktop / UI
- `ViewModels/WorkspaceViewModel.cs` — the big VM. `InstallRowAsync` (routing inserts at the top), the LCU panel commands, `HasSelectedServer2016` (selection-gate for the Reboot Wave button), the filter enum/predicate, the two-bucket completion summary, `_appSettings` access. Also `OnIsUpdateModeChanged` (Health/Patching mode flip + the patch-only-filter reset when entering Health). WUG callers `SetWugMaintenanceAsync` / `TestWugConnectionAsync` / `InstallWugModuleAsync` live here too.
- `ViewModels/ShellViewModel.cs` — `CloseTab` and tab/list management.
- `WorkspaceView.xaml`(.cs) — ONE view, mode-swapped by `IsUpdateMode` (Health = `IsMachineMode`; Patching = `IsUpdateMode`), with two DataGrids that swap by visibility. The filter chips live in **two separate mode-gated StackPanels** (Health bar = 6 chips; Patching bar = full set incl. Updates, Server 2016, Not scanned, Scheduled). The LCU action bar (Border) is gated to Patching via a 3-condition MultiDataTrigger (ActiveFilter==Server2016 AND HasServer2016 AND IsUpdateMode). Status-pill label renames are **DataTrigger overrides in the Patching Status column only** — never edit the shared `PhaseChipLabelConverter` (it's also used by `ComputerDetailWindow.xaml`, so editing it leaks renames into the detail window / Health context).
- `MainWindow.xaml`(.cs) — `DockMaxOpenFraction`, dock-height clamp. **Now hosts the completed `ui:NavigationView` shell** (LeftCompact pane + hamburger; Fleet → Health/Patching sub-items; Scripts; Cross-Domain RDP; Settings pinned bottom; mode chips + menu bar removed). The NavigationView refactor incl. Phase 4 is DONE — TODO: capture the as-built shell layout here in detail next time this file is touched.
- `AdaptiveLayoutController.cs` — layout controller.
- `SettingsPage.xaml`(.cs) — Integrations, Help & about. The LCU package folder + "This month's CU" (KB / target UBR) fields live here.
- `App.xaml`(.cs) — composition root.
- Converters: `EnumEqualsConverter.cs`, `UxConverters.cs`, `PhaseChipConverter.cs` (class `PhaseChipLabelConverter`; SHARED — Patching Status column + ComputerDetailWindow; do NOT edit for Patching-only label changes). Help text: `HelpContent.cs`.
- **Dialog sizing standard** (audit `fe4d68e`): modals use `CenterOwner`; fixed-content forms use `SizeToContent` + Min/Max (NoResize OK); content-heavy/list dialogs use `CanResize` + a ScrollViewer with the action buttons in their OWN row OUTSIDE the ScrollViewer (so they're always visible). `SoftwareCheckWindow` uses `SizeToContent="Height"` + `MaxHeight` so it opens fully visible and only scrolls on a too-short screen. Sizing attributes only — **never bind `Run.Text`** (the a0cb80a render-break class).
- `Computer.cs` — `OsBuild` populated in `ApplyVitals`; `Is2016`/`IsServer2016` predicate (single source of truth for both panel filter and routing). `PatchState` derives from `UpdatePhase` + `RebootRequired`. `IsScheduled => ScheduledNextRun is not null`. `PatchPhase.Cleaned` → `PatchState.Done`.

## Settings / data
- `AppSettings` (Vivre.Desktop, in `AppSettingsStore.cs`) — LCU package folder (`C:\Vivre\VivrePackages`), This-month's-CU (KB + target UBR), defaults KB5094122 / 9234. Also `WugServer` (the only persisted WUG field — credentials are never saved).
- `source/Vivre.Core/Computers/ComputerListStore.cs` — the computer list store.
- `RebootOutcomeMessages.cs` (Vivre.Core) — the 6 ready-to-use reboot-and-verify outcome strings ("Back online · installed N · up to date", etc.). Defined but **NOT wired** — the queued Smart reboot-and-verify flow will call them.

## Tests
- `source/Vivre.Core.Tests/...` — **344 green** as of the WUG resolution (was 339 at the WUG pre-flight
  build, 328 at the Part B naming pass, 313 at the panel rebuild). Includes the wave behavior tests
  (graceful→forced, not-ready refusal, rollback=red, late-return-still-verifies-green, never-returns=red),
  the LCU classifier tests, the phase→state mapping tests, the `RebootOutcomeMessages` tests, the
  `ParsePreflight` result-classification tests (now incl. the safe-default contract + `__WUGRESULT__`
  marker extraction — failure cases must NOT claim the module is missing), and the
  **`WritePs51Script_writes_utf8_with_bom`** BOM regression guard (`Vivre.Core.Tests/Wug/`).

## Docs in repo
- **Root:** `UPDATE_PLAN.md` (the WUA lane), `CHANGELOG.md`, `README.md`, `CLAUDE.md`.
- **`docs/`:** `key-file-path-map.md` (this file), `vivre-backlog.md`, `2016-LCU-lane-spec.md`, `2016-LCU-panel-spec.md`, `2016-LCU-red-team-review.md`.
- Retired: the nav-refactor plan doc (refactor complete) and the overnight Kerberos status doc (spent) were removed; their content lives in CLAUDE.md / UPDATE_PLAN.md / this file.

## Recent commits (restore points, newest last)
- LCU routing + wiring (`b078014`); self-populating 2016 panel rebuilt clean (`5631a61`) — NOTE the
  earlier `a0cb80a` proved to render broken (Run.Text two-way bind); panel rebuilt on `b078014` base.
- scan/install SMB-agent fallback on generic WinRM failure (`9d3f82a`)
- quick-wins label fixes (`0a40d17`, `c78c3bf`)
- Health/Patching chip-bar split + LCU-bar gated to Patching (Part A)
- status pill + message naming standard + Not scanned / Scheduled chips (Part B)
- retired the two "Scheduled task" columns; folded into the update message (`087b748`)
- plain WinRM-unavailable guidance on no-fallback ops + RoutingPowerShellHost comment fix
- consistent dialog sizing across all popups (`fe4d68e`)
- **WUG maintenance pre-flight (Test connection + module/creds check); fix PS7-contaminated
  `PSModulePath` + BOM-less script encoding on the 5.1 shell-outs (`756fa9d`)** — closes
  the WUG saga; covers the pre-flight feature, Gotcha-1 strip, Gotcha-2 BOM helper + regression test,
  and the truthful-connect-error parse contract.
