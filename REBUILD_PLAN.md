# Vivre (formerly Collection Commander) — Rebuild Plan

**Audience: Claude Code.** This document is the single source of truth for the rebuild. Update it when decisions change.

---

## 0. Current status — READ THIS FIRST (updated 2026-05-28)

This block is the resume point. Whoever picks this up next: start here, then continue from **NEXT**.

- 🚀 **Install/Uninstall streaming refactor — single persistent WinRM session (committed 2026-05-28, `d29479e`, 95 tests green).** Major architectural change after a session of production failures (silent "Installed 0 updates", `The type initializer for 'System.Management.Automation.Runspaces.InitialSessionState' threw an exception`, 15-minute hangs on "Searching for updates"). **Root cause was the previous per-poll WinRM-shell pattern** — `RunWorkerTaskAsync` opened one shell for bootstrap, then N more shells (one per progress poll, every `PollInterval`s) over the entire install duration, then one for cleanup. A 30-min install with 2s polls = ~900 sequential shells, hitting **`MaxShellsPerUser` (default 30)** — which surfaced as the `InitialSessionState` type-initializer error, silent stalls, and "shows nothing" UX. **User specifically rejected sync-mode-only operation; needs BatchPatch-like live progress.**
  - **New architecture (one persistent shell per install, streaming):** `IPowerShellHost.RunRemoteStreamingAsync` (new method) pre-allocates a `PSDataCollection<PSObject>` and subscribes to `DataAdded`, so each `Write-Output` on the server reaches the client the moment it's written. `BuildBootstrapScript` became the **whole server-side controller**: register SYSTEM task → start it → position-tracked tail of the append-only progress log (`[System.IO.File]::Open(...FileShare.ReadWrite)` + `Seek($lastLen)`) → emit each new JSON line via `Write-Output` → exit on terminal `Done`/`Error`/`PendingReboot` line → **`finally` block unregisters task + removes temp files** (so a client `ps.Stop()` cancel still cleans up server-side). `RunWorkerTaskAsync` collapsed `PollAsync` + `StartTaskAsync` + `CleanupAsync` + `StartTaskViaDcom` + `BuildPollScript` + `BuildCleanupScript` into one streaming call.
  - **Worker progress is now append-only JSONL** (`Add-Content` one JSON object per line) instead of `Set-Content` + `Move-Item` overwrite — the tail reads only new bytes since last iteration, so no race with mid-overwrite reads.
  - **15s-silence heartbeat** emits a synthetic `"phase":"Heartbeat"` line; **client filters them** before parsing so they don't regress UI phase (Heartbeat would otherwise map to Scanning via `MapPhase`'s unknown-phase fallback, dropping UI from "Installing 1 of 5 — 50%" back to "Searching"). 2-min worker-startup deadline emits a synthetic `Error` line if the worker task never starts writing.
  - **Removed:** DCOM `Win32_Process.Create` fallback (streaming needs WinRM end-to-end), `BuildPollScript`, `BuildCleanupScript`, `TaskGoneMarker` constant, the `Microsoft.Management.Infrastructure` Core imports.
  - **STILL OPEN — live byte-level download progress.** This refactor fixes the *reliability* problem but does NOT add bytes/percent during the actual WUA download. `BeginDownload($null, $null, $null)` in the worker is unreliable (PS can't supply real `IUnknown` callbacks; an `Add-Type` C# CCW shim was attempted in commit `1cabb9c` and **reverted in `78008ab`** after a 15-min hang on `$searcher.Search()` post-Add-Type — couldn't prove the managed CCW registration wasn't interacting badly with WUA's COM machinery on the target). Current behavior: sync `Download()` fallback shows `"Downloading 1 of N (sync mode — no live progress)"`; the streaming heartbeats keep the channel visibly alive but **user does NOT see bytes/percent during the download itself**. **Planned next iteration: `Start-ThreadJob` for sync `$downloader.Download()` in background + `Get-BitsTransfer` polling in foreground for live bytes** — gets real byte progress without needing WUA's callback API at all, and bytes flow back through the now-in-place streaming infra instantly. (~1.5 hr.)
  - **Earlier-in-session symptom-patches (kept; not load-bearing for the architecture fix but defensive):** per-iteration try/catch around scan rows (`1888775`) and install/uninstall iterations (`41a865e`); sync `Download()`/`Install()`/`Uninstall()` fallback when `Begin*` returns null (`5305430`); per-row `installedAt` lookup try/catch (`abcffbb`); date-installed column on the Installed-scope side-panel checklist (`be51ca9`). **The Add-Type WUA CCW shims (`1cabb9c`) were reverted in `78008ab` — do NOT reintroduce that path unless the BITS-polling approach is exhausted.**
  - **Bug the user noticed mid-session** (resolved by reboot, not code): `InitialSessionState` type-initializer error was correlated with a pending reboot on the target server — clearing it required rebooting the box. Plausibly: pending-reboot state corrupted WinRM/PSRP shell init. The streaming refactor side-effect-fixes this too (one shell vs hundreds), so the failure window shrinks dramatically.
  - **NEXT (live-verify the streaming build BEFORE doing BITS-polling work):** drop `publish\Vivre-win-x64.zip` on the workstation, restart Vivre, click **Install on NYC-FP1** and on **the other server that hung in the previous build** — both should now show streamed progress lines as the worker writes them (and the `InitialSessionState` / silent "Installed 0" / 15-min-hang failure modes should be gone). Then: BITS-polling for byte progress during sync Download. **Recent commit chain (newest first):** `d29479e` (streaming refactor) · `78008ab` (revert Add-Type) · `1cabb9c` (Add-Type CCW — REVERTED) · `5305430` (sync fallback) · `41a865e` (per-iter try/catch install worker) · `1888775` (per-iter try/catch scan) · `abcffbb` (installedAt try/catch) · `be51ca9` (Installed-date column).

