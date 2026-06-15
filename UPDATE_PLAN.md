# Windows Update (WUA) lane

How Vivre patches machines — the deep-dive that complements **[README.md](README.md)** (overview)
and **[CLAUDE.md](CLAUDE.md)** (architecture + conventions). The lane is merged into `master`.

---

## What it is

A built-in Windows Update manager — the BatchPatch-equivalent. It scans, installs, uninstalls, and
schedules updates directly on each target via the **Windows Update Agent (WUA)** COM API — **no SCCM
deployment required** (an SCCM-deployment lane would only install updates an admin has deployed, and
this environment deploys none, so it would install nothing). The **Update Source** choice (Windows
Update / Microsoft Update / Managed-WSUS) and the **exclude-by-name** list live here.

Open **Fleet ▸ Patching** in the left nav to get the update grid + patch actions over the same machine
list. The **Fleet ▸ Health** features (SCCM health, client actions, Run Script) are untouched.

---

## Two operations, two transports

- **Scan** — a read-only WUA search script run over WinRM (`PSRunspaceHost.RunRemoteAsync`). Fast;
  returns the applicable (or already-installed) update list + count for the chosen source.
- **Install / Uninstall** — WUA install will **not** run inside a WinRM network-logon session
  (`WU_E_NO_INTERACTIVE_USER`). So the lane drops a compiled agent + a JSON config onto the target,
  runs it from a **one-time SYSTEM scheduled task**, and streams the agent's progress back over a
  **single persistent WinRM session**.

The drop directory is the **Administrators/SYSTEM-only** `C:\ProgramData\Vivre\agent` (the WinRM
bootstrap creates + ACL-hardens it on the target), not the old world-writable `C:\Windows\Temp` —
so a non-privileged local user can't plant or swap the binary that then runs as SYSTEM. Per-run
filenames (`Vivre_WUA_<runId>.*`) isolate concurrent runs.

---

## Kerberos-broken hosts: the SMB + SCM fallback lane

A growing set of domain servers reject WinRM with Kerberos `0x80090322` (SEC_E_WRONG_PRINCIPAL). For
those, both transports above are dead, so Vivre falls back to the **BatchPatch/PsExec model**, driven
entirely from the operator's machine on the **current Windows login** (NTLM SSO — no Kerberos, no
credential prompt): `Vivre.Core/Updates/SmbAgentLane.cs` + `Remoting/RemoteServiceController.cs`.

1. **Drop** the signed agent + config to `\\host\C$\ProgramData\Vivre\agent` over the admin share
   (ACL-hardened to SYSTEM + Administrators) and **SHA-256-verify** the dropped EXE.
2. **Launch** it as a one-shot **LocalSystem service** through the SCM over the SMB `svcctl` named
   pipe (`RemoteServiceController`, P/Invoke advapi32). The agent runs in `--service` mode and reports
   RUNNING immediately, so `StartService` never trips the SCM's **1053** start-timeout.
3. **Tail** the agent's progress JSONL over SMB (same shape the WinRM lane streams); an agent
   heartbeat + a silence watchdog detect a dead/hung agent. A **Scan** also reads back the agent's
   JSON update array.
4. **Teardown:** stop → wait for stopped → `DeleteService` → delete the per-run drop files.

**Selection lives in `WuaUpdateLane`** (not the routing decorator): Scan / Install / Uninstall try
WinRM first and, on the typed `KerberosWrongPrincipalException`, transparently route here. The
operation result is **deliberately indistinguishable** from a WinRM run — the Kerberos degradation is
surfaced only through Vitals, never on an operation result. The lane runs on the **ambient identity
only** (an alternate credential is ignored — the whole point is that the current login works over
SMB/DCOM where Kerberos doesn't). **Not yet supported:** *Schedule* over this lane (it surfaces a
neutral "scheduling isn't available — run now" message; scheduling rides the WinRM scheduled task).

---

## The compiled agent (`Vivre.UpdateAgent`)

