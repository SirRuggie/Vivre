# Scripts

A curated library of PowerShell scripts for running against ConfigMgr (SCCM) clients from
Vivre — one machine, the selection, or everything at once.

**All scripts target PowerShell 7** (the app's runspace host). They use `Get-CimInstance` /
`Invoke-CimMethod`, **not** the legacy `Get-WmiObject` / `[wmiclass]` / `[wmi]` accessors, which
do not exist in PowerShell 7.

## Categories

| Folder | What's in it |
| --- | --- |
| `Reboot/` | Force restart, restart-only-if-pending, warn-then-restart (5 min), and cancel-pending. |
| `SCCM Client/` | Restart/check the CcmExec agent, reset policy, request machine policy, run all client actions, clear/inspect the client cache. |
| `SCCM Inventory & Updates/` | Full hardware inventory, update scan & eval, DCM baseline scan, count missing updates, install all required updates. |
| `Windows Update/` | Direct Windows Update Agent: count missing, download & install (bypasses ConfigMgr). |
| `Repair/` | Reset the Windows Update cache, rebuild the WMI repository, re-register WMI/BITS DLLs, restart a service, force a Group Policy update. |
| `Info/` | Logged-on user, free disk, uptime/last boot, why-a-reboot-is-pending, OS version, installed-software search. |

## Heads-up — these change the target machine

The following **do something** and, run against a selection, do it everywhere at once:

- `Reboot/Restart - force now` — reboots in 5 seconds with no user prompt (unsaved work is lost).
- `Reboot/Restart - if reboot pending` — safer: only reboots machines that actually need it.
- `Info/Logoff all users` — ends every interactive session.
- `SCCM Inventory & Updates/Install all required updates (SCCM)` and
  `Windows Update/Download and install updates` — install patches and may reboot.
- `SCCM Client/Clear CM client cache` and `Repair/Restart Windows Update (reset cache)` — delete cached content.

Scripts ending in `(search)` or `Restart a service` are **templates** — edit the variable at the
top (`$Name`, `$ServiceName`) before running.
