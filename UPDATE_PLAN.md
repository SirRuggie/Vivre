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

---

## The compiled agent (`Vivre.UpdateAgent`)

A ~25 KB **net48** console EXE (net48 runs on every modern Windows target with no runtime to
deploy). It does search → download → install/uninstall locally as SYSTEM and writes append-only
progress JSONL that the controller tails.

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
- **Side panel** (focused machine) — its update checklist with an **Applicable | Installed** scope
  toggle, All/None, and Install / Uninstall buttons.
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

**Verified live on real targets (2026-05):** scan (with counts), install with live progress,
uninstall (including a cumulative update correctly reporting "can't be removed" / `0x800F0825`),
a scheduled reboot firing at its set time, and the "WinRM unhealthy" degraded-host flag coming and
going as the self-heal re-tests. One box flapped WinRM-unhealthy intermittently — confirmed to be
the back-off / self-heal working as designed, not a regression.
