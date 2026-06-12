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

In a tab, switch to **View ▸ Windows Update** to get the update grid + patch actions over the same
machine list. The Machines-view features (SCCM health, client actions, Run Script) are untouched.

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
  The two **Scheduled task** columns show what's queued + when; they clear once the trigger time has
  passed (client-side — no per-tick polling of the target).

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

---

## UI surfaces

- **Windows Update grid** — Name · Ping · **Status chip** · Reboot message · Windows-update message ·
  **Progress** · Scheduled-task action · next run time · Pending reboot · Command messages.
- **Unified bottom dock** (shared, full width) — one tabbed panel with a **Close** button that hands
  the height back to the grid. Tabs:
  - **Activity** (always present) — the searchable activity log (right-click a line to Copy / Copy all).
  - **Updates** (focused machine; shown only in Windows Update mode with a row focused) — **Applicable |
    Installed** sub-tabs (each grid carries only its relevant columns: Applicable shows download Size,
    Installed shows the install date), a KB/title **filter box**, All/None, a "scanned HH:mm" freshness
    stamp, and Install / Uninstall buttons.
  Opening: View ▸ Activity log lands on Activity; clicking a machine in Update view opens + lands on
  Updates. Resizable via the splitter.
- **Command bar** (Update mode) — Scan and Install, labelled for the current target ("…selected (N)"
  vs "…all"). Source / Include-drivers / Exclude live under the **Updates** menu.
- **Right-click** — Scan / Install selected · **Reboot (force now)** · **Schedule ▸** (Install
  updates / Reboot / Cancel) · **Details…** · **Show messages** · Run script… · Client actions ▸ ·
  Enable WinRM.
- **Per-machine detail window** (Details…) — live state (chip, messages, progress, reboot, scheduled)
  + tabs for Applicable / Installed updates / that machine's Messages.

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