- 🛠 **Windows Update (WUA) lane — Phase 1 built (2026-05-27, branch `feature/wua-update-lane`; Windows build + 86 tests GREEN 2026-05-27, PENDING LIVE-SERVER VERIFY).** The BatchPatch replacement (backlog **A1**), WUA-first because the user's SCCM admin deploys nothing (so the SCCM `InstallUpdates` lane installs nothing — deferred). New `Vivre.Core/Updates/`: `UpdateSource` (+`WuaServerSelection` mapping 2/3+ServiceID/1), `SoftwareUpdate`, `PatchOptions` (Source · exclude-by-name · run/reboot behavior · throttle · per-host timeout), `HostPatchStatus`/`PatchPhase`, `IPatchService`/`PatchService`, and `WuaUpdateLane` — **Scan** runs a search script over WinRM (read-only); **Install** writes a worker to `C:\Windows\Temp` and runs it from a **one-time SYSTEM scheduled task** (WUA install dies in a WinRM network-logon → `WU_E_NO_INTERACTIVE_USER`), the worker writes a progress JSON the controller **polls over WinRM** (WinRM-first register/start, **DCOM `Win32_Process.Create` fallback** reusing the `WinRmEnabler` plumbing), then deletes task+files. `Computer` gained update fields (UpdateMessage/RebootMessage/UpdateProgress/UpdatesAvailable/UpdatePhase/UpdateError/ScheduledAction/ScheduledNextRun). Desktop: per-tab **`IsUpdateMode`** toggle in `WorkspaceView` flips between the Machines grid and a new **Windows Update grid** (Name · Ping · Reboot message · Windows update message · Progress bar · Scheduled task action · next run · Pending reboot · Command messages) over the same list, with a patch command bar (Source toggle + Exclude box + Scan/Install) and a right-click **Updates ▸**; `WorkspaceViewModel` got `Scan/InstallUpdates` commands on a `SemaphoreSlim` throttle + per-host timeout reusing the Stop-race; `App` constructs `PatchService` + a shared session-only `PatchOptions`. Tests: `Vivre.Core.Tests/Updates/WuaUpdateLaneTests` (source mapping, exclude filter, scan parse, progress-JSON parse, scan-over-fake-host). **NEXT (verify live): on a real target SERVER (full update access, BatchPatch works there) — Scan shows a count, Win-Update vs MS-Update differ, exclude "SQL" drops out; Install drives Downloading→Installing(progress bar)→PendingReboot and the SYSTEM task is created+cleaned up.** (Windows `dotnet build source\Vivre.slnx` + `dotnet build source\Vivre.Desktop` + `dotnet test` all green 2026-05-27; added an `UpdateSourceNameConverter` for friendly Source-toggle labels during the desktop pass; the branch was landed from the cloud `wuaupdatelane.bundle` and a PR is open. **UX upgrade (2026-05-27, after a first hands-on look):** the mode switch is now a *Machines | Windows Update* **segmented control** (two RadioButtons via `InverseBooleanConverter`), and Windows Update mode gained a **per-machine update checklist** — a master-detail side panel listing the focused row's scanned updates with checkboxes (retained on `Computer.ScannedUpdates`/`SelectableUpdate`, re-scan preserves unticks by KB); Install scopes to the ticked KBs via `PatchOptions.IncludeKbArticleIds` (+`Clone()` for concurrency-safe per-host scope), honored by the worker's filter, with the typed Exclude box still fleet-wide. Build + 89 tests green; user wants to live-test before further UI tweaks.) Phase 2 = reboot-and-wait + scheduled-task scheduling (the two schedule columns) + per-host detail window.
- 🪪 **RENAMED TO VIVRE (2026-05-27)** — the app is now **Vivre** (One Piece *Vivre Card* lore; every grid row tracks one machine's life force). Full rename: `CMCollCtr.*` → `Vivre.*` (namespaces, projects), solution `source/Vivre.slnx`, output assembly **`Vivre.exe`** (`AssemblyName=Vivre`, namespaces stay `Vivre.Desktop`). New **Help ▸ About Vivre** dialog (`AboutWindow`) with the name story + signal→card table. Per-user data moved to `%APPDATA%/%LOCALAPPDATA%\Vivre`, migrated from the old `CMCollCtr` folder on first run (old kept as backup; `App.TryMigrateLegacyData`). Build/test/Vivre.exe verified, migration verified, 72 tests green. **Throughout this doc's history, `CMCollCtr` = the old name** — paths/names in build instructions are now `Vivre.*`.
- ✅ **Post-rename polish (committed 2026-05-27):** global unhandled-exception net in `App` (`DispatcherUnhandledException` → log + `Handled=true`, plus `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException`) so nothing dies silently; relative "Last reboot" refresh (60 s `DispatcherTimer` in `MainWindow`); grid tweaks (Name auto-width, Command result moved to 3rd col after Online); Paste-list button height fix. New **Vivre logo** (`AppIcon.png` + multi-size `AppIcon.ico`) and the About **hero background** (`AboutBackground.png`). Commits `6fa5936`, `f459ed6`, `286f6f8`. **Working tree clean; latest publish = `publish\Vivre-win-x64.zip`.**
- 🏁 **CUTOVER DONE (2026-05-26)** — the **legacy app is deleted** (`source/CMCollCtr` + `source/plugin.collctr.*` + the throwaway `spikes/`). `source/` now holds only the rewrite (`CMCollCtr.New.slnx`: Core + Desktop + Tests) plus `tools/RemoteRun`. The bundled PowerShell scripts were **preserved** to `scripts/` (since **curated** — see the scripts bullet below). Docs refreshed: `CLAUDE.md`, `README.md`, new `CHANGELOG.md`. **§9–§13 below describe code that no longer exists** — kept as historical reference / migration notes only. 58 tests green.
- ✅ **Phase 0** — logging + audit fixes in the legacy app (see §10).
- ✅ **Spike #1** — PowerShell SDK **7.6.2** confirmed hosting on **.NET 10** (`spikes/ps-remoting/`). Object pipeline + error capture work. Commit `3ea894c`.
- ✅ **Session 1 — scaffold** — `source/CMCollCtr.New.slnx` with `CMCollCtr.Core` (net10.0), `CMCollCtr.Desktop` (net10.0-windows, WPF-UI Fluent shell + working light/dark toggle), `CMCollCtr.Core.Tests` (xUnit, 1 test green). Builds clean. Commit `7e8965f`.
- ✅ **Session 2 — models + grid** — `CMCollCtr.Core/Models/Computer.cs` (`ObservableObject`; Name · IsOnline · SiteCode · AgentVersion · LastStatus · LastError) and `Credential.cs` (Domain · UserName · Description · computed `DisplayName`; **no secret yet** — DPAPI deferred). `CMCollCtr.Desktop/ViewModels/MainViewModel.cs` + `MainWindow.xaml` host a WPF-UI `DataGrid` (six columns), `DataContext` = `MainViewModel`. Builds clean, launches & renders. Convention: `[ObservableProperty]` uses the **partial-property** form (C# 13+, no analyzer suggestion). Commit `b1d437c`.
- ✅ **Session 3 — ping** — `CMCollCtr.Core/Net/`: `PingResult` (record), `IHostPinger`, `HostPinger` over `System.Net.NetworkInformation.Ping` (the .NET 8+ `SendPingAsync(host, TimeSpan, …, CancellationToken)` overload; resolution/timeout → offline, **only cancellation throws**). `MainViewModel.Refresh` is now `[RelayCommand(IncludeCancelCommand = true)]` async — generates `RefreshCommand` + `RefreshCancelCommand`; pings all rows via `Task.WhenAll` honoring the token and writes back `IsOnline`/`LastStatus`/`LastError`. UI gained a **Stop** button (→ `RefreshCancelCommand`) + a busy `ProgressRing` bound to `IsBusy`. Host list is still seeded test data (machine name, localhost, 127.0.0.1, plus TEST-NET `192.0.2.1` → TimedOut and `*.invalid` → DNS failure). **18 tests green**; verified live (3 online, 2 offline with distinct errors). The §8.1 cancellation bug is now structurally impossible.
- ✅ **Session 4 (local) — PowerShell host** — added **Microsoft.PowerShell.SDK `7.6.*`** to `CMCollCtr.Core` (resolves 7.6.2; was deferred since scaffold). `CMCollCtr.Core/PowerShell/`: `PSExecutionResult` (record: Output `IReadOnlyList<PSObject>` · Errors · Warnings · HadErrors), `IPowerShellHost`, `PSRunspaceHost`. `RunLocalAsync` opens a per-call local runspace, `await ps.InvokeAsync()`, captures the error/warning streams (the silent-failure fix, §8.2), and is **cancellable** — the token calls `ps.Stop()` and the resulting `PipelineStoppedException` is translated to `OperationCanceledException`. Desktop still builds & launches with the SDK in the graph. **Not yet DI-wired / no UI** — deliberate; nothing consumes the host yet. Commit `ccb4ed9`.
- ✅ **Session 4 (remote) — VERIFIED against `NYC-FP1` (2026-05-26)** — `IPowerShellHost.RunRemoteAsync(host, script, PSCredential?, port=5985, useSsl=false, ct)` over WinRM `WSManConnectionInfo`; `null` credential = current Windows login, else connect as the given account. Bounded `OpenTimeout` (20 s). Local/remote share one private invoke+capture+cancel helper. Added dev runner **`tools/RemoteRun`** (console, references real Core; args `<host> "<script>" [--user DOMAIN\u] [--port n] [--ssl]`, masked password prompt, prints output+streams+troubleshooting hints). **32 tests green** (no-network remote arg-guard tests). **Live result:** `dotnet run --project tools/RemoteRun -- NYC-FP1 "hostname; whoami" --user EMPLOYEES\admin_sbridges` → `NYC-FP1` / `employees\admin_sbridges`, `HadErrors=False`, ~5.4 s — i.e. it authenticated *as the supplied account* (not the current login) over a real WinRM hop. **The remote execution architecture is now proven**; explicit-credential remoting works.
- ✅ **Session 5 — SCCM client health check (verified live)** — `CMCollCtr.Core/Sccm/`: `SccmClientInfo` (record: ClientVersion · SiteCode · RebootRequired · MissingUpdates · RunningUpdates · UserLoggedOn · computed `IsHealthy`), `SccmQueryException`, `IConfigMgrClient`, `ConfigMgrClient`. **Decision: query via the verified PowerShell host, not `Microsoft.Management.Infrastructure`** (reuses the proven local/remote runspace; CIM stays an option for a later local perf pass). Embedded a **modernized CIM health script** ported from cm12 `HealthCheck.ps.txt` (`Get-WmiObject`→`Get-CimInstance`/`Invoke-CimMethod` for PS7; emits one typed object). `ConfigMgrClient` dispatches local vs remote by host name and throws `SccmQueryException` when no client. `MainViewModel` now pings then queries health under the **same** `CancellationToken`, filling Site code / Agent version / a health summary in LastStatus (graceful `Online · no ConfigMgr client` on failure). **37 tests green** (5 new, fake-host). **Live result on this box (has the client):** rows show Site code `TRC`, Agent version `5.00.9135.1013`, status `reboot required`; offline rows still TimedOut/DNS-fail. Two findings baked in: `SMS_Client.GetAssignedSite()` needs elevation ("Access denied") so site code is read from `SMS_Authority.Name` (`SMS:TRC` → `TRC`) which works unprivileged; `DetermineIfRebootPending` works unprivileged.
- ✅ **Session 6 — SCCM client actions (right-click menu)** — ported the cm12 plugin as **built-in actions**, not a reflection-loaded plugin framework (decision pending user input on whether a formal plugin system is wanted — see below). `CMCollCtr.Core/Sccm/ScheduleAction.cs`: `ScheduleAction` record + `ClientActions.All` (Machine Policy · Heartbeat · Hardware Inventory · Update Scan · Update Evaluation) with the **legacy GUIDs verbatim**. `IConfigMgrClient.TriggerScheduleAsync` runs `Invoke-CimMethod SMS_Client TriggerSchedule` via the PS host (local/remote dispatch), returns the completion message, throws `SccmQueryException` on failure. `MainViewModel` exposes `ClientActions`, tracks `SelectedComputers`, and has a `TriggerScheduleCommand` that runs the action against selected online rows. `MainWindow` grid has a right-click menu of the actions; opened from code-behind (`OnRowRightClick`) which selects the row, sets the menu's `DataContext` to the VM, and suppresses WPF-UI's default cell Copy/Cut/Paste menu. **39 tests green** (2 new; trigger returns message + carries the right GUID, error throws). Verified live: the themed action menu renders on right-click. (Firing a trigger needs elevation — Access denied unprivileged — so run the app elevated to actually trigger; the logic is unit-tested.)
- ✅ **#5 tabs + #1 DI (BUILT & VERIFIED).** Manual **composition root** in `App.xaml.cs` (no DI container): the shared singletons — `PSRunspaceHost`, `HostPinger`, `ConfigMgrClient`, `WinRmEnabler`, `ComputerListStore`, `CredentialStore` — are built once and injected (view models no longer `new` services). `MainViewModel` → **`WorkspaceViewModel`** (one per tab; adds `Title`); new **`ShellViewModel`** (ObservableCollection `Tabs`, `SelectedTab`, `NewTab`/`CloseTab` via injected `Func<WorkspaceViewModel>` factory, shared `Credentials`). Extracted the toolbar + add-bar + grid into a **`WorkspaceView`** UserControl (its own grid code-behind: selection, right-click menu, double-click→script, Delete, Copy, quick-add). `MainWindow` is now the shell: TitleBar + **File | Settings | New Tab** menu + **TabControl** (header = Title + ✕ close, double-click to rename, content = `WorkspaceView`). File menu acts on the active tab; theme/credentials app-wide. **Verified live:** app launches, Tab 1 renders full workspace, **New Tab → 2 independent tabs** (row added to Tab 1; Tab 2 grid empty). 58 tests green. **Deferred:** per-tab credential override + marker (app-wide creds for now); the MS.Extensions.DI container (manual root suffices at this size); close/rename are wired but click-verify pending (sandbox can't drive them). **Decision note:** realized #1 as a manual composition root rather than the MS.Extensions.DI package — same DI benefit (single wiring point, injected deps), far less ceremony; revisit the container if the graph grows. **Layout (user refinement):** shared toolbar + add-bar sit **above** the tab strip and act on the active tab; each tab holds **only the grid**; browser-style **"+"** new-tab button after the last tab.
- ✅ **Plugin scope decided (with user).** **No formal `IPlugin` architecture** — it was for third-party extensibility; this is a single-user tool, so features are built **directly in**. Per-plugin: **cm12 → done** (Session 6, built-in actions); **PsScript → done** (Session 7, below); **EnableWinRM → wanted** (user hits machines with WinRM off — queued next); **ClientCenter → dropped** (user doesn't use it); **localpsscripts & RuckZuck → dropped** (redundant / dead upstream).
- ✅ **Session 7 — PsScript (Run Script window)** — `CMCollCtr.Core/Scripts/`: `ScriptFile`, `IScriptLibrary`, `ScriptLibrary` (folder-backed at `%APPDATA%\CMCollCtr\Scripts`, seeds 2 examples, configurable dir for tests). Added **AvalonEdit** to Desktop with an embedded `Resources/PowerShell.xshd` (safe no-highlight fallback). `ScriptRunnerWindow` + `ScriptRunnerViewModel`: saved-scripts list (click loads into editor), highlighted editor, Save to library, **Run** against the grid's selected machines (local/remote dispatch via the verified host, current login), per-machine output log, cancellable. Opened from a MainWindow toolbar **"Run Script…"** button. **43 tests green** (4 ScriptLibrary). Verified live: window renders with highlighting; ran `Test-Path …RebootPending` → `False` locally on the 3 local aliases, graceful per-machine WinRM failure on the unreachable host.
- ✅ **Session 8 — EnableWinRM (DCOM) + context-menu rework** — added **`Microsoft.Management.Infrastructure`** to Core. `CMCollCtr.Core/Remoting/`: `IWinRmEnabler` + `WinRmEnabler` + `WinRmEnableException`. `EnableAsync(host, ct)` opens a **DCOM** `CimSession` (`DComSessionOptions`, current login) and invokes `Win32_Process.Create` running `powershell -NoProfile -ExecutionPolicy Bypass -Command "Enable-PSRemoting -Force"` — a channel that does **not** need WinRM (the point). Non-zero `ReturnValue` → `WinRmEnableException` with a decoded reason. **UX change (user request):** dropped the toolbar "Run Script" button; the grid's **right-click menu** is now the hub, built in code-behind: **Run Script… · ─ · [5 SCCM actions] · ─ · Enable WinRM (PSRemoting)…**. Enable WinRM confirms via a WPF-UI `MessageBox` first, then runs against selected rows. **47 tests green** (4 enabler arg/cancel guards). Verified live: the menu renders with all items; DCOM `CimSession` + query confirmed working on this box. **Not live-verified:** the actual `Enable-PSRemoting` (has side effects + needs DCOM reach to a real target; the sandbox blocks process-spawn) — user verifies from the jump box, like remoting against `NYC-FP1`.
- ✅ **Session 9 — session-only credentials + computer-list loader** — `CMCollCtr.Core/Credentials/`: `ConnectionCredential` (Domain · UserName · `SecureString` Password; `ToPowerShellCredential()`) + `CredentialStore` (in-memory `Current`; null = current Windows login). **Nothing persisted** (user chose session-only). Threaded the credential through **every** remote op: SCCM health + triggers (`PSCredential`), Run Script (`RunRemoteAsync`), and EnableWinRM (`WinRmEnabler` now takes the credential → `CimCredential` on `DComSessionOptions`). New **Settings window** (gear button): "Use my Windows login" vs "Use these credentials" (Domain/Username/PasswordBox; password read in code-behind, re-save without retyping keeps it). New **Load computers** window (toolbar): paste machine names, one per line → `MainViewModel.SetComputers` replaces the grid (trim/dedupe). `CredentialStore` shared from `MainViewModel.Credentials` into the Settings + Run Script windows. **53 tests green** (6 credential). Both windows verified live. So you can now: load real machines (e.g. `NYC-FP1`), set explicit creds, Refresh → everything uses them.
- ✅ **Script library curated + cascading "Run script" menu (BUILT & VERIFIED, 2026-05-26).** Replaced the 58 preserved legacy scripts (all `Get-WmiObject`/`[wmiclass]`/`[wmi]` — none of which exist in PS7, so every one would have errored on the host) with **30 modern PS7 scripts** (`Get-CimInstance`/`Invoke-CimMethod`) + a `scripts/README.md`, organised into 6 category folders: **Reboot · SCCM Client · SCCM Inventory & Updates · Windows Update · Repair · Info**. Added the bulk "do it everywhere" actions the user asked for: **Restart - force now** (the "Restart Force"), Restart-if-pending, Restart-warn-users-5min, Restart-cancel-pending, **Run all client actions**, Reset WU cache, Force GPUpdate, Uptime, Why-reboot-pending, OS info. Dropped dead/niche: all App-V 4.6, all SCOM, SLP/HTTP-port/DNS-suffix/site-code setters, the contoso-placeholder Install-CM-Agent, the broken Fix-DCOM-Permissions (`$converter` undefined), hardcoded-KB lookup, CCMEval, boundary-group, IsCacheCopyNeeded, etc. **Wiring:** `ScriptLibrary` now lists **recursively** and exposes `ScriptFile.Category` (relative folder); the curated `scripts/` ship with the Desktop app (`<Content Include="..\..\scripts\**\*.ps1" Link="Scripts\…">`, copied to output) and seed `%APPDATA%\CMCollCtr\Scripts` on first run **copy-if-missing** (never overwrites the user's own/edited scripts; `ScriptLibrary(dir, bundleDir)` ctor, falls back to the 2 inline examples when no bundle, e.g. in tests). `WorkspaceViewModel.ScriptLibrary` (shared singleton via the composition root) backs a new code-behind **"Run script ▸ [Category] ▸ [Script]"** cascading right-click menu in `WorkspaceView`; picking a leaf **opens the Run Script window pre-loaded** with that script + the current selection (review-then-Run, so no accidental bulk reboot on a stray click) — `ScriptRunnerWindow` got an optional `initialScript` param + takes the shared `IScriptLibrary`. **61 tests green** (+3: category from subfolder, bundle seeds categorised, bundle doesn't overwrite user edits). Verified live: app seeded 30 scripts into the 6 folders in `%APPDATA%` on launch (the 2 root examples left intact). *(Possible follow-up the user may want: have leaves **run directly** instead of opening the editor, and/or group the Run Script window's left-hand list by category.)*
- ✅ **Credentialed reachability — WMI/DCOM fallback for Ping All (BUILT & VERIFIED, 2026-05-26).** Resolved a recurring user point: ICMP carries no credentials, so "Ping All" could never go green for ping-blocked boxes no matter the creds. New `CMCollCtr.Core/Remoting/`: `IHostProbe` + `WmiHostProbe` (+ `ProbeResult` record) — a lightweight **authenticated** probe (`SELECT Name FROM Win32_ComputerSystem` over **WMI/DCOM** with the active `ConnectionCredential`, reusing the Enable-WinRM `DComSessionOptions`/`CimCredential` plumbing; 8s per-host timeout; returns the failure reason rather than a bare bool). `WorkspaceViewModel.PingRowAsync`: ICMP first → **only if it fails AND explicit credentials are stored** (`_credentials.Current is not null`), fall back to the WMI probe; **Online if either responds** (status is plain "Online" for both). On the default Windows login Ping All stays pure-ICMP (fast — no per-host timeout). WMI failures surface to the row's **Last error** (e.g. `WMI: The RPC server is unavailable` / `Access is denied`) so an un-revertable host is diagnosable. Injected via the composition root (`WorkspaceViewModel` gained an `IHostProbe` param). **65 tests green** (+4 probe arg/cancel guards). Verified live: with admin creds set, ping-blocked servers turn green via WMI; without creds, no fallback. *(User-tuned design: fallback gated on explicit creds; no "· WMI" status tag.)*
- ▶ **NEXT — remaining cross-cutting work (decisions being made with the user, one at a time):**
  - (1) **DI/composition root** — ✅ **AGREED, do it LATER.** A quiet refactor: build `HostPinger`/`ConfigMgrClient`/`PSRunspaceHost`/`ScriptLibrary`/`WinRmEnabler`/`CredentialStore` once at a composition root in `App.xaml.cs` and inject them, instead of `new`-ing in VM ctors + child windows (makes `CredentialStore` a real shared singleton rather than passed-by-hand). No behavior change. Schedule **before the app grows more windows**; not urgent.
  - (2) **Named machine lists** — ✅ **BUILT.** Opens with an **EMPTY grid** (removed `SeedComputers`). `CMCollCtr.Core/Computers/`: `IComputerListStore` + `ComputerListStore` — named lists as plain `<Name>.txt` (one machine per line, trimmed/deduped) in **`%APPDATA%\CMCollCtr\Lists\`** (editable/backup-able outside the app); List/Load/Save/Delete/Exists. **5 tests green.** `MainWindow` gained a **File menu** (built on open in code-behind): **New (clear)** · **Open list ▸** (saved lists) · **Save current as list…** (reusable `TextPromptWindow` for the name) · **Delete list ▸** (WPF-UI confirm) · **Paste computers…** · **Exit**. `MainViewModel`: `SavedLists/OpenList/SaveCurrentAsList/DeleteList`. **Verified:** empty-on-launch + File-menu presence (screenshots) + store unit tests; the menu *drop-down* couldn't be exercised in the sandbox (foreground/focus restriction — see the UI-automation note below), so give it a click to confirm. **No** auto-remember-last, **no** CLI args (deferrable). Rename-list not built yet (delete + re-save covers it for now).
  - (3) **Activity log — in-app panel + file** — ✅ **BUILT & VERIFIED.** `CMCollCtr.Core/Logging/`: `LogEntry` (Timestamp · Severity · Machine? · Message), `LogSeverity`, `IActivityLog` (ObservableCollection Entries + Info/Warn/Error + Clear). Desktop `ActivityLog` (singleton from the composition root): newest-first, capped at 2000, Dispatcher-marshalled, also writes a **rolling daily Serilog file** at `%LOCALAPPDATA%\CMCollCtr\logs\cmcollctr-YYYYMMDD.log`. The shell hosts a **bottom panel** (GridSplitter-resizable) — `ActivityLogViewModel` exposes a filtered `ICollectionView`; the **search box matches machine OR message** (pinpoint a host / show failures), a Clear button, and a grid (Time · Machine · Message) **colour-coded by severity** (`LogSeverityBrushConverter`: info grey / warning amber / error red). `WorkspaceViewModel` logs ping/check/trigger/enable outcomes; `ScriptRunnerViewModel` logs per-machine script runs (it receives the log via the workspace). The log is **global across tabs** (one history). Verified live: Check All produced "Health: reboot required" (info) and a 192.0.2.1 "Check failed — WinRM…" (warning) in both the panel and the file. **(2026-05-26) The panel is now hidden by default and toggled from a new `View ▸ Activity log` checkable menu item** (between File and Settings); `MainWindow` collapses the splitter + panel rows when off and restores the last height when on (`OnToggleActivityLog`). The rolling file always logs regardless of panel visibility. Packages: `Serilog` + `Serilog.Sinks.File` (skipped Microsoft.Extensions.Logging — the VM logs directly to `IActivityLog`).
  - (3-orig) ~~Activity log — in-app panel + file~~ — ✅ **AGREED (user wants a rich panel, not just a file).** An **in-app log/history panel** listing every action as it happens: **Time · Machine · Action · Result** (success + failures with the error), **color-coded by severity**, with a **search box (free text)** and **filter by machine** (pinpoint one host / show only failures). **Plus** a persisted **log file** via Serilog at `%LOCALAPPDATA%\CMCollCtr\logs\cmcollctr-YYYYMMDD.log` (matches the legacy path / §3). Design: Core `LogEntry` (Timestamp · Severity · Machine? · Message) + `IActivityLog`; a Desktop `ActivityLog` impl holding an `ObservableCollection<LogEntry>` (bound through an `ICollectionView` for search/machine filter) that also writes to the Serilog file. The VM logs at each step (ping/health/trigger/script/enable) since it knows the machine + outcome; Core services stay log-free. Packages per §3: `Microsoft.Extensions.Logging` + `Serilog.Extensions.Logging` + `Serilog.Sinks.File`. Panel placement (collapsible bottom dock vs toggled view) decided at build.
  - (4) **Polish** — ✅ **BUILT.** Run Script editor now uses theme brushes (`CardBackgroundFillColorDefaultBrush` / `TextFillColorPrimaryBrush` via DynamicResource) so it follows light/dark instead of a hardcoded dark box; the output pane auto-scrolls to the newest line (`TextChanged → ScrollToEnd`). Theme picker (Light/Dark/System) already lives in the Settings menu (done with the menu rework). *(Note: the PowerShell .xshd highlight colours are dark-tuned; readable but could be refined for light theme later.)*
  - (5) **Tabbed workspaces** — ✅ **AGREED (architectural reshape).** The window becomes **tabbed**: each tab is an **independent workspace** with its **own machine list, selection, and operations**, able to run **concurrently** (async design already supports it). Shared top toolbar acts on the **active** tab. **New tab**, **close tab**, and **rename via double-click** the header (custom names like `BreachList`, not `Tab 2`). Refactor: today's `MainViewModel` → a per-tab `WorkspaceViewModel`; add a `ShellViewModel` holding `ObservableCollection<WorkspaceViewModel>` + New/Close/Rename + selected tab; `MainWindow` hosts a `TabControl` (editable headers). **Do this together with DI (#1)**, and **before** the log panel (#3) + icon columns (#6) so those are built tab-aware. ✅ **DECIDED:** **theme is app-wide**; **credentials are app-wide by default** (one shared `CredentialStore` from Settings, used by every tab), **but a tab may override with its own credentials** — resolution per op is **tab-override → app-wide → current Windows login**. A tab using its own creds shows a **visual marker on its header** (badge/icon + tooltip naming the account). Mechanism to set a per-tab cred (e.g., right-click tab → "Use different credentials for this tab…") decided at build; reuse the Settings credential UI.
  - (6) **Status + health icon columns & last reboot** — ✅ **BUILT & VERIFIED (live).** `Computer.IsOnline` is now `bool?` (null=grey/unchecked, true=green, false=red); added `bool? RebootRequired/MissingUpdates/RunningUpdates/UserLoggedOn`, `DateTime? LastBootTime`, computed `LastRebootDisplay` (relative). `SccmClientInfo`/`ConfigMgrClient` health script now returns `LastBootUpTime`. Grid shows colored **dot columns** (Online · Reboot · Updates · Install · User) via `StatusBrushConverter` (green/red/grey, `problem` polarity for health; tooltips via `BoolStateTextConverter`) + a **Last reboot** column (relative text, exact on hover). `CheckRowAsync` populates them; `RemoveOffline`/`PingOffline` updated for the nullable. **Verified on the real client:** localhost/127.0.0.1/machine → green Online, red Reboot (pending), green Updates/Install/User, Last reboot "4h", site TRC. 58 tests green. *(Original agreed design, for reference:)* Replace the Online checkbox with a **status dot**: grey = not checked yet, green = online, red = offline. Add green(good)/red(attention) **icon columns** for **Reboot pending · Updates missing · Install running · Users online** — the health query **already returns all four** (`SccmClientInfo`: RebootRequired/MissingUpdates/RunningUpdates/UserLoggedOn); surface them per-column instead of the single LastStatus summary. Add **Last reboot** as relative text (`1d`, `3h`) with the **exact timestamp on hover** — requires adding `LastBootUpTime` (`(Get-CimInstance Win32_OperatingSystem).LastBootUpTime`) to the health CIM script + `SccmClientInfo` + `Computer`. ✅ **DECIDED:** **built-in colored icon glyphs** (not image files). Status colors come from **theme-aware brush resources** (e.g. `StatusGood` / `StatusBad` / `StatusUnknown`) defined per light/dark so green/red/grey stay crisp and legible in both modes.
  - (7) **Assets directory + app icon/logo + tray** — ✅ **BUILT.** `CMCollCtr.Desktop/Assets/` holds the logo: `AppIcon.png` (256² square — padded from the user's 1536×1024 `CollectionCommander.png`, centered, no distortion) embedded as a `Resource`, and `AppIcon.ico` set as `<ApplicationIcon>` (EXE/Explorer). Window icon (`Icon=AppIcon.png` → taskbar) + WPF-UI `ui:TitleBar` `ImageIcon` (in-app title bar). **System tray** via **`Hardcodet.NotifyIcon.Wpf`** (`ui:NotifyIcon` doesn't exist in WPF-UI 4.3) — `tb:TaskbarIcon` with the logo, tooltip, right-click **Open / Exit** menu, double-click to open. Verified: title-bar logo renders; app runs with the tray registered. **Deferred:** minimize-to-tray on close (kept simple — close still exits; can add if wanted).
  - (8) **Main-window overhaul → classic Collection Commander layout** — ✅ **AGREED (user-directed); supersedes the current toolbar + parts of #4.** Target layout:
    - **Menu bar: `File | Settings`** — make **Settings a menu next to File** (theme picker Light/Dark/System lives here per #4); **remove the toolbar Settings + "Toggle light/dark" buttons.**
    - **Big action buttons** (replace today's Refresh/Stop bar): **Ping All** (▾ **Ping Offline** = re-ping only offline rows) · **Check All** (= ping **+** SCCM health; today's Refresh) · **Remove Offline** (plain button — drop offline rows; **Remove Warning was rejected as useless**) · **Stop** (cancel running ops) · **Clear Results**.
    - **Per-row "Command result" column (NOT a panel).** ✅ **Q1 RESOLVED:** a script's output flows back to **each target machine's own row** (e.g. `write-host "hello $env:computername"` → "hello NYC-FP1" on the NYC-FP1 row). Cell shows a single line with **hover/expand for full multi-line output**; the double-click window is where full output is read comfortably. Store full text on `Computer.CommandResult`. **Clear Results** empties the column on all rows. The searchable activity log (#3) stays **separate** (action history, not raw output).
    - **Run Script entry points (3), all write output back to the rows:** **double-click a row** → editor scoped to **that one machine**; right-click **Run PowerShell Script** → **selected** rows; right-click **Run PowerShell (All Machines)** → **every** row. The `ScriptRunnerWindow` takes a target set; replace the single context-menu "Run Script…" with the two items above.
    - VM split: `Ping All` = ping-only sweep; `Check All` = ping+health sweep. Grid edit cmd: **RemoveOffline** only (✅ Q2 RESOLVED — Remove Warning dropped).
  - **▶ STEP A BUILT (commit pending):** #9 + the classic toolbar/menu. `MainViewModel` now has `PingAllCommand` (ping only) · `PingOfflineCommand` · `CheckAllCommand` (ping+health) · `StopCommand` (shared CTS) · `RemoveOfflineCommand` · `ClearResultsCommand`; sweeps gated by `IsBusy` via `NotifyCanExecuteChangedFor`. `CheckRowAsync` decouples from ICMP (always attempts health with the active credential; `SccmQueryException` ⇒ reached/online, transport error ⇒ fall back to ping). Dropped the `IsOnline` filter on `TriggerSchedule`. `Computer.CommandResult` added + a **Command result** column (single line + tooltip). `MainWindow`: menu bar **File | Settings** (Settings menu: Credentials… + Theme Light/Dark/System); toolbar **Ping All (▾ Ping Offline) · Check All · Remove Offline · Stop · Clear Results**; removed the old Refresh/Settings/Toggle/Computers toolbar buttons. 58 tests green; builds + launches; layout verified by screenshot (menu + buttons render).
    - **▶ STEP B BUILT:** three Run-PowerShell entry points → **double-click a row** (script window for that one machine), right-click **Run PowerShell Script** (selected), **Run PowerShell (All Machines)** (all). `ScriptRunnerViewModel` writes each machine's output to its own `Computer.CommandResult` (grid column updates live) as well as the window's combined log. **Grid-interaction fixes (user-reported):** removing the earlier custom `DataGridRow` style restored the **selection highlight** (the override had stripped WPF-UI's default visuals); right-click now handled at grid level (`OnGridRightClick` + visual-tree walk) so highlight is preserved; added **Delete key → remove selected rows** (`RemoveSelected`), **Copy** (Ctrl+C via `ClipboardCopyMode=IncludeHeader` + a Copy context-menu item → TSV of selected rows). Quick-add bar also refined to a compact single-add box + "Paste list…" button. Builds clean; grid interactions need hands-on confirmation (sandbox can't drive clicks).
  - (9) **Decouple manageability from ICMP (credentials take effect)** — ✅ **DONE in Step A (above).** **"Online" = responded** — ICMP **or** a successful WinRM/health reply (with the active credential). Stop skipping health + SCCM client actions on ICMP-offline rows (servers that block ping but answer WinRM must still work). So: change creds → **Check All** → previously-"offline" CM servers turn green + populate health. Tradeoff: genuinely-down hosts incur the WinRM open-timeout (~20s) during a Check All instead of being skipped instantly — acceptable for mostly-up server lists. Also broaden `ProbeAsync`'s health catch to all exceptions (not just `SccmQueryException`) and drop the `IsOnline` filter on the SCCM trigger command.
- ✅ **Continuous monitoring + Stop reliability + Run Script window polish (BUILT & VERIFIED, 2026-05-26).** Delivers parked idea (B) and several user-reported fixes:
  - **Continuous reachability monitoring** — `WorkspaceViewModel.IsMonitoring` (on by default per tab; bound to a toolbar **Monitor** toggle with a Pulse icon). A background loop re-checks every row's online/offline every **20 s** via a shared `ProbeReachabilityAsync` (ICMP, then the credentialed WMI probe when explicit creds are stored), and newly-added rows are checked **immediately** on add. Quiet by design: updates the dot every pass but only rewrites status / logs on a **state change** (so no log spam and it won't clobber a Check All health summary). Pauses while a manual sweep runs; **Stop** halts it (and the Monitor toggle restarts it); closing a tab stops its loop (`ShellViewModel`).
  - **Stop now actually stops a hung Check All** — root cause: `PSRunspaceHost.RunRemoteAsync` blocked on `runspace.Open()` (WinRM connect) which ignores the token until the 20 s `OpenTimeout`, so a rebooting host hung the sweep and the busy ring. Fixed two ways: `RunRemoteAsync` waits on Open with `WaitAsync(token)` (and disposes the half-open runspace to abort the connect); `RunSweepAsync` races the work against cancellation so Stop frees the UI instantly. In-flight rows flip to "Cancelled".
  - **Run Script window** — saved-scripts list is now **grouped by category** (folders, like the right-click menu) into **collapsible** Expanders (default **collapsed**, showing item counts), the left panel is **resizable** (GridSplitter) and wider; **Save into a folder** (editable "Folder" combo — pick an existing category or type a new one to create it; `IScriptLibrary.Save(name, content, category)` with path-traversal-safe sanitising); and **Delete** a saved script (built-in **defaults are protected** — `IScriptLibrary.IsDefault`/`Delete`, determined by matching the shipped bundle; empty user folders are tidied up). **72 tests green** (+ category/save/delete/default-guard coverage).
- 💡 **PARKED IDEAS — raised by user, NOT scheduled (captured 2026-05-26):**
  - **(A) Absorb BatchPatch and retire it.** A lot of the day-to-day is *already* covered by the grid + curated scripts (run PowerShell one/selected/all, force/warn/cancel reboot, install updates via WUA **and** SCCM, services, logged-on user / uptime / disk / OS). The gaps that would actually *replace* BatchPatch:
    - **(A1) Structured Windows Update panel** — 🛠 **Phase 1 BUILT 2026-05-27 (branch `feature/wua-update-lane`, pending Windows verify) — see the top §0 bullet.** WUA-only lane (b); SCCM lane (a) deferred (admin deploys nothing). Live progress comes from a SYSTEM-task-written **progress JSON polled over WinRM** (not the `ps.Streams.Progress` idea below — the install runs detached as SYSTEM, so its Progress stream isn't visible to the controller). Scheduled-task columns + reboot-and-wait + per-host history = Phase 2. *Original spec retained below.* — scan → show per-host available-update **count/list in columns** → download / install / reboot with **per-host progress**, instead of today's text-returning scripts. Builds directly on the existing WUA + SCCM update code. *Highest value.* **Target columns (user, 2026-05-27), mapped to what exists:** Name ✅ · Ping ✅ (Online dot) · Pending Reboot ✅ (dot) · **Reboot Message** ⚠️ (have pending state; add a reboot-action status: *Rebooting… / back online in Xm*) · **Windows Update Message** ⚠️ (have Updates-Missing dot + scripts dumping to Command result; add a structured per-row status: *Searching → Downloading (3/8) → Installing → Installed N, reboot required*) · **Progress of update install** ❌ (live per-host progress — feasible via PowerShell `Write-Progress` captured from the host runspace's **Progress stream** `ProgressRecord.PercentComplete` pushed onto the row; the SDK exposes `ps.Streams.Progress`) · **Scheduled Task Action** ❌ (only meaningful once the A4 scheduler exists — the queued action for the host) · **All messages** ⚠️ (have a global activity log + per-row Last status/Last error/Command result; add a per-host message **history**, e.g. the activity log filtered by machine into a row expander). Net new build: the structured WU Message+Progress pair, a reboot-action status, the per-host message history, and (with A4) the scheduled-action column.
      - **Mechanism (researched 2026-05-27) — the key constraint is that WUA install will NOT run inside a WinRM session** (network-logon / double-hop → access denied / `WU_E_NOT_INTERACTIVE`). That's *why BatchPatch ships a SYSTEM agent* (it polls a temp file over SMB), and why PSWindowsUpdate's `Invoke-WUJob` registers a **scheduled task as SYSTEM** to escape it. So a scheduled-task/SYSTEM context is effectively **required** for the WUA install, not optional. **Two lanes for CC:**
        - **(a) SCCM-managed (most targets):** skip WUA — trigger `CCM_SoftwareUpdatesManager.InstallUpdates` (we already have this script); the **ConfigMgr agent runs as SYSTEM, no double-hop** — then **poll `CCM_SoftwareUpdate` ComplianceState/EvaluationState** per update for the status/progress column. Cleanest path.
        - **(b) Standalone / non-SCCM:** run the install from a **one-time SYSTEM scheduled task** whose worker writes **`cc_progress.json`** (phase · current update · % · counts) that CC **polls over WinRM** (`Get-Content`, ~3 s). No SMB needed (better than BatchPatch's ADMIN$ polling). Live % comes from this file (real WUA `IDownloadProgressChangedCallback` / `IInstallationProgressChangedCallback`).
        - **Audit/history feed (the "All messages" column):** poll the **`Microsoft-Windows-WindowsUpdateClient/Operational`** event log for phase transitions + success/failure. NB it gives phases, **not smooth %** — and the exact event IDs drift per OS, so verify them rather than hard-coding. (Reading event-log/JSON over WinRM is fine; only the *install* needs the SYSTEM task.)
        - **CC side:** a per-host polling loop (reuse the monitor's pattern) updates a `HostPatchStatus` the grid binds to → drives the Update Message + Progress columns.
    - **(A2) Reboot-and-wait** — reboot, **wait for the host to come back online** (the ICMP+WMI probe already detects return), then continue / verify. Mechanically easy with what we have.
    - **(A3) Concurrency throttle** — cap simultaneous hosts (`SemaphoreSlim` in the sweep, which currently fans out to all rows at once). Matters once lists get big.
    - **(A4) Scheduled reboot at a set time (e.g. 1 AM) + confirm back online** — user's real need. **How BatchPatch actually does it:** the BatchPatch console **stays running** and fires the reboot **live at the scheduled time** (app-resident scheduler/job queue), then verifies — it does **not** plant a fire-and-forget command on the target. User **dislikes a planted "random command,"** so match BatchPatch: an **in-app scheduler** that holds the job and, at the time, reboots + **waits-for-online** (A2). **Credential fit:** this stays inside session-only creds with **no persistence** as long as CC is left open (creds live in memory); the credential tension only appears if the app must be **closed**. *(Cruder fallback considered and rejected: issuing a bare `shutdown /r /t <secs>` on the target — fire-and-forget, no central control, cancel = `shutdown /a` per box. If a closeable option is ever wanted, create a proper **one-time Windows Scheduled Task on the target** via schtasks/CIM — cancellable in its Task Scheduler — not a raw shutdown timer.)*
  - **User's actual usage (scopes this):** *most* updates = **download + install now, then reboot later manually with a force-reboot** (interactive). *Some* = **schedule a reboot ~1 AM and verify it comes back online** (in-app scheduler, app left open). So this is an **interactive operator** need + **app-resident scheduled-reboot**, **not** lights-out unattended patching — keeping it inside the session-only-credential model (no stored creds required). A full **job sequencer/scheduler** for truly app-closed unattended multi-step runs is the *only* piece that would force revisiting the credential model, and likely isn't needed.
  - **(B) Continuous reachability monitoring.** ✅ **BUILT 2026-05-26** (see the monitoring bullet above). When pinging, keep **auto-polling online/offline on an interval** (re-checking each host) **until the user clicks Stop**, instead of a single sweep. Surfaces transitions live (came online / went offline) — exactly what's wanted to watch a 1 AM-rebooted box return (pairs with A2/A4). Design: a cancellable loop in the workspace re-running the ping (ICMP + credentialed WMI) per host on a timer, torn down by the existing Stop/CTS; likely a **"Monitor" toggle** distinct from one-shot "Ping All", optionally logging transitions to the activity log.
- 🔒 **Credentials behavior (locked requirement, user-confirmed):** the app must **never auto-prompt** for credentials. On launch it **defaults to the current Windows login** silently. The user supplies explicit credentials **only by choice**, via **Settings → "Use these credentials."** Credentials are **session-only and never persisted** — even once `settings.json` exists (item 2), it stores the computer list / non-secret settings but **never the credential**, so each launch starts clean on the Windows login. Per-machine stored creds / a Password Manager remain out of scope unless explicitly requested.
- 🟢 **No blockers.** Local PS, remote WinRM (`NYC-FP1`, `EMPLOYEES`), SCCM health, client-action triggers, Run Script, and the EnableWinRM wiring are all in; only the live `Enable-PSRemoting` itself is user-to-verify.
- 🛠 **Verification note for whoever automates the UI:** WPF-UI `ui:Button`s with a `Click` handler do **not** respond to UI-Automation `InvokePattern` (only `Command`-bound buttons do — e.g. Refresh works, Run/Save don't). Drive `Click`-handler buttons with synthesized mouse events, and **click twice** (first activates the window since `SetForegroundWindow` from a background process is restricted, second presses). Owned windows may not appear under `RootElement` children — find them via Win32 `EnumWindows` by title + pid. (Also saved as a memory.)
- 🧭 **Environment:** local git only (no remote, on `master`); .NET 10 SDK `10.0.300`; no Visual Studio / msbuild — build via `dotnet`.

> **Maintenance:** update this block (status bullets + NEXT + date) at the end of every working session, before the final commit. It is the contract for clean resumption.

---

## 1. What this app is

Collection Commander (CMCollCtr) is a Windows desktop tool for **managing Microsoft Configuration Manager (SCCM/MEMCM) clients at scale**. It presents a grid of computer names, pings them, runs SCCM client actions against them (machine policy refresh, hardware inventory trigger, software update scan, etc.), and provides a plugin model for additional actions.

Originally written by Roger Zander, migrated from CodePlex, and now being modernized for personal use by the current owner.

**Threat model:** single-user on their own machine, running against their own SCCM environment. Most "security" findings in the audit are not pressing — they would matter for an enterprise deployment but not here. Focus engineering effort on **usability, maintainability, and modernization**, not hardening.

---

## 2. Why we're rewriting

The current app is **.NET Framework 4.8 WinForms**, legacy `packages.config`, ~2012-era patterns:
- 2000-line `MainForm.cs` mixing UI, threading, business logic
- 80+ empty `catch { }` blocks (now mitigated — see §10)
- Third-party WinForms Ribbon control with no maintained equivalent
- `BindingSource` + `DataGridView` + `Application.DoEvents()`
- Reflection-based plugin invocation (`InvokeMember`)
- `Thread.Sleep(200)` race-condition workarounds
- Passwords "encrypted" using the domain name as the key
- Hardcoded SQL `sa` connection string in `App.Config` (dead code — see §11)

Goals of the rewrite:
1. **Modern look** — Fluent design, dark mode, rounded corners, proper high-DPI.
2. **Maintainability** — MVVM, async/await, dependency injection, structured logging.
3. **Future-proofing** — .NET 8 LTS, supported through Nov 2026, with an upgrade path to .NET 10 LTS.
4. **Cleaner plugin contract** — proper interface, no reflection.

---

## 3. Stack decisions (final)

| Concern | Choice | Why |
|---|---|---|
| Runtime | **.NET 10** (Windows-only) | Current LTS (Nov 2025 → ~Nov 2028). Chosen over the plan's original .NET 8, which is only supported to Nov 2026. WPF runs first-class. |
| UI framework | **WPF** | Mature DataGrid, full designer in VS, MVVM-ready, can host WinForms during transition if needed. WinUI 3 was considered and rejected — DataGrid story is rough and tooling is younger. |
| Styling | **[WPF-UI](https://github.com/lepoco/wpfui)** | Fluent design / WinUI look on WPF foundations. Dark mode, Mica, rounded controls. Actively maintained. |
| MVVM | **[CommunityToolkit.Mvvm](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/)** | Source-generated `ObservableObject`, `RelayCommand`. No reflection, no boilerplate. |
| DI | **Microsoft.Extensions.DependencyInjection** | Standard, lightweight, plays well with everything else. |
| Logging | **Microsoft.Extensions.Logging** + **Serilog** sink (file + optional console) | Structured logs to `%LOCALAPPDATA%\CMCollCtr\logs\`. Replaces the homegrown `Logging.cs` from Phase 0. |
| PowerShell hosting | **Microsoft.PowerShell.SDK** (7.6.x on net10) | PS 7+ runspace. Replaces the legacy `System.Management.Automation 3.0` reference. Verified working on net10 via Spike #1 — see below. |
| Code editor (PS script plugins) | **AvalonEdit** | Syntax-highlighted PowerShell editor as a WPF control. |
| WMI / CIM | **Microsoft.Management.Infrastructure** (CIM) | Modern WS-Man-based replacement for `System.Management`. Async-friendly. Falls back to DCOM if needed. |
| Settings | **JSON in `%APPDATA%\CMCollCtr\settings.json`** | Replaces the legacy `Properties.Settings.Default` / `user.config` mess. |
| Credentials | **Windows DPAPI** (`ProtectedData.Protect` with `CurrentUser` scope) | Replaces the broken domain-name-as-key "encryption" pattern. |
| Tests | **xUnit + FluentAssertions** | Standard. Worth having for the Core library at minimum. |

> **Note:** the original plan said `.NET 8` / `net8.0` throughout (§4, §14, §17). The runtime
> decision is now **.NET 10**. The remaining `net8.0` literals in this doc are stale; treat them as
> `net10.0` and correct them at scaffold time.

### Spike #1 findings (PowerShell SDK on .NET 10 — `spikes/ps-remoting/`)
- ✅ `Microsoft.PowerShell.SDK 7.6.2` (stable GA) restores and runs on `net10.0`. Hosted engine
  reports PS `7.6.2`. The version-coupling concern (net10 SDK possibly prerelease) is resolved.
- ✅ Runspace creation, the object pipeline, and **error-stream capture** all work — the silent
  failures from the old app are visible by default now.
- 🔎 **The dev box has the ConfigMgr client installed** (`ROOT\ccm:SMS_Client` exists; the
  `TriggerSchedule` call returns *"Access denied"*, not *"class not found"*). So the SCCM-specific
  call can be exercised **locally when run elevated** — we don't need a remote target to de-risk it.
- ⏳ **Still unverified:** remote runspace over WinRM with explicit `PSCredential` (the actual
  `sccmclictr.automation` replacement). Stubbed as `RunRemote()`. Must pass before Phase 4.

---

## 4. Solution layout (recommended)

```
source/
├── CMCollCtr/                    # OLD — leave intact during migration, delete at cutover.
├── CMCollCtr.Core/               # NEW — non-UI logic. Targets net8.0. No UI references.
│   ├── Models/                   # Computer, PingResult, HealthStatus, PWEntry, etc.
│   ├── Plugins/                  # IPlugin contract, plugin discovery, MEF or manual loader.
│   ├── PowerShell/               # PSRunspaceHost — wraps Microsoft.PowerShell.SDK.
│   ├── Sccm/                     # ConfigMgrClient — wraps CIM calls to ROOT\ccm.
│   ├── Credentials/              # DpapiPasswordStore — replaces sccmclictr.automation.common.Encrypt.
│   └── Settings/                 # AppSettings + JSON loader.
├── CMCollCtr.Desktop/            # NEW — the WPF app head. Targets net10.0-windows.
│                                 #   NOTE: named .Desktop, NOT .Wpf. A root namespace
│                                 #   ending in `.Wpf` shadows WPF-UI's top-level `Wpf.*`
│                                 #   namespaces in generated XAML code-behind (CS0234).
│   ├── App.xaml + App.xaml.cs    # Composition root (DI container, logging init).
│   ├── Views/                    # MainWindow.xaml, SettingsWindow.xaml, PasswordManagerWindow.xaml.
│   ├── ViewModels/               # MainViewModel, ComputerViewModel, etc.
│   ├── Controls/                 # Reusable UserControls.
│   └── Resources/                # Icons, themes, styles.
└── plugins/                      # NEW — each plugin is its own project.
    ├── CMCollCtr.Plugin.Cm12/    # SCCM client actions context menu.
    ├── CMCollCtr.Plugin.WinRm/   # Enable PSRemoting on a target.
    ├── CMCollCtr.Plugin.PsScript/  # Run-arbitrary-PS plugin (consolidates the two old PS plugins).
    └── CMCollCtr.Plugin.ClientCenter/  # Launches Roger Zander's SCCM Client Center exe (sccmclictr).
```

Plugins reference `CMCollCtr.Core`, NOT `CMCollCtr.Desktop`. The desktop app discovers plugins at runtime via the `IPlugin` interface.

> **Scaffold status (Session 1 — done).** Solution `source/CMCollCtr.New.slnx` (the new `.slnx`
> format is the .NET 10 default) holds `CMCollCtr.Core` (net10.0), `CMCollCtr.Desktop`
> (net10.0-windows, WPF-UI 4.3 Fluent shell with a working light/dark toggle), and
> `CMCollCtr.Core.Tests` (net10.0, xUnit — 1 smoke test passing). Builds clean, legacy app
> untouched. **Packages deliberately deferred** until the session that needs them, to keep the
> first build lean: `Microsoft.PowerShell.SDK`, `Microsoft.Management.Infrastructure`,
> `System.Security.Cryptography.ProtectedData` (DPAPI), `AvalonEdit`, and the DI/Serilog stack.
> **FluentAssertions is NOT added** — v8 (2025) switched to a paid commercial license; decide
> later whether to pin the last MIT release (7.x) or stay on plain xUnit asserts.

**Skip** the RuckZuck plugin unless explicitly requested — the upstream service (`http://ruckzuck.azurewebsites.net`) hasn't been updated in years and is likely dead.

---

## 5. Plugin contract (proposed)

Replace the current reflection-based `InvokeMember("EnableCMRows", ...)` pattern with a clean interface:

```csharp
namespace CMCollCtr.Core.Plugins;

public interface IPlugin
{
    string Name { get; }
    string Description { get; }

    /// <summary>Items shown in the right-click context menu of the computer grid.</summary>
    IReadOnlyList<IPluginAction> ContextMenuActions { get; }
}

public interface IPluginAction
{
    string Label { get; }
    string? IconPath { get; }

    /// <summary>Invoked when the user clicks this action against the selected computers.</summary>
    Task ExecuteAsync(
        IReadOnlyList<Computer> targets,
        IPowerShellHost powerShell,
        IPluginContext context,
        CancellationToken ct);
}
```

`IPluginContext` exposes logger, credential lookup, settings, and a way to write results back to the grid.

`IPowerShellHost` is the runspace abstraction in `CMCollCtr.Core.PowerShell`.

---

## 6. Phased plan

| Phase | Deliverable | Effort (est.) | App usable during? |
|---|---|---|---|
| **0** | Logging + audit fixes in old WinForms app. **Mostly done.** | ~4h | Yes |
| **1** | SDK-style csproj migration of the old app (still targets net48). Lets `dotnet build` work. | ~8h | Yes |
| **2** | Stand up `CMCollCtr.Core` and `CMCollCtr.Wpf`. MainWindow shell with WPF-UI styling, settings, theme switching. No real features yet. | ~30h | Yes (old app) |
| **3** | Port computer grid, password manager, ping, basic health check. | ~25h | Both (old fully, new partially) |
| **4** | Port plugins one at a time. EnableWinRM first (simplest), then Cm12, then PsScript, then ClientCenter launcher. | ~25h | Both |
| **5** | Cutover. Settings importer for first run of new app. Retire old project. | ~10h | New only |

**Total: ~100h, 3–4 months at 5–8 hrs/week.**

---

## 7. The six existing plugins — port priority

Ordered by usefulness × ease:

1. **EnableWinRM** — One menu item. Trivial. Use `Win32_Process.Create` via CIM, or just shell out to `Invoke-Command -ScriptBlock { Enable-PSRemoting -Force }` over an alternative channel. Port first as a smoke test of the plugin contract. **~1 day.**

2. **cm12** — Right-click menu of SCCM client actions (Machine Policy, Heartbeat, Hardware Inventory, Update Scan, Update Eval, GPUpdate). Each is a one-liner that calls `TriggerSchedule` on `ROOT\ccm:SMS_Client` with a hardcoded GUID. Port the GUIDs verbatim. **~2 days.**

3. **PsScript** — Consolidates the old `psscripts` and `localpsscripts` into one plugin. Opens an AvalonEdit window, lets the user write PowerShell, runs it against selected computers. The old "PSHost" remote-execution mode in `localpsscripts` is rarely used — drop it unless needed. **~1 week** (the AvalonEdit integration is the bulk).

4. **ClientCenter** — Just a `Process.Start("ClientCenter.exe", "-host=foo")` wrapper. Trivial. **~half day.**

5. **RuckZuck** — Skip. Upstream service is likely dead.

6. **localpsscripts vs psscripts** — these are two plugins doing nearly the same thing in slightly different ways. Pick one approach (direct WinRM) and drop the other.

---

## 8. Bugs to fix during rewrite

These are real, reproducible bugs in the current app. The rewrite is the right place to address them:

### 8.1 Health Check runs when unchecked
**File:** [source/CMCollCtr/MainForm.cs:1717-1732](source/CMCollCtr/MainForm.cs)
**Cause:** Unchecking `cbHealthCheck` sets the timer's `Enabled = false`, but does not call `ctHealth.Cancel()`. An in-flight `Parallel.ForEach` (started at line 542) keeps grinding through the computer list until it finishes naturally.
**Fix in new app:** the equivalent ViewModel should expose `IsHealthCheckEnabled` as an observable property; the underlying `Task` should be cancelled the instant it becomes false. With async/await + CancellationToken propagation, this is structural — you can't write the bug.

### 8.2 Various silent failures
**Status:** mitigated in Phase 0 — see §10. In the new app, **never write an empty `catch`.** Use `ILogger.LogDebug(ex, ...)` at minimum.

### 8.3 Race conditions around `pForm.Show()`
**File:** [source/CMCollCtr/Program.cs:67, 114, 173](source/CMCollCtr/Program.cs) — `Thread.Sleep(200)` after `Show()` to "wait" before `Invoke`.
**Fix in new app:** WPF + MVVM + async means the entire pattern goes away.

### 8.4 Registry view picked from `PROCESSOR_ARCHITECTURE` env var
**File:** [source/CMCollCtr/Program.cs:254-308](source/CMCollCtr/Program.cs) — reads `PROCESSOR_ARCHITECTURE` which is the *process* architecture, not the OS architecture. Mis-reads on 64-bit OS when running as x86.
**Fix in new app:** `RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)`. Don't sniff the env var.

---

## 9. Audit findings to design around (not to port)

These exist in the old app. Do NOT replicate them in the new one:

| Finding | Old code | New design |
|---|---|---|
| Hardcoded SQL `sa` password | [App.Config:11-13](source/CMCollCtr/App.Config) | Delete entirely. The `SchedulerEntities` connection string is dead code. |
| Passwords "encrypted" with domain name as key | `sccmclictr.automation.common.Encrypt(value, Domain)` in [PasswordManager.cs:61](source/CMCollCtr/PasswordManager.cs) | `ProtectedData.Protect` (DPAPI, CurrentUser scope). |
| WQL injection on `/Path:` CLI arg | [Program.cs:158](source/CMCollCtr/Program.cs) | Parameterize. Validate input. |
| 80+ empty `catch { }` blocks | All over MainForm.cs (mitigated Phase 0) | `ILogger`. Never empty. |
| `System.Management.Automation 3.0.0.0` | csproj hint path | `Microsoft.PowerShell.SDK` via PackageReference. |
| `Get-WmiObject` in shipped scripts | `PSScripts/Cache/CacheCleanup.ps1` and others | Update to `Get-CimInstance` for PS 7 compatibility. |
| Plugin reflection contract | [MainForm.cs:254](source/CMCollCtr/MainForm.cs) `InvokeMember("EnableCMRows", ...)` | Proper `IPlugin` interface. See §5. |
| `Application.DoEvents()` | scattered | async/await. Banned. |
| `Thread.Sleep` race workarounds | [Program.cs:67](source/CMCollCtr/Program.cs) etc. | async/await + `Loaded` event handlers. |
| `BindingSource` + `DataGridView` | MainForm.cs throughout | `ObservableCollection<ComputerViewModel>` + WPF `DataGrid`. |

---

## 10. Phase 0 — what's already done

Look at:
- **`source/CMCollCtr/Logging.cs`** — new file, zero-dependency file logger to `%LOCALAPPDATA%\CMCollCtr\logs\cmcollctr-YYYYMMDD.log`. Auto-tags entries with `[CallerMemberName]` / `[CallerLineNumber]`. Has `Swallow(ex)`, `Error(ex, context)`, `Info(message)`.
- **`source/CMCollCtr/Program.cs`** — `Logging.Initialize()` is the first call in `Main`. The two `UnhandledException` handlers now actually log instead of doing `e.ToString()` and discarding.
- **`source/CMCollCtr/MainForm.cs`** — all 87 silent-failure sites now log:
  - 61 instances of `catch { }` → `catch (Exception _ex) { Logging.Swallow(_ex); }`
  - 25 instances of `ex.Message.ToString();` (a no-op) → `Logging.Swallow(ex);`
  - 1 legit assignment at [line 924](source/CMCollCtr/MainForm.cs:924) preserved: kept `oComp.ErrorMessage = ex.Message;` and added a `Logging.Swallow(ex);`.
- **`source/CMCollCtr/PasswordManager.cs`** — 1 empty catch in the constructor logged.
- **`source/CMCollCtr/CMCollCtr.csproj`** — added `<Compile Include="Logging.cs" />`.

**The `_ex` variable name** in inline catches avoids C# variable-shadowing errors (CS0136) where nested catches sit inside outer `catch (Exception ex)` blocks.

**Migration note:** The new app should drop this homegrown `Logging.cs` immediately in favor of `Microsoft.Extensions.Logging` + Serilog. The homegrown logger only existed to provide visibility before the rewrite.

---

## 11. Dead code to drop

- **`source/CMCollCtr/App.Config:11-13`** — `<connectionStrings>` block for `SchedulerEntities`. Points at the original author's `srsccm01.syliance.dns` SQL server with `sa` / `kerb7eros`. **No C# code references it.** The EDMX (`ComputerType.edmx`) that the connection string was meant to feed only generates POCO classes used as binding shapes — no SQL connection is ever opened.
- **`source/CMCollCtr/ComputerType.edmx`** — EF6 designer model targeting SQL Server 2005 provider manifest. In the new app, replace with plain POCO classes in `CMCollCtr.Core.Models`. The generated `Computer` and `ComputerContainer` types are used as data-binding shapes; rewrite them as `record` types or `ObservableObject`-derived view models.

---

## 12. File reference map (old codebase)

| What you need | Where to find it |
|---|---|
| Computer grid logic, ping, health check, threading | [source/CMCollCtr/MainForm.cs](source/CMCollCtr/MainForm.cs) |
| App entry point, command-line parsing, MMC extension registration | [source/CMCollCtr/Program.cs](source/CMCollCtr/Program.cs) |
| Password manager UI + storage | [source/CMCollCtr/PasswordManager.cs](source/CMCollCtr/PasswordManager.cs) |
| Computer / ComputerContainer POCO classes | [source/CMCollCtr/ComputerType.Designer.cs](source/CMCollCtr/ComputerType.Designer.cs) |
| Settings schema | [source/CMCollCtr/Properties/Settings.settings](source/CMCollCtr/Properties/Settings.settings) |
| App settings defaults | [source/CMCollCtr/App.Config](source/CMCollCtr/App.Config) |
| SCCM client wrapper library (3rd party — `sccmclictr.automation`) | `source/CMCollCtr/packages/sccmclictrlib.1.0.0.18/lib/net46/` — read-only reference, **rewrite functionality natively in CMCollCtr.Core.Sccm**. |
| PowerShell scripts shipped with the tool | [source/CMCollCtr/bin/Debug/PSScripts/](source/CMCollCtr/bin/Debug/PSScripts/) — 58 scripts across Agent, Cache, DCM, OS, SCOM, SW Updates, WMI Repair, WUA folders. Bundle these into the new app's resources or a `scripts/` folder. Most use `Get-WmiObject` — update to `Get-CimInstance` for PS 7. |
| Plugin entry points (each plugin's main UserControl) | `source/plugin.collctr.*/UserControl1.cs` |

---

## 13. User-data migration (Phase 5)

When the user runs the new app for the first time, import:

1. **Computer list** (if persisted): currently lives in `user.config` under `XmlStorage` / loaded from CLI args. Probably empty in practice — most users supply lists via `/File:` or `/List:` CLI args.
2. **Password manager entries**: `user.config` under setting name `PasswordManager`, XML-serialized `BindingList<PWList>`. Each entry has `Domain`, `Username`, `Password` (encrypted with the broken domain-key cipher). **Decrypt with the old algorithm, re-encrypt with DPAPI on import.** The decryption needs to mirror `sccmclictr.automation.common.Decrypt(password, domain)` — read the IL or just re-derive (it's a basic Rijndael with PBKDF2 over the domain string).
3. **Settings**: `MaxPingThreads`, `MaxHealthThreads`, `PingTimeout`, `HealthCheckDelay`, `WinRMPort`, `WinRMSSL`, `ServerDNSSuffix`. Map 1:1 into the new `settings.json`.

`user.config` lives at `%LOCALAPPDATA%\CMHealthMon\CMCollCtr.exe_Url_xxx\<version>\user.config`. Note: the legacy app's company name is `CMHealthMon` (the project's root namespace), not `CMCollCtr` — that's a vestigial rename the original author never finished. Search both locations.

---

## 14. How to drive this in Cursor

**Install:**
- [Cursor](https://cursor.sh/) (you have it)
- [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- Cursor extensions: **C# Dev Kit** (Microsoft), **.NET Install Tool**, optionally **XAML Styler**.

**Bootstrap commands (from the repo root):**

```powershell
cd source
dotnet new classlib --name CMCollCtr.Core --framework net8.0
dotnet new wpf --name CMCollCtr.Wpf --framework net8.0
dotnet new xunit --name CMCollCtr.Core.Tests --framework net8.0
dotnet new sln --name CMCollCtr.New
dotnet sln CMCollCtr.New.sln add CMCollCtr.Core CMCollCtr.Wpf CMCollCtr.Core.Tests
dotnet add CMCollCtr.Wpf reference CMCollCtr.Core
dotnet add CMCollCtr.Core.Tests reference CMCollCtr.Core

# Core NuGet packages
dotnet add CMCollCtr.Core package Microsoft.PowerShell.SDK
dotnet add CMCollCtr.Core package Microsoft.Management.Infrastructure
dotnet add CMCollCtr.Core package Microsoft.Extensions.Logging
dotnet add CMCollCtr.Core package Microsoft.Extensions.DependencyInjection
dotnet add CMCollCtr.Core package CommunityToolkit.Mvvm
dotnet add CMCollCtr.Core package Serilog.Extensions.Logging
dotnet add CMCollCtr.Core package Serilog.Sinks.File

# WPF UI packages
dotnet add CMCollCtr.Wpf package WPF-UI
dotnet add CMCollCtr.Wpf package AvalonEdit
dotnet add CMCollCtr.Wpf reference CMCollCtr.Core

# Tests
dotnet add CMCollCtr.Core.Tests package FluentAssertions
```

**Recommended order of first sessions in Cursor:**

1. **Session 1 — Scaffold + theme.** Get the new solution building. Wire up WPF-UI in App.xaml. Add a settings window with a theme switcher (Light/Dark/System) to prove the styling pipeline works end-to-end.

2. **Session 2 — Models + grid.** Define `Computer`, `Plugin`, `Credential` models in `Core`. Stand up `MainWindow` with a WPF DataGrid bound to an `ObservableCollection<ComputerViewModel>`. Add a Refresh command. No real backend yet — load test data.

3. **Session 3 — Ping.** Replace test data with real ping via `System.Net.NetworkInformation.Ping`. Wire up the start/stop commands. Cancellation via `CancellationTokenSource`.

4. **Session 4 — PowerShell host.** Build `PSRunspaceHost` in `Core`. Smoke-test with `Get-Process` against the local machine.

5. **Session 5 — Health check.** Port the SCCM client health query. This is when the Health Check bug from §8.1 becomes architecturally impossible.

6. **Sessions 6–9 — Plugins**, in the priority order from §7.

7. **Session 10 — Settings importer + cutover.**

**At the end of each session, update this document** if you've made a stack decision, found a new bug, or changed direction.

---

## 15. What NOT to port

- Don't port `Application.DoEvents()`. Anywhere.
- Don't port `BinaryFormatter` / `SoapFormatter` if you find any (you won't — old code uses `XmlSerializer`, which is fine).
- Don't port the `/Path:` MMC integration unless you actively use it. The MMC extension model is dying anyway. The `/List:` and `/File:` CLI args are useful and trivial to keep.
- Don't port the RuckZuck plugin.
- Don't port the `srsccm01.syliance.dns` SQL connection.
- Don't port the homegrown XML serialization for the password manager — use System.Text.Json.

---

## 16. Open questions for the user

When Cursor's AI hits one of these, escalate to the user — don't decide unilaterally:

1. **Which PS-script plugin model** — direct WinRM (recommended) or the old "PS host server" indirection from `localpsscripts`?
2. **MMC console extension** — do you want the new app to register itself as an MMC right-click extension for SCCM device collections (the `/Path:` integration)? If no, drop the registry-hive code in Program.cs entirely.
3. **High-DPI awareness** — `PerMonitorV2` is the modern default. Any reason to avoid?
4. **Telemetry** — none planned. Confirm.
5. **Auto-update** — out of scope for v1. Confirm.

---

## 17. Definition of done

- New app builds cleanly with `dotnet build`.
- Phase 4 plugin set (EnableWinRM, Cm12, PsScript, ClientCenter) works against real SCCM clients.
- Settings imported from old app on first run.
- Health Check bug fixed (impossible by construction in MVVM model).
- Old `CMCollCtr` project deleted from solution.
- README updated to reflect new build/run instructions.
- A `CHANGELOG.md` exists summarizing what changed.
