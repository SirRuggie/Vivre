# Vivre — Windows Update (WUA) lane: the BatchPatch replacement

> **Status (2026-05-27):** Phase 1 **built** on branch `feature/wua-update-lane` — Windows
> `dotnet build` + `dotnet test` green (86 tests); **live-server verification pending**. This doc is the
> WUA-first companion to `REBUILD_PLAN.md` (see its §0 resume bullet). It supersedes the earlier
> SCCM-first draft — the SCCM update-**install** lane is deferred (see below).

## Why WUA-first (the decision that reshaped this plan)

An earlier draft led with an **SCCM lane** (`CCM_SoftwareUpdatesManager.InstallUpdates`). That only
installs updates an SCCM admin has **deployed** to the box — and the user's SCCM admin deploys nothing,
so that lane would install *nothing*. The real BatchPatch-equivalent is the **Windows Update Agent
(WUA) lane**: it scans/downloads/installs whatever is applicable directly on each target via the
`Microsoft.Update.Session` COM API — no SCCM deployment required. That's where the **Update Source
toggle** (Windows Update vs Microsoft Update vs Managed/WSUS) and the **exclude-by-name list** live.

**Existing SCCM features are untouched** — the Machines view keeps all its SCCM client-health checks
and right-click client actions. We simply don't add a deployment-based update installer; patching goes
via WUA.

## Two operations, two transports

- **Scan** — a search script run directly over WinRM (`IPowerShellHost.RunRemoteAsync`). Read-only,
  fast, returns the applicable-update list + count for the chosen source.
- **Install** — WUA install will **not** run inside a WinRM network-logon session
  (`WU_E_NO_INTERACTIVE_USER`). So the lane writes a worker script to `C:\Windows\Temp` on the target
  and runs it from a **one-time SYSTEM scheduled task**; the worker does search→download→install
  locally as SYSTEM and writes a **progress JSON** after each state change; the controller **polls the
  JSON over WinRM**, then deletes the task + files. Register/start is **WinRM-first with a DCOM
  `Win32_Process.Create` fallback** (the `WinRmEnabler` plumbing). No SMB, no pushed binary.

## What shipped (Phase 1)

**Core (`source/Vivre.Core/Updates/`):** `UpdateSource` (+ `WuaServerSelection` mapping →
`ServerSelection` 2 / 3+`ServiceID` / 1), `SoftwareUpdate`, `PatchOptions` (Source · exclude-by-name ·
`RunBehavior` InstallNow/ScheduleAt · `RebootBehavior` ReportOnly/RebootAndWait · concurrency throttle ·
per-host timeout · poll interval/stuck threshold), `HostPatchStatus`/`PatchPhase`,
`IPatchService`/`PatchService` (WUA-only), and `WuaUpdateLane` (scan script, SYSTEM-task install worker,
register/poll/cleanup, DCOM fallback). The progress-JSON parser isolates the object by its braces, so
the UTF-8 BOM that Windows PowerShell 5.1's `Set-Content -Encoding UTF8` prepends is handled.

**Model (`Models/Computer.cs`):** new `[ObservableProperty]` fields — `UpdateMessage`, `RebootMessage`,
`UpdateProgress`, `UpdatesAvailable`, `UpdatePhase`, `UpdateError`, `ScheduledAction`,
`ScheduledNextRun`. (Ping = existing `IsOnline`; Pending reboot = `RebootRequired`; Command messages =
`CommandResult`.)

**Desktop:** per-tab **`IsUpdateMode`** toggle in `WorkspaceView` flips between the Machines grid and a
new **Windows Update grid** over the same list, with columns **Name · Ping · Reboot message · Windows
update message · Progress · Scheduled task action · Scheduled task next run time · Pending reboot ·
Command messages**, a patch command bar (Source toggle + Exclude box + Scan/Install), and a right-click
**Updates ▸**. `UpdateSourceNameConverter` gives the Source toggle friendly labels. `WorkspaceViewModel`
gained `ScanUpdates`/`InstallUpdates` commands on a `SemaphoreSlim` throttle + per-host timeout, reusing
the existing Stop-race. `App` constructs `PatchService` + a shared session-only `PatchOptions`.

**Tests (`source/Vivre.Core.Tests/Updates/`):** source→`ServerSelection` mapping, exclude filter, scan
parse, progress-JSON parse, scan over the `FakeHost` double.

## Deferred

- **Phase 2** — reboot-and-wait (poll `WmiHostProbe.CanReachAsync`); **scheduled** install/reboot as a
  one-time SYSTEM task with the two **Scheduled task** columns read back from the target; per-host
  update-detail window; per-host "All messages" (activity log filtered by machine).
- **Phase 3 (optional)** — an SCCM update-install lane (`SccmUpdateLane`, `UpdateEvaluationState`),
  only if updates ever get deployed through SCCM.

## Verify live (on a real target SERVER — can't be driven from the build box)

1. **Step 0 source probe** — registry `HKLM\…\WindowsUpdate` (`WUServer`, `UseWUServer`,
   `DoNotConnectToWindowsUpdateInternetLocations`, `AllowMUUpdateService`) + a manual WUA search per
   `ServerSelection`; confirms which source returns updates → sets the toggle default and whether a
   "Managed (WSUS)" default is needed.
2. **Scan** — flip to Windows Update mode, pick a source, Scan → a count appears; Windows Update vs
   Microsoft Update differ (SQL/Office/.NET appear only under Microsoft Update); add "SQL" to Exclude
   and confirm it drops out.
3. **Install** — Install drives Downloading→Installing (progress bar moves)→PendingReboot; row shows
   "Installed N, reboot required". Confirm the SYSTEM task is created + cleaned up, the re-run is
   idempotent, and Stop mid-install frees the grid.
4. **Known live risk:** a network-logon user running a Microsoft-Update-*online* scan may hit a
   double-hop — if so, route the scan through the SYSTEM-task path too.

## Critical files

`source/Vivre.Core/Updates/*` (new); `Models/Computer.cs`; `Desktop/ViewModels/WorkspaceViewModel.cs`,
`Desktop/WorkspaceView.xaml(.cs)`, `Desktop/UpdateSourceNameConverter.cs`, `Desktop/App.xaml.cs`;
`scripts/Windows Update/{Count missing updates,Download and install updates}.ps1` (extended with source
selection + exclude + progress-JSON writes); `source/Vivre.Core.Tests/Updates/*`.