A small **net48** EXE (net48 runs on every modern Windows target with no runtime to deploy). It does
search → download → install/uninstall locally as SYSTEM and writes append-only progress JSONL that
the controller tails.

- **Two launch modes.** `Vivre.UpdateAgent.exe <config.json>` is plain console mode (the WinRM lane's
  SYSTEM scheduled task). `Vivre.UpdateAgent.exe --service <config.json>` hosts the same work under a
  `ServiceBase` so the SMB lane can run it as a LocalSystem service: `OnStart` returns immediately (the
  SCM "I'm running" check-in that avoids error 1053), the work runs on a thread, and the agent
  self-stops when done. Service mode also writes a periodic **heartbeat** line (console mode does not —
  so the WinRM stream is byte-identical to before).
- **Three operations.** `Mode` is Install (default), Uninstall, or **Scan** (the SMB lane's read-only
  WUA search, mirroring the WinRM scan script; it writes the update array to `ResultPath` for the
  controller to read back over SMB).

- It uses WUA's own `IDownloadProgressChangedCallback` / `IInstallationProgressChangedCallback` for
  **ground-truth percent** — the thing PowerShell can't supply.
- WUA COM is referenced via a **committed `Interop.WUApiLib.dll`** (generated once with `tlbimp.exe`).
  `COMReference` / `ResolveComReference` do **not** work under `dotnet`'s .NET-Core MSBuild, so the
  committed tlbimp output is what keeps `dotnet build` headless.
- The Desktop csproj copy targets bundle `Vivre.UpdateAgent.exe` beside `Vivre.exe`.

---

## Scan / Install / Uninstall / Schedule

- **Scan** fills the per-machine checklist (Applicable scope) or the installed-updates list
  (Installed scope). The exclude-by-name list filters titles fleet-wide; `Type='Software'` hides
  drivers unless **Include drivers** is on.
- **Install** installs the ticked KBs (or all applicable if nothing's ticked), with live progress
  (download maps to 0–50% of the bar, install 50–100%). Reboot is **reported, not forced** by
  default (`RebootBehavior.ReportOnly`).
- **Uninstall** = **WUA then DISM**. WUA `BeginUninstall` (live %) when WUA can remove the update,
  otherwise **`dism /Online /Remove-Package`** (the supported path — `wusa /uninstall` is deprecated
  for cumulative updates). The Installed scan only marks a row removable when **WUA reports
  `IsUninstallable`**. **Note:** modern cumulative updates are **non-removable by design** — Windows
  refuses with `0x800F0825` (permanent SSU/package). That's expected, not a tool bug; the per-KB
  reason is surfaced to the user.
- **Schedule** (right-click → *Schedule ▸*) — runs a one-time SYSTEM task at a chosen time:
  - **Install updates…** — `RunBehavior.ScheduleAt`; registers the agent task with a one-time trigger.
  - **Reboot…** — registers a `Vivre_Reboot` task that runs `shutdown /r /f` (no agent needed).
  - **Cancel scheduled task** — unregisters any pending `Vivre_*` task on the host.
  What's queued and when now reads inline in the update message; it clears once the trigger time has
  passed (client-side — no per-tick polling of the target).

---

## Transient WUA reach failures — silent retry + no false-green (load-bearing)

**Root cause (proven, not inferred).** `0x80072EE2` (WININET/WinHTTP timeout, Win32 12002) is a
**transient** failure of the on-box WUA's **first** network call — the **SLS (Service Locator Service)**
lookup during *"Processing auto/pending service registrations"*, which happens **before** search,
download, or install. Proven from `APVWUG`'s `WindowsUpdate.log`: the SLS call to
`sls.update.microsoft.com/SLS/{9482F4B4-…}` completed with `[80072EE2]` and **http status code [0]** (no
response at all) during the failed run, and the **identical URL** returned `[00000000]` / http `200` in
~0.5s an hour later. Windows' own internal 3 retries (Retry Counter 0,1,2) were exhausted inside a
~2m38s blind window. So it is a brief network blip, **idempotent to retry** — nothing was searched,
downloaded, or installed when it fires.

**The BatchPatch trap (the rule we enforce, never to be re-litigated).** BatchPatch masks this exact
failure as a fake-green *"no applicable updates."* Vivre must not. **A non-clean search NEVER reads as
up-to-date** — "0 updates" means *"Up to date"* **only** on a CLEAN success (WUA
`ResultCode == orcSucceeded`). A box that couldn't be scanned must read as a reach failure, never as
patched.

**Two faces.** The transient failure surfaces two ways; the classifier triggers on the **HRESULT, not
the phase**:
1. the search **throws** a transient HRESULT (the `0x80072EE2` hard-fail), OR
2. the search **returns without throwing** but did not cleanly succeed — `SucceededWithErrors` / `Failed`
   / `Aborted`, i.e. 0 updates carrying a non-success result code (e.g. `0x80240438`).

**Transient family** (`TransientWuaError` — pure, host-free, unit-tested): `0x80072EE2` + `0x80240438`
plus the documented WININET/WinHTTP transport and WU_E_PT (HTTP protocol-talker) timeout / 5xx siblings.
Auth/config (`0x8024401B` 407 proxy-auth, `0x80244017` 401 denied), HTTP-4xx, and real install failures
are **deliberately excluded** so they surface immediately without a pointless retry.

**Retry** (`TransientRetryRunner` — pure, unit-tested; wired at the **VM/lane level by re-dispatching the
whole operation**, so the protected on-box agent is untouched for the retry itself):
- Wraps the **entire** operation (service-registration → search → download → install) — the failure is at
  service-registration, so a download-only retry would miss it.
- **3 retries, ~60s backoff, jittered** (up to +15s via `Random.Shared.Next` — the same pattern as the
  reboot-trigger gate) so a fleet-wide SLS outage doesn't retry in lockstep against the recovering service.
- During retries the row shows a calm *"Couldn't reach Windows Update — retrying (n/3)…"* (a working
  state, never an error). Exhaustion → `PatchPhase.Unreachable`, which reduces to `PatchState.Error` (red;
  counted/filtered as a failure everywhere) with a distinct **"Can't reach WU"** chip label and an honest
  *"(0x…) after N tries — try again"* message. Never up-to-date, never zero-applicable. Locked by tests.

**(a) Fresh per-attempt timeout — load-bearing.** The scan's timeout is **per-attempt**
(`ScanAttemptTimeoutSeconds` = 300s, applied via a linked CTS *inside* each retry attempt), **NOT** one
budget shared across all attempts + backoffs. The shared form (the original bug) killed attempt 2 before
attempt 3 ran. A per-attempt timeout — distinguished from a user Stop by the linked-CTS check — is itself
treated as a transient → retry. **Worst case for a fully-stuck box ≈ 24 min** (4 × 300s + 3 × ~75s
backoffs), showing *"retrying…"* throughout, then *"Can't reach WU."* Install is unchanged — its 3h
per-host budget already dwarfs the retry.

**(c) Install re-entry guard.** Once install has **begun** (`Installing`/`PendingReboot`/`Done` or
`InstalledCount > 0`), a late transient does **not** re-run — a re-search would find 0 applicable, report
a false *"up to date"*/zero, and drop the installed count + reboot-pending. It surfaces a terminal
*"install was interrupted after it began — re-scan to confirm"* instead. Search/download transients
(install never began) still retry.

**Coverage — all four paths:** WinRM scan, WinRM install, SMB-agent scan, SMB-agent install. The WinRM
scan emits the search `ResultCode` as a status row that `ScanAsync` checks before the up-to-date path; the
SMB-agent `RunScan`/`RunInstall` write a terminal Error line on a non-clean `ResultCode` (a **read-only**
check — no install/reboot behavior added), which `SmbAgentLane` surfaces as a `Failed` status the VM retry
runner re-dispatches. So Kerberos-broken boxes (which hit this failure most) get the **same** retry as
WinRM boxes. **No auto-reboot:** the entire feature is classify/retry/timeout/status plumbing.

---

## 2016 staged patching toggle (opt-in)

Server 2016 (build 14393) patching is **opt-in per box**. By default a 2016 box patches through the normal
WUA lane like a 2019/2022 box; only a box the operator has **flagged** (`Computer.RequiresStagedPatching`,
persisted in `AppSettings.StagedHosts` — OrdinalIgnoreCase, normalized after every load) routes to the
full-package DISM lane. The lane exists for the boxes whose Express-delta CU genuinely fails through Windows
Update, not for every 2016 box — so the operator declares which ones, instead of all 14393 boxes auto-routing.

- **Routing** (`InstallRowAsync`): a non-flagged 2016 box → WUA. A flagged box that's already staged → skip
  ("CU staged — run Reboot Wave"); already verified this session → WUA for its remaining minor updates; not
  yet staged → the decision dialog owns it (the row is never silently auto-staged or WUA-installed — reaching
  it directly skips with guidance). `Server2016Targets()` (the panel's Stage / Clean up / Verify) is
  flagged-only, and `LcuRouting.RebootVerifyLaneFor(osBuild, requiresStaging)` is override-aware: a non-flagged
  2016 box verifies via the WUA lane, not UBR.
- **Decision dialog** (`StagedInstallDecisionDialog`, gated by the View-layer `StagedInstallInteraction`):
  when Install / Install all hits a flagged box whose CU isn't staged, the operator chooses **Stage CU first**
  (the chip Stage workflow, scoped to those boxes), **Install minor updates only** (WUA with **every**
  CU-titled KB excluded so the broken Express-delta CU never goes via WUA — requires this month's CU set in
  Settings as a floor), or **Cancel** (skip the flagged boxes; the rest of the fleet still installs). A
  Settings-vs-scan CU KB mismatch warns at the top. The partition (`StagedInstallPlanner`) and CU-title
  matching (`Lcu2016CuMatcher` — `FindCuKb` for the single-confident warning, `CuKbs` for the conservative
  exclude set) are pure + unit-tested.
- **Already-current pre-check** (`WorkspaceViewModel.ResolveAlreadyCurrentAsync` → `StagedInstallPlanner.PartitionByCurrency`):
  before prompting, each flagged box's UBR is read over the **same** `VerifyLcuAsync` → `DcomLcuBuildReader`
  path the Stage lane's pre-stage check uses; a box already at the target UBR (or verified this session) is
  dropped from the dialog and installs its minor updates via WUA ("Already current — skipped"). **Fail-open** —
  a null/unreadable read (`Unreachable`), a `WrongBuild`, or any error keeps the box in the dialog; only a
  definitive `Verified` excludes it. All flagged boxes current → no dialog at all. Reads are bounded by the
  shared remote throttle and the reader self-times-out (8s), so a dead box can't hang the prompt.
- **Operator surface:** right-click a 2016 row ▸ **Mark as Staged patching** / **Remove Staged flag** (2016 +
  Patching mode only); a narrow **Staged** pill column in the grid (`StagedColumn`, code-behind visibility
  driven by `WorkspaceViewModel.HasStagedServer2016` — hidden entirely when nothing is flagged); **Settings ▸
  Staged patching machines** lists / removes / clears flagged hosts, and a remove/clear re-syncs loaded rows
  (`MainWindow.ResyncStagedPatchingFlags`) so routing never goes stale. Nothing here reboots or stages on its
  own — every CU-committing action stays operator-driven.

---

## Reliability & safety (load-bearing — don't regress)

These mechanisms exist because of real production failures. Don't undo them without understanding why.

- **SYSTEM scheduled task, not a direct WinRM call** — WUA install dies in a network-logon
  (`WU_E_NO_INTERACTIVE_USER`); the one-time SYSTEM task is the workaround.
- **One persistent streaming WinRM session per install — NOT per-poll shells.** The old per-poll
  pattern opened a fresh shell every progress poll and hit `MaxShellsPerUser` (default 30), which
  surfaced as silent stalls and the `InitialSessionState` type-initializer error. Don't go back to it.
- **Don't reintroduce the Add-Type WUA COM-callback shims.** That path was tried and reverted (it
  hung `$searcher.Search()` after the managed CCW registration). The compiled agent is the answer.
- **A reboot-pending target poisons WinRM/PSRP** — a pending reboot corrupts shell init (the
  `InitialSessionState` error, cleared only by actually rebooting the box). So the monitor must not
  hammer it: it **skips re-probing a known-reboot-pending host**, **backs off "degraded" hosts** (on
  the shell-init error) and **periodically re-tests them** so they self-heal once WinRM responds —
  never opening fresh shells every 20s against a sick box.
- **Heartbeat watchdog** — the on-target controller heartbeats every ~15s while the session is alive.
  The client fails a session that goes **fully silent** (no progress **and** no heartbeat) for
  `PatchOptions.NoResponseTimeout` (90s) so a dead/hung session surfaces instead of freezing on stale
  progress. It keys off heartbeat **liveness**, not percent, so it never trips on a slow-but-working
  update.
- **Typed remoting exceptions** — `PSRunspaceHost.TranslateRemotingException` maps raw failures to
  `RemoteSessionLostException` / `RemoteShellInitException` (at both the connect and invoke phases),
  so the UI shows actionable messages, never "The pipeline has been stopped." or the raw
  InitialSessionState text. A user Stop still maps to "Cancelled"; genuine in-script errors pass through.
- **Servicing-collision guards** — the agent's `BootBusyGuard` defers cleanly if a reboot /
  offline-servicing pass is already staged (CBS `RebootPending`, `pending.xml`,
  `PendingFileRenameOperations`); `-StartWhenAvailable` was removed from the task (a missed trigger
  could re-fire at boot); the agent releases its WUA COM RCWs before scheduling any reboot.
- **Per-host serialization** — `PatchService` refuses two CBS/DISM ops (install / uninstall /
  Installed-scope scan) on the same host at once, which also catches the cross-tab "same host in two
  tabs" case the per-row UI guard can't see.
- **Server 2016 Stage preconditions** — the Stage step is gated by a scan-this-session check
  (`LastScannedApplicable != null`; a post-reboot rescan satisfies it) and short-circuits boxes that
  are already staged (RebootRequired && StagedThisSession → "Already staged — run Reboot Wave") or
  already current (a pre-Stage UBR read — the same call Verify makes — returns Verified → "Already
  current — skipped"). The already-current check **fails OPEN**: if the UBR can't be read the box is
  staged anyway, so an unreachable read never silently skips a box that needs the patch. Decision
  logic lives in the pure, unit-tested `StagePreconditions` (Vivre.Core/Updates).

---

## UI surfaces

- **Windows Update grid** — Name · Ping · **Status chip** · Reboot message · Windows-update message ·
  **Progress** · Pending reboot · Command messages (what's scheduled now reads inline in the update
  message — the dedicated Scheduled-task columns were retired).
- **Unified bottom dock** (shared, full width) — one tabbed panel with a **Close** button that hands
  the height back to the grid. Tabs:
  - **Activity** (always present) — the searchable activity log (right-click a line to Copy / Copy all).
  - **Updates** (focused machine; shown only in Patching with a row focused) — **Applicable |
    Installed** sub-tabs (each grid carries only its relevant columns: Applicable shows download Size,
    Installed shows the install date), a KB/title **filter box**, All/None, a "scanned HH:mm" freshness
    stamp, and Install / Uninstall buttons.
  Opening: the status-bar Activity-log toggle lands on Activity; clicking a machine in Patching opens +
  lands on Updates. Resizable via the splitter.
- **Command bar** (Patching) — Scan and Install, labelled for the current target ("…selected (N)" vs
  "…all"). Source / Include-drivers / Exclude live under the **Update options ▾** toolbar button.
- **Right-click** — Scan / Install selected · **Reboot (force now)** · **Schedule ▸** (Install
  updates / Reboot / Cancel) · **Details…** · **Show messages** · Run script… · Client actions ▸ ·
  Enable WinRM.
- **Per-machine detail window** (Details…) — live state (chip, messages, progress, reboot, scheduled)
  + tabs for Applicable / Installed updates / that machine's Messages.

---

## Reboot-and-verify

A fleet-wide reboot-with-confirmation flow accessible from the right-click menu as **Reboot & verify…**.

### Entry point and confirm gate

`WorkspaceViewModel.RebootAndVerifyAsync` (the `[RelayCommand]`) acts only on the
operator-selected rows. The UI shows a confirm dialog naming each machine before any reboot is
issued — nothing fires autonomously. Cancelling the dialog is a no-op.

### Per-box wave (`RebootWaveRowAsync` → `PatchService.RebootWaveLcuAsync` / `RebootWaveWuaAsync`)

Routing is by `LcuRouting.RebootVerifyLaneFor(computer.OsBuild)`:

- **Server 2016 (build 14393)** → `RebootWaveLcuAsync` → `DcomRebootReadinessProbe` +
  `UbrConfirmation`. Pre-reboot guard checks TrustedInstaller-stopped + CBS RebootPending to
  avoid rebooting into the 2-hour Stopping hang. Post-reboot UBR is read via `DcomLcuBuildReader`
  and compared to the operator's target UBR; a rolled-back build is caught as Failed (red).
- **All other boxes** → `RebootWaveWuaAsync` → `BasicReachabilityReadinessProbe` +
  `ReadyConfirmation`. Pre-reboot gate is unconditionally ready (no 2016-specific signals to
  check). Post-reboot confirmation queries `Win32_OperatingSystem` over DCOM/CIM — Confirmed when
  the OS stack answers, NotReady (retry) when it can't be reached yet. It never returns Failed;
  whether updates took is determined by the WUA rescan below.

### Reboot execution (`RebootWave.RebootAndCommitAsync`)

`DcomRebootTrigger` is the **sole reboot primitive** — its only call path is
`RebootWave.RebootAndCommitAsync`. No other code calls Win32Shutdown, `shutdown.exe`,
`Restart-Computer`, or any reboot API.

Flow per box:
1. Pre-reboot readiness check (fails fast with a clear message if not ready).
2. Graceful reboot issued.
3. Wait up to the go-offline window (default 8 min) for the box to drop off TCP-445.
   If it doesn't drop: **escalate to a forced reboot** (completion of the operator-ordered
   reboot, not an autonomous decision). If it still won't drop: red (check it directly).
4. Unbounded offline watch — `OfflineCeiling` only flags "Overdue — check console/iLO" once;
   the clock never stops watching and never fails a box just for being slow.
5. When TCP-445 comes back: confirmation strategy runs. `NotReady` → retry (still coming up);
   `Confirmed` → green (Done); `Failed` → red (e.g. rolled-back UBR).
6. `HardCap` (default 4 h): if the box hasn't returned by then, the live loop exits red with
   "no longer tracking it live — use Verify once it's back up". The standalone Verify action
   is the durable net past the live wave.

### Scale model — two throttles, one gate per wave

`_waveThrottle` (static `SemaphoreSlim(256)`) is the concurrency width for the per-box watch
loops. It is effectively unbounded so ALL selected boxes start their offline watch simultaneously
— a slow Server 2016 commit (45 min typical) never blocks a fast box from completing and
reporting.

`_rebootTriggerThrottle` (static `SemaphoreSlim(12)`) caps the burst rate of simultaneous reboot
*issuance* to protect DCs/DNS/auth services from too many boxes dropping off the network at the
same instant. Both throttles are shared across tabs (static fields on the VM) so a multi-tab
fleet scenario doesn't multiply the burst.

`RebootTriggerGate` wraps the trigger throttle semaphore and adds an optional jitter delay
(500 ms default) spread across slots to further stagger reboot commands when many boxes become
eligible at once. The gate is acquired only around the reboot call and released immediately
after — never held through the offline watch — so it never serializes the per-box verify step.

### Post-reboot rescan (read-only)

After `RebootWave.RebootAndCommitAsync` returns `Done`, `ReportPostRebootOutcomeAsync` runs:

1. **Applicable rescan** — `PatchService.ScanAsync(scope=Applicable)` only. This is
   **strictly read-only**: no Install, no Uninstall, no further Reboot is ever called here.
2. **Reboot-still-pending probe** — `IRebootPendingProbe.IsRebootPendingAsync` (best-effort;
   failure → treat as "don't know", never as pending).
3. **Outcome selection** — `RebootOutcomeSelector.Select(installed, failed, remaining,
   rebootStillPending, scanFailed)` → one of the `RebootOutcomeMessages` format strings
   (now fully wired — see below).

For non-2016 boxes the outcome string replaces `UpdateMessage` as the primary result.
For 2016 boxes the wave's UBR Done message is kept as primary; the rescan appends a
supplementary note (e.g. "· up to date" or "· N update(s) still applicable — run a WUA pass").

### Readiness vs offline-detection distinction

- **Readiness probe** (`IRebootReadinessProbe`) — PRE-reboot question: "may we reboot this box
  right now?" For 2016 this guards against rebooting with TrustedInstaller still stopping. For
  other boxes (`BasicReachabilityReadinessProbe`) it always answers Ready — the 2016-specific
  signals don't apply and the reboot was operator-ordered.
- **Offline detection** (`IReachabilityProbe` / `TcpReachabilityProbe`) — POST-reboot question:
  "has the box dropped off the network yet?" Entirely separate from readiness; drives the
  `WaitForOfflineAsync` loop and the online-watch loop in `RebootAndCommitAsync`.

### `RebootOutcomeMessages` — now wired

`RebootOutcomeMessages.cs` defines six format methods (BackOnlineUpToDate, BackOnlineRemaining,
BackOnlineFailed, InstalledNoReboot, RebootStillPending, BackOnlineRescanFailed, StillRebooting).
`RebootOutcomeSelector.Select` calls five of them (InstalledNoReboot and StillRebooting are
excluded — wrong semantic context). The selector is called from `ReportPostRebootOutcomeAsync`
in `WorkspaceViewModel`, closing the full reboot-and-verify loop.

---

## Status

**Done:** scan; install with live progress; uninstall (WUA + DISM, with the cumulative-update reason
surfaced); schedule (install + reboot + cancel); the reliability mechanisms above; the per-machine
detail window; force-reboot.

**Deferred:**
- Reboot-and-wait as an install option (the agent can reboot; no UI toggle / "waiting…" status yet).
- An SCCM-deployment update lane (only if updates ever get deployed through SCCM here).
- An **update-centric pivot** view — one row per KB across the selected machines ("applies to N,
  install everywhere") for planning a maintenance window by update instead of by machine. Standard in
  fleet patch tools (WSUS/Action1/PDQ); worth building only if by-KB planning becomes a real need.
  The per-machine detail panel covers today's "install all applicable + reboot" workflow.

**Verified live on real targets (2026-05):** scan (with counts), install with live progress,
uninstall (including a cumulative update correctly reporting "can't be removed" / `0x800F0825`),
a scheduled reboot firing at its set time, and the "WinRM unhealthy" degraded-host flag coming and
going as the self-heal re-tests. One box flapped WinRM-unhealthy intermittently — confirmed to be
the back-off / self-heal working as designed, not a regression.
