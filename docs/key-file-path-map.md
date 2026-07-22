# Vivre — key file-path map

> **Project knowledge note:** The load-bearing files, so new chats don't re-derive them. Paths are relative to the repo root (SirRuggie/Vivre).

## Project & namespace map

Moved here from CLAUDE.md ▸ Layout (which keeps one line per project). Inventory + the rules that ride each area.

| Project / namespace | Purpose |
|---|---|
| **`Vivre.Core`** (net10.0) | All non-UI logic. |
| ▸ `Models` | `Computer` + grid-row state (observability rules: ⚠ `Computer.cs` section below). |
| ▸ `Net` | Ping / reachability (`ReachabilityConfirmation`). |
| ▸ `PowerShell` | `PSRunspaceHost` — the ONE WinRM choke point, wrapped by `RoutingPowerShellHost`: on a Kerberos `0x80090322` rejection it flips the host to SMB/DCOM via the session-scoped `HostTransportCache`; Vitals scores the degradation (see windows-patching-lane.md ▸ "Kerberos-broken hosts"). |
| ▸ `Sccm` | `ConfigMgrClient`, client actions. |
| ▸ `Remoting` | `WinRmEnabler` (DCOM), `HostRebootProbe`, `OrphanRebootServiceReaper` — reaps orphaned `Vivre_Reboot_*` services on list load. |
| ▸ `Credentials` | Operator credential handling. |
| ▸ `Computers` | Named-list store. |
| ▸ `IO` | `AtomicFileWriter` — crash-safe temp+swap writes behind the settings and list stores. |
| ▸ `Scripts` | Script-library store. |
| ▸ `Logging` | Serilog wiring. |
| ▸ `Updates` | The WUA lane (see windows-patching-lane.md). |
| ▸ `Vitals` | `VitalsProbe` + the pure `VitalityScorer` — the read-only 0-100 machine health score. |
| ▸ `Remediation` | `RemediationService` — start a service / free disk / end a process from the Vitals triage view. |
| ▸ `Deploy` | `DeploymentService` — **stage** a package: copy to a temp dir on the target, no execution; the admin runs installs. Transport prefers the **SMB admin share** (`\\host\C$`, fast single copy), falls back to **WinRM** (zip → chunked transfer → SHA-256 verify → expand) when SMB is blocked. Install-as-SYSTEM was dropped — EDR agents tear down watch sessions mid-install; delivering files is robust. |
| ▸ `Software` | `SoftwareProbe` — registry-based, read-only installed check → the grid's Software column; on any WinRM-unavailable failure falls back to a read-only StdRegProv/DCOM read (`DcomSoftwareReader`, ambient login) — load-bearing RV rules: ▸ "Software check" below. |
| ▸ `Columns` | `CustomColumnProbe` — user PowerShell one-liner per machine → custom grid column; the column manager hides/shows built-ins + adds custom/predefined columns, persisted to AppData. |
| ▸ `Threading` | `SplitThrottle` — the split active/reserved sweep-concurrency budget that keeps passive fills (custom columns, client actions) from starving behind large sweeps. |
| ▸ `Wug` | `WugMaintenance` — WhatsUp Gold enter/exit set + read-only per-machine state read, via WhatsUpGoldPS shelled out to Windows PowerShell 5.1 (the two 5.1 traps: CLAUDE.md ▸ Conventions); no reboot path. |
| ▸ `Rdp` | `RdpHostStore` (folder/host tree); `RdpCredentialStore` (DPAPI-per-user saved logins + inheritance resolver); `RdpDisconnectClassifier` — keep-by-default: a session tab closes ONLY on a measured sign-out (`ExtendedDisconnectReasonCode` 12 while connected, no auto-reconnect in flight); every other/unknown code keeps the tab with Reconnect. UI-free — the RDP ActiveX control + WindowsFormsHost live in Vivre.Desktop. |
| ▸ `Configuration` | `SharedSettingsStore` — machine-wide shared settings (rules: CLAUDE.md ▸ Building/running). |
| **`Vivre.Desktop`** (net10.0-windows) | The WPF app (`Vivre.exe`): shell + workspace VMs, dialogs, composition root `App.xaml.cs`. RDP machine-gate + THE PIN CARDINAL: CLAUDE.md ▸ Layout. |
| **`Vivre.UpdateAgent`** (net48) | SYSTEM-run WUA agent EXE, bundled beside `Vivre.exe` (see windows-patching-lane.md). |
| **`Vivre.Core.Tests`** (net10.0) | xUnit suite. |
| `tools/RemoteRun` | Dev console to exercise remote PowerShell (WinRM) against a host. |
| `scripts/` | Curated PS7 script library, seeded to `%APPDATA%\Vivre\Scripts`, surfaced via right-click **Run script…** (grouped by category in the Run Script window). |

## Patch lane / 2016 LCU lane
- `source/Vivre.Core/PowerShell/PSRunspaceHost.cs` — WSMan connect/execute; `MaxConnectionRetryCount=0` on both sites (the WSMan retry crash fix). **Also the PSModulePath contaminator — see gotcha below.**
- `source/Vivre.Core/PowerShell/HostWinRmGate.cs` — per-host WinRM shell cap (≤4 concurrent/host; background probes capped at 2 so operator-clicked ops always have reserved slots). Acquired at the shell-open chokepoint in `PSRunspaceHost` via a `background` flag threaded through `IPowerShellHost`/`IHostRebootProbe`; the monitor's reboot-pending poll is the only `background: true` caller.
- `source/Vivre.Core/Net/ReachabilityConfirmation.cs` — pure `ConfirmEffectiveOnline(previous, rawOnline, consecutiveFailures, threshold)`: a previously-online box needs 2 consecutive failed probes before the monitor declares it offline (kills false "went offline" blips). Wired into `WorkspaceViewModel.MonitorRowAsync`.
- `source/Vivre.Core/Updates/WuaUpdateLane.cs` — the normal Windows Update lane; owns the agent bytes + `SmbAgentLane`. Install selector lives here (an install is multi-call). Scan emission: the PS scan string AND the `Vivre.UpdateAgent` agent (`Program.cs`) now emit raw `MinSizeBytes`+`MaxSizeBytes` per update (replaced the single rounded `SizeMb`); `SmbAgentLane.ParseScanResultJson` parses both.
- **Update download-size lane** (display-only — nothing reads or acts on the size; replaced WUA's inflated `MaxDownloadSize` worst-case aggregate in the grid):
  - `source/Vivre.Core/Updates/SoftwareUpdate.cs` — the scan record now carries `long MinDownloadSizeBytes` + `long MaxDownloadSizeBytes` (raw bytes; replaced the old `double SizeMb`).
  - `source/Vivre.Core/Updates/UpdateSizeResolver.cs` — pure tiered resolution: `ResolveDisplaySize(catalogBytes?, minBytes, maxBytes)` → WUA `MaxDownloadSize` when `Max>0 && Max≤10 GB` (PRIMARY, matches BatchPatch); `Min` when `Max==0`; the catalog size when `Max>10 GB` (`AbsurdMaxDownloadSizeBytes` — the inflated express/checkpoint-CU aggregate); else `null` (→ dash). `NeedsCatalogLookup(maxBytes)` = `Max>10 GB` gates the network call to absurd rows only. `ArchFromTitle` extracts x64/arm64/x86 from the update title for catalog row selection.
  - `source/Vivre.Core/Updates/CatalogPageParser.cs` (pure) — HtmlAgilityPack parse of the catalog `Search.aspx` result page: reads the hidden `_originalSize` RAW-BYTE spans (ignores the formatted `_size` text), pairs each to its row title; `SelectSizeBytes` picks the architecture-matched row else the largest (full OS package, never a small companion).
  - `source/Vivre.Core/Updates/MicrosoftUpdateCatalogService.cs` (`ICatalogSizeService`) — one read-only **TLS-1.2** HTTPS GET to `catalog.update.microsoft.com/Search.aspx?q=KB#`, ~30 s timeout; per-KB `ConcurrentDictionary<string,Task<long?>>` cache (caches the in-flight Task, so many machines/tabs showing the same KB fetch it once); all failures → `null` ("unavailable"); **log-free** (Core convention — null is the surfaced outcome). One shared instance built in `App.xaml.cs`. Dependency: **HtmlAgilityPack 1.12.4** (`Vivre.Core.csproj`).
  - Display + fill: `SelectableUpdate.DisplaySizeMb` (computed via the resolver from the observable `CatalogSizeBytes`, wired with `[NotifyPropertyChangedFor]`) is grid-bound with `TargetNullValue=—` in `MainWindow.xaml` + `ComputerDetailWindow.xaml`. `WorkspaceViewModel.ResolveCatalogSizesAsync` runs fire-and-forget after `ReplaceUpdatesForScope`, resumes on the UI thread (no `ConfigureAwait(false)`), and its `Where` requires `NeedsCatalogLookup`, so a normal fleet scan makes **zero** catalog calls.
- `FullPackageLcuLane` (Vivre.Core/Updates) — the Server 2016 full-package CU lane. `StageAsync`, `VerifyAsync`, `ComponentCleanupAsync`.
- `PatchService.cs` / `IPatchService` (Vivre.Core) — per-host serialization owner (the `_inFlight` guard the LCU lane reuses). The LCU lane lives INSIDE PatchService so Stage/Cleanup/Wave can't collide with a WUA install on the same box.
- `RebootWave.cs` / `IRebootWave.cs` (Vivre.Core) — the wave state machine. Graceful→8min→force escalation, scoped to operator-selected + confirmed boxes only. `RebootAndCommitAsync` takes a pluggable `IRebootReadinessProbe` and `IPostRebootConfirmation` so the wave is reusable across box types, and an optional `IRebootGate` for burst-rate limiting.
- `DcomRebootTrigger` (Vivre.Core/Updates) — the ONLY `Win32Shutdown` primitive site in the C# codebase (the reboot cardinal — gate grep `Win32Shutdown` → exactly one file). Used by BOTH the Reboot Wave AND `ForceRebootRunner`'s Kerberos fallback (no longer wave-only). DCOM `Win32Shutdown` (flags 2 = reboot, 6 = reboot+force), with an SMB/SCM reboot fallback for Kerberos-broken boxes (a one-shot `Vivre_Reboot_*` LocalSystem service that runs `shutdown.exe` over NTLM SSO; 64337d1). `RebootDispatch.AlreadyInProgress` (Win32 1115) = the box is already going offline on its own, never a failure.
- `ForceRebootRunner` (Vivre.Core/Updates) — Force reboot's channel runner: `shutdown.exe /r /f /t 5` over WinRM, with ONE narrow fallback — on a WinRM Kerberos auth rejection (`KerberosWrongPrincipalException` ONLY — auth precedes execution, so the command provably never ran) it completes the SAME confirmed reboot over the injected `IRebootTrigger` (`DcomRebootTrigger` → DCOM → SMB/SCM). Every ambiguous failure (session-lost, timeout, shell-init/busy, `HadErrors` = the box itself refused, unknown) surfaces with NO fallback — no double-reboot. Optional `IProgress<string>` narration hook feeds the grid's Reboot message column at the channel switch. Contains NO shutdown primitive (the cardinal lives in `DcomRebootTrigger`, so the grep stays one file). `ForceRebootChannel` (WinRm/Dcom) + `ForceRebootResult` record.
- `OrphanRebootServiceReaper` + `RebootServiceReapPolicy` (Vivre.Core/Remoting) — the list-load reaper for orphaned `Vivre_Reboot_<32hex>` services the SMB fallback's best-effort delete can leave behind (`a008747`). Read-enumerate-query-delete ONLY — its advapi32 set deliberately binds no StartService/ControlService/CreateService; deletes exact-name + confirmed-Stopped matches, once per host per session, gated by auto-check-on-load.
- `DcomLcuBuildReader` (Vivre.Core) — reads the UBR over DCOM for Verify.
- **CU package auto-read (the Settings "Read from package" accelerator — read-only, writes nothing):**
  - `MsuIdentity` (Vivre.Core/Updates) — the PURE, I/O-free parser: given the `.msu` file name, its single expanded servicing-XML member name, and that XML's text, returns `MsuIdentityResult.Accepted(Kb, TargetUbr, Arch, …)` or `Refused(reason)`. PRODUCT-PINNED — accepts ONLY identity `Package_for_RollupFix` + version matching `^14393\.(\d+)\.` (→ `TargetUbr`, e.g. `14393.9339` → 9339) + `amd64` (returned as `"x64"`), with a KB cross-check that the file name and embedded metadata carry the SAME KB. Refuses an SSU, a 2019/Win10 CU, a .NET rollup, a combined SSU+LCU bundle, a renamed file, or malformed XML — a wrong CU identity fed into the staging lane is a real-harm class, so it never guesses (safe-decline to manual entry).
  - `MsuPackageReader` (Vivre.Core/Updates) — the I/O reader: finds EXACTLY one `.msu` in the CU folder (0 or >1 → refused, never coin-flips between months), expands its servicing XML via `expand.exe -F:*.xml` (sub-second — does NOT decompress the multi-GB payload; 30s cap + kill; `IMsuXmlExtractor` seam for tests), hands it to `MsuIdentity.Parse`, and returns `MsuReadResult` (the parser verdict + the file path/size/last-modified stamp — a DOWNLOAD date, NOT a release date). Computes no hash, contacts no host, writes no settings; the temp extract dir is always deleted.
- `StagePreconditions` (Vivre.Core/Updates) — pure, unit-tested pre-Stage decision predicates: `IsAlreadyStaged` (RebootRequired && StagedThisSession → skip "Already staged — run Reboot Wave"), `IsAlreadyCurrent` (VerifyLcuAsync's verdict == Verified → skip "Already current — skipped"; fail-OPEN on a null/unreadable read), `UnscannedThisSession` (targets whose `LastScannedApplicable` is null → the scan-this-session gate). Wired into `StageLcuRowAsync` (the two skips) and `OnStage2016` via `WorkspaceViewModel.UnscannedStageTargets()` (the gate, shown before the package check).
- **Staged-patching toggle (opt-in 2016 routing) — Core pieces:**
  - `StagedInstallPlanner` (Vivre.Core/Updates) — pure planner. `Plan` partitions an Install set into flagged-2016-not-staged (the dialog set) vs Normal, + per-box Settings-vs-scan CU KB mismatches; `NeedsStageDecision` (the per-box predicate); `PartitionByCurrency` — the pre-dialog already-current split, **fail-open**: a box is excluded only on `LcuVerifiedThisSession` OR a definitive `Verified` UBR read (Unreachable / WrongBuild / null read → stays in the dialog).
  - `Lcu2016CuMatcher` (Vivre.Core/Updates) — identifies the 2016 OS CU KB from a scan's titles. `FindCuKb` (single confident match → the dialog's mismatch warning, returns null when ambiguous) and `CuKbs` (EVERY CU-titled KB → the "Install minor updates only" exclude set, so the CU can't slip through WUA even when the scan lists two CU KBs).
  - `LcuRouting.RebootVerifyLaneFor(int?, bool)` — override-aware lane: a 2016 box verifies via the UBR (Lcu2016) lane ONLY when flagged for staging; a non-flagged 2016 box verifies via WUA. The 1-arg overload is kept for legacy callers (treats every 2016 box as the LCU lane).
- **Transient WUA reach-failure retry (no false-green) — the `0x80072EE2` SLS timeout + the BatchPatch fake-green trap (see windows-patching-lane.md ▸ "Transient WUA reach failures"):**
  - `TransientWuaError` (Vivre.Core/Updates) — pure classifier: is a WUA failure a transient reach hiccup (retry) or terminal (surface at once)? Transient family = `0x80072EE2` + `0x80240438` + the WININET/WinHTTP & WU_E_PT timeout/5xx siblings; auth/config/4xx/install errors excluded. Keys on the HRESULT, **not** the phase. `IsTransient(int)` / `IsTransient(string)` / `FirstTransientToken`.
  - `TransientRetryRunner` (Vivre.Core/Updates) — pure retry driver (injected attempt / delay / onRetrying / buildExhausted): transient + retries-left → calm "retrying" + backoff + re-dispatch; success or terminal → return at once; exhausted → honest `Unreachable`. Wraps the WHOLE operation (service-reg → search → download → install).
  - **Face 2 (non-clean search ≠ up-to-date):** `WuaUpdateLane.ScanAsync` reads the search `ResultCode` (the scan script emits it as a `SearchResultCode` status row) and diverts any non-`orcSucceeded` result to a transient reach failure via `SearchDidNotCleanlySucceed` / `BuildSearchIncompleteMessage` (`OrcSucceeded=2`) **before** the up-to-date path. `SmbAgentLane.BuildScanStatus` does the same for the SMB scan; `Vivre.UpdateAgent` `RunScan`/`RunInstall` write a terminal Error line on a non-clean `ResultCode` (read-only — no install/reboot added).
  - `HostPatchStatus.Unreachable` / `PatchPhase.Unreachable` → reduces to `PatchState.Error` (never green) with the distinct **"Can't reach WU"** chip label (`WorkspaceView.xaml` `UpdatePhase=Unreachable` text trigger).
  - **VM wiring** (`WorkspaceViewModel`): `ScanRowAsync` / `InstallRowAsync` wrap the `_patch` call in `TransientRetryRunner`. `MaxTransientRetries`=3; jittered `TransientBackoffDelayAsync` (60s + up to 15s); **fresh per-attempt** `ScanAttemptTimeoutSeconds`=300s via a linked CTS inside each scan attempt (NOT a shared budget — the (a) fix; the 3 scan dispatch sites dropped the old shared per-host 300s); install re-entry guard so a transient after install began surfaces terminal, never a re-run — the began-flag is `InstallBeganLatch` (Vivre.Core/Updates), a synchronous producer-side `IProgress` decorator (`832aa7f` closed the race where the old UI-posted flag write lost to the retry attempt's thread-pool read).
- `DcomRebootReadinessProbe` (Vivre.Core) — pre-reboot readiness guard (3 signals, fail-safe: unreadable = not-ready). Used for Server 2016 staged boxes to prevent rebooting into the 2-hour TrustedInstaller Stopping hang.
- `BasicReachabilityReadinessProbe` (Vivre.Core) — permissive readiness probe for non-2016 operator-ordered reboots. Always answers Ready; the 2016-specific TrustedInstaller/CBS signals do not apply.
- `IPostRebootConfirmation` (Vivre.Core) — pluggable post-reboot confirmation strategy. Three outcomes: Confirmed (terminal green), Failed (terminal red), NotReady (retry).
  - `UbrConfirmation` — 2016 strategy: reads UBR via `DcomLcuBuildReader` and delegates to `FullPackageLcuLane.Decide`. Same rule as the standalone Verify, so wave and Verify can't drift.
  - `ReadyConfirmation` — non-2016 strategy: queries `Win32_OperatingSystem` via DCOM/CIM. Confirmed = OS stack answered; NotReady = not up yet. Never returns Failed (whether updates took is decided by the WUA rescan).
- `IRebootGate` (Vivre.Core) — rate-limiter interface for reboot issuance. Acquired only around the actual reboot trigger; never held through the offline watch.
  - `RebootTriggerGate` (Vivre.Desktop/ViewModels) — `IRebootGate` wrapping a `SemaphoreSlim` with optional jitter. Shared across all per-box tasks in a wave via the static `_rebootTriggerThrottle`.
- `RebootOutcomeSelector` (Vivre.Core/Updates) — pure (no I/O) selector mapping post-reboot rescan counts → one of the `RebootOutcomeMessages` strings, called from `WorkspaceViewModel.ReportPostRebootOutcomeAsync`. `Classify(failed, remaining, rebootStillPending, scanFailed) → RebootOutcomeKind` (UpToDate / Remaining / Failed / RebootStillPending / CouldntRescan / CouldntConfirm) is the SINGLE source of outcome precedence (scan-failure > install-failure > confirmed-pending > remaining > couldn't-confirm > up-to-date); `Select` delegates to it, and the VM keys the row's `Unverified` display state off the kind (CouldntConfirm/CouldntRescan) so the chip and the message can never drift.
- **`PatchState.Unverified` — the post-reboot couldn't-confirm state (neutral grey, NEVER green, NOT red):** a box back from a reboot wave whose post-reboot rescan FAILED, or whose reboot-pending probe couldn't answer. Plumbing: `PatchPhase.Unverified` + `PatchState.Unverified` (`HostPatchStatus.cs`); the `DerivePatchState` case in `Computer.cs` (a KNOWN-pending reboot still upgrades it to amber `RebootPending`); the VM stamps `UpdatePhase = Unverified` keyed off `RebootOutcomeKind` (`WorkspaceViewModel.ReportPostRebootOutcomeAsync`, both the WUA and 2016 lanes); the neutral chip + detail-pill (`WorkspaceView.xaml`, `ComputerDetailWindow.xaml`); counted in `FleetSummary` and downgraded to Warning in `BuildCompletionSummary`; and `ScopeToggleRule` treats it as a message-preserving terminal.
- `RebootMessageText` (Vivre.Core/Updates) — pure `IsTransientRebootNotice(msg)` for the per-host **Reboot message** column: a PAST-EVENT notice (prefix `"Reboot complete"`, `"Back online"`, or `"Forced reboot sent"`) is cleared when the row starts a new scan/install so it can't linger and look like a fresh reboot; the CURRENT-STATE notices (`"Offline since…"`, `"WinRM temporarily unavailable…"`) are EXCLUDED — each has its own condition-based clearer. Narration set-sites live in `WorkspaceViewModel`: `RebootWaveRowAsync` mirrors the Rebooting-phase progress into `Computer.RebootMessage`; the force-reboot branches write `"Rebooting (force)…"` / `"Forced reboot sent HH:mm (DCOM)"` / `"Reboot failed HH:mm — reason"` (only the three prefixes above are transient-cleared — a regression test locks them against drift).
- TCP-445 reachability probe (`TcpReachabilityProbe`) — drives the offline-detection and online-return watch loops inside `RebootWave`.

## ⚠ PS 5.1 shell-out gotchas — canonical rules live in CLAUDE.md § Conventions

- The two rules (write the temp `.ps1` UTF-8 **with BOM**; strip the inherited **`PSModulePath`**) are
  stated canonically in **CLAUDE.md § Conventions** — read them there; this entry is the applied-at map.
- **Applied at:** PSModulePath strip → `RunPreflightProcessAsync` AND `RunAsync` in `WugMaintenance.cs`;
  BOM write → the ONE shared helper `WritePs51ScriptAsync` (`WugMaintenance.cs`), locked by regression
  test `WritePs51Script_writes_utf8_with_bom`. Any new 5.1 launcher must use that helper.
- Validation must mirror the real writer's exact bytes (`new UTF8Encoding(true)`) — a harness that writes
  with `Set-Content -Encoding UTF8` silently adds a BOM in 5.1 and green-lights the very bug (also in
  CLAUDE.md § Conventions).

## WUG maintenance (WhatsUp Gold)

Full story — root causes, the failed attempts, live evidence: **docs/wug-state-check-findings.md**
(frozen case file). This is the path + rules stub; every rule below is current as-shipped (1.16.4).

- `source/Vivre.Core/Wug/WugMaintenance.cs` — ALL WUG logic: talks to WUG via the **WhatsUpGoldPS**
  module (not raw REST), shelled out to **Windows PowerShell 5.1**; runs on the operator's workstation
  ONLY (no target box is contacted), **no reboot path**. Holds the set path (`RunAsync` → internal
  `RunCoreAsync` seam), pre-flight (`TestConnectionAsync` / `InstallModuleAsync`), the streaming state
  read (`GetMaintenanceStateAsync` + `StateScript`), the shared launcher `RunPreflightProcessAsync`, the
  BOM helper `WritePs51ScriptAsync`, and the three stdout markers. All 5.1 shell-outs strip
  `PSModulePath` + write UTF-8-with-BOM (gotchas above).
- **Resolver — single-sourced (`ResolveFunctionScript`, spliced into BOTH set and state paths; never
  fork it):** outcome is exactly MatchedByName / MatchedByIp / NoDevice / Ambiguous / LookupError. Name
  match is normalized, case-insensitive, DOT-BOUNDARY ("APVSQL1" ≠ "APVSQL10.domain"), presence-guarded
  over name/hostName/displayName. **The IP fall-through classifies by the COUNT of EXACT
  `networkAddress` equality matches** (WUG's SearchValue is a SUBSTRING search — ".10" also returns
  ".101"/".109"): **1 exact → MatchedByIp; 0 exact (rows returned, none equal) → NoDevice ("no matching
  device"); 2+ exact → Ambiguous ("unknown")**. **An errored search is LookupError (state UNKNOWN),
  NEVER a false NoDevice** — only a clean-empty answer everywhere is NoDevice. Locked by
  `WugResolverProcessTests`.
- **SSL trust — the cold-start mass-unknown fix; invariants locked by `WugSslTrustTests`:**
  - The module's `Get-WUGAPIResponse` `begin{}` **re-arms a scriptblock cert callback on every API call**
    whenever it finds the callback null (gated on the module's ignore-SSL flag).
  - A scriptblock callback **dies on I/O-completion-port threads** ("no Runspace available") during cold
    TLS handshakes → mass LookupError → "state unknown", self-healing on the warm rerun.
  - On .NET Framework a **non-null callback WINS** — `CertificatePolicy` is ignored while one is set.
  - Fix invariants: **`-IgnoreSSLErrors` at ZERO connect sites** (pre-flight included — it must match the
    real run; the flag gone gates off every module callback site); a **compiled, runspace-free
    `RemoteCertificateValidationCallback`** (`VivreWugCertValidator`) installed at script HEAD **before
    the first connect**; its `Add-Type -ReferencedAssemblies` **resolved at runtime from the live types'
    `Assembly.Location`** (X509Chain is type-forwarded on some boxes — never a guessed assembly name);
    trust is **non-optional** — a failed install hard-fails "Couldn't establish a trusted connection to
    WhatsUp Gold", never a silent continue.
- **Pooled state read:** concurrency from Settings (`AppSettings.WugStateConcurrency`, default **2**,
  clamp **1–4** in C# AND re-clamped in-script; env absent = 1 = sequential). Cap rationale (measured):
  1→2 halves wall time, 2→4→8 flat (~1.1s/lookup; WUG serialises under load); a bulk inventory prefetch
  measured SLOWER (426s / 1469 devices) and is permanently rejected. **The four fan-out traps (all
  honoured):** **T1** `ServicePointManager::DefaultConnectionLimit = 32` before the first request (.NET
  Framework's per-host default of 2 silently throttles any pool); **T2** connect ONCE per runspace with
  its OWN session — never share the module's auth globals across runspaces (the headers dict is not
  thread-safe); **T3** completion-order poll-drain — not `WaitHandle.WaitAny` (64-handle cap), not
  submission-order `EndInvoke` (head-of-line blocking starves stdout and trips the stall watchdog);
  **T4** deliberately NO per-lookup `Stop()` (cooperative — can't interrupt a blocked request); the C#
  stall watchdog + ceiling are the sole authority over a wedged run. Test seam
  `VIVRE_WUG_MODULE_OVERRIDE` (never set in production) carries the committed stub module
  (`Vivre.Core.Tests/Wug/Fixtures/WugStubModule.psm1`) through the SAME `ImportPSModule` path.
- **Three stdout markers, distinct ON PURPOSE:** `__WUGP__` progress → activity log (set path only) ·
  `__WUGDEV__` one JSON object per device as it resolves (routed OUT of the summary buffer; JSON-per-line
  keeps the wire pure ASCII — never switch to a raw delimited format) · `__WUGRESULT__` the authoritative
  summary. **`ParseMaintenanceState` REQUIRES the marker** (the last-braced-line fallback was DELETED —
  it could parse a trailing device line as a clean-but-empty summary = a quiet false green);
  `ParsePreflight` keeps its fallback (no device lines exist there). "Module missing" is reported ONLY on
  an explicit script signal — a timeout/empty/unparseable output surfaces the real error, never a false
  reinstall prompt.
- **Timeouts + aborts:** `StateReadStallTimeout` **90s** — resets ONLY on a `__WUGDEV__` line, kills a
  wedged run naming the last machine ("Stalled after X — 47 of 324 checked"); `StateReadCeiling`
  **45min** — runaway backstop. An aborted read KEEPS the already-streamed results (snapshot-copied
  under a lock against stragglers from the killed child — the live-Dictionary write-during-read is this
  codebase's cardinal crash class); unreached rows read `WugRowText.NotChecked` ("not checked (read
  stopped)") — deliberately NOT "unknown" and NOT "no matching device". **Kill-on-cancel:** a caller
  token cancel KILLS the `powershell.exe` child in BOTH launchers (a cancelled SET must never still flip
  maintenance afterward). All row strings live in `Vivre.Core/Wug/WugRowText.cs`, test-locked.
- **Detail enrichment (reason / who / since):** in-maintenance rows ONLY — **the cost contract is
  load-bearing** (a not-in-maintenance box costs exactly what it cost before; test-locked by counting
  stub REST calls). Two 15s-capped GETs on the emitting thread; every failure fails OPEN (plain "in
  maintenance") and is COUNTED into the "maintenance-reason lookup failed for N" summary note — degraded
  detail is said out loud, never silent, never a state change.
- **Dialogs + wiring:** `MaintenanceWindow` — pre-flight gate before the set fires; Reason field only in
  Enter mode. `WugStateWindow` — server read-only from Settings; same gate; fires
  `WorkspaceViewModel.CheckWugStateAsync`. `CheckWugStateAsync` — PASSIVE op (`registerRows: false`, so
  Stop lights and cancels it, killing the child); per-row writes ONLY via a `Progress<>` constructed on
  the UI thread (the dispatcher capture IS the thread-safety — never write `CommandResult` from the
  stdout reader thread); post-exit reconcile stamps only rows that saw no line; a generation guard makes
  a second check supersede the first; abort logs "Stopped — N of M checked" (test-locked). **No
  `ConfigureAwait(false)` in `CheckWugStateAsync`** — the dispatcher continuation keeps the reconcile
  UI-thread-safe.
- **Credential invariant (DO NOT deviate):** the WUG password is a `SecureString` → plaintext only via
  `new NetworkCredential(...).Password` → handed to the child ONLY via the `VIVRE_WUG_PASS` environment
  variable — never on a command line, to disk, or in a log. Only the server address persists
  (`SharedSettings.WugServer`); credentials are NEVER saved.

## Software check (installed-software column) — WinRM + DCOM fallback
- `source/Vivre.Core/Software/SoftwareProbe.cs` — the WinRM-first probe (registry Uninstall hives via
  a PS script; never `Win32_Product`). On ANY `IsWinRmUnavailable` failure (Kerberos 0x80090322,
  WinRM stopped, session lost) it reroutes to the injected `IDcomSoftwareReader` (à la `VitalsProbe`);
  if DCOM also fails it throws naming BOTH transports — never a fabricated "not found".
- `DcomSoftwareReader.cs` — read-only StdRegProv-over-DCOM read of the SAME Uninstall hives, ambient
  login only. **Load-bearing RV rules (do NOT copy `DcomLcuBuildReader.InvokeRegRead`, which lumps
  RV=5 into null):** EnumKey RV=0 → enumerate (null `sNames` = benign empty), RV=2 → hive absent
  (benign), RV=5/other → THROW; `Found=false` is legal only when every hive ∈ {0,2} with ≥1
  enumerated. OperationCanceledException rethrows FIRST at every layer (a timeout must surface as
  "check timed out", never "both transports failed"). Structure = `DcomLcuBuildReader`, NEVER
  `DcomVitalsProbe`'s swallow-to-null (Found is a bool that paints the cell red).
- `SoftwareShaping.cs` — pure parity seams: `Match` (DisplayName-OR-Publisher ordinal contains,
  DisplayName-sorted first), `MatchAcrossHives` (first hive with any match wins — never concat+sort),
  `NormalizeServiceState` (Win32_Service "Start Pending" → Get-Service "StartPending").
- VM: `CheckSoftwareRowAsync` gates on `IsGenuinelyOfflineAsync` first (both ping AND ambient DCOM
  dead → clean "Offline" cell, no connection attempt).

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
- `ViewModels/WorkspaceViewModel.cs` — the big VM. `InstallRowAsync` (routing inserts at the top), the LCU panel commands, `RebootAndVerifyCommand` (fleet-wide reboot-and-verify on the selected boxes — routes per box via `LcuRouting.RebootVerifyLaneFor`), `UnscannedStageTargets()` (returns 2016 targets that haven't been scanned this session — used by the Stage scan-gate), the filter enum/predicate, the two-bucket completion summary, `_appSettings` access. Also `OnIsUpdateModeChanged` (Health/Patching mode flip + the patch-only-filter reset when entering Health). WUG callers `SetWugMaintenanceAsync` / `CheckWugStateAsync` / `GetWugMaintenanceStateAsync` / `TestWugConnectionAsync` / `InstallWugModuleAsync` live here too. Custom-column sweeps register in `_customColumnSweeps` (CTS + captured spec names, via `RunSweepAsync`'s `onBegin` callback) so `RemoveCustomColumn` cancels a sweep whose every spec is gone; `WrapWithCompletion` does not count cancelled rows (the N/M counter freezes on Stop).
  - `RebootWaveRowAsync` — per-box reboot-and-verify step (routes by `LcuRouting.RebootVerifyLaneFor`; calls `RebootWaveLcuAsync` or `RebootWaveWuaAsync`; post-wave calls `ReportPostRebootOutcomeAsync`).
  - `ReportPostRebootOutcomeAsync` — post-reboot rescan: read-only `ScanAsync` + reboot-pending probe → `RebootOutcomeSelector.Select` → outcome string. Never triggers Install/Uninstall/Reboot.
  - `_waveThrottle` — static `SemaphoreSlim(256)`; concurrency width for the per-box offline-watch loops. Effectively unbounded so all selected boxes watch in parallel — the reboot-and-verify wave uses this (NOT the install/stage `_patchThrottle`), so a slow box's long commit never blocks a fast box's verify/report.
  - `_rebootTriggerThrottle` — static `SemaphoreSlim(12)`; caps simultaneous reboot *issuance* across the fleet. Shared across tabs to protect DCs/DNS/auth from a burst of simultaneous drops.
- `ViewModels/RebootTriggerGate.cs` — `IRebootGate` impl wrapping `_rebootTriggerThrottle` with optional jitter. Released the instant the reboot is issued, never held through the watch.
- **Fleet-wide reboot-and-verify entry point:** the grid right-click **Reboot & verify…** item (`WorkspaceView.xaml.cs` `OnRebootAndVerify` → confirm dialog → `RebootAndVerifyCommand`) is **Patching-mode-only** (gated by `vm.IsUpdateMode`, like the Scan/Install shortcuts). The 2016 LCU action-bar **Reboot Wave** button re-points to the same `OnRebootAndVerify` handler; it now enables on any selection (`RebootWaveButton.IsEnabled = SelectedComputers.Count > 0`), no longer 2016-only.
- `ViewModels/ShellViewModel.cs` — `CloseTab` and tab/list management.
- `WorkspaceView.xaml`(.cs) — ONE view, mode-swapped by `IsUpdateMode` (Health = `IsMachineMode`; Patching = `IsUpdateMode`), with two DataGrids that swap by visibility. The filter chips live in **two separate mode-gated StackPanels** (Health bar = 6 chips; Patching bar = full set incl. Updates, Server 2016, Not scanned, Scheduled). The LCU action bar (Border) is gated to Patching via a 3-condition MultiDataTrigger (ActiveFilter==Server2016 AND HasServer2016 AND IsUpdateMode). Status-pill label renames are **DataTrigger overrides in the Patching Status column only** — never edit the shared `PhaseChipLabelConverter` (it's also used by `ComputerDetailWindow.xaml`, so editing it leaks renames into the detail window / Health context). Cold-start responsiveness (1.14.2): a debounced (~150ms) re-layout on `SizeChanged`/`Computers.CollectionChanged` re-measures+re-arranges the grid so it isn't blank until a manual resize (`19f766b`); and `WorkspaceViewModel.AddComputers` defers the auto-check kickoff (vitals sweep + custom-column fill) to `DispatcherPriority.Background` so the grid paints before the sweep prologue (`0bfd362`).
- `MainWindow.xaml`(.cs) — bottom-dock open/size: the single mode-labeled `ActivityLogToggle` is the ONLY thing that opens the dock (no auto-open on row select); reopen honors the operator's saved `BottomDockHeight` as-is, floored against `WorkspaceGridMinHeight` so the grid can't vanish (`WorkspaceGridRow.MinHeight` bounds the splitter drag too). **Now hosts the completed `ui:NavigationView` shell** (LeftCompact pane + hamburger; Fleet → Health/Patching sub-items; Scripts; Cross-Domain RDP; Settings pinned bottom; mode chips + menu bar removed). The NavigationView refactor incl. Phase 4 is DONE — TODO: capture the as-built shell layout here in detail next time this file is touched.
- `AdaptiveLayoutController.cs` — layout controller.
- `SettingsPage.xaml`(.cs) — Integrations, Help & about. The LCU package folder + "This month's CU" (KB / target UBR) fields live here, plus the **Staged patching machines** card (lists the flagged hosts; per-row Remove + Clear all; re-seeds on expand; a remove/clear calls `MainWindow.ResyncStagedPatchingFlags`).
- `App.xaml`(.cs) — composition root. `App.OnStartup` also raises **`ThreadPool.SetMinThreads(64, 64)`** — load-bearing: the per-host WinRM open is a blocking `Task.Run(runspace.Open)`, and on a low-core box the pool's default min workers (= CPU count, e.g. 2) inject ~1 thread/500ms, serializing the ~28 sweep opens behind the slowest connect and freezing the UI on cold start. The min-floor lets the already-bounded opens run in parallel. **DON'T delete** (won't repro on a many-core dev box). See `docs/cold-start-freeze-and-threadpool-findings.md`.
- Converters: `EnumEqualsConverter.cs`, `UxConverters.cs`, `PhaseChipConverter.cs` (class `PhaseChipLabelConverter`; SHARED — Patching Status column + ComputerDetailWindow; do NOT edit for Patching-only label changes). Help text: `HelpContent.cs`.
- **Dialog sizing standard** (audit `fe4d68e`): modals use `CenterOwner`; fixed-content forms use `SizeToContent` + Min/Max (NoResize OK); content-heavy/list dialogs use `CanResize` + a ScrollViewer with the action buttons in their OWN row OUTSIDE the ScrollViewer (so they're always visible). `SoftwareCheckWindow` uses `SizeToContent="Height"` + `MaxHeight` so it opens fully visible and only scrolls on a too-short screen. Sizing attributes only — **never bind `Run.Text`** (the a0cb80a render-break class).
- `Computer.cs` (lives in **Vivre.Core/Models**, not Desktop — listed here because the UI binds it) — `OsBuild` populated in `ApplyVitals` — the 2016 predicate is `LcuRouting.Is2016(int?)` (Vivre.Core/Updates), the single source of truth for both the panel filter and routing; it is **not** a property on `Computer.cs`. `PatchState` derives from `UpdatePhase` + `RebootRequired`. `IsScheduled => ScheduledNextRun is not null`. `PatchPhase.Cleaned` → `PatchState.Done`. Also `LastInstallInstalledCount` / `LastInstallFailedCount` — runtime-only, non-observable `int?` counts: stamped by `InstallRowAsync` only for a REAL install outcome (Done/PendingReboot with a nonzero count; never a schedule registration or failed attempt), consumed (nulled) by `ReportPostRebootOutcomeAsync` after the post-reboot outcome message reports them once. **`RequiresStagedPatching`** (observable) — the operator's per-box opt-in for the 2016 DISM staging lane; seeded from `SharedSettings.StagedHosts` (the machine-wide shared store) on row add, drives routing + the Staged column. **`LcuVerifiedThisSession`** (runtime-only, non-observable) — set when a 2016 box's CU is confirmed at the target UBR this cycle (verify, 2016 reboot-wave commit, or the pre-dialog already-current check); cleared on re-stage. Lets the staged-update dialog skip an already-current box.
- **Staged-patching toggle — Desktop pieces:**
  - `StagedInstallDecisionDialog.xaml`(.cs) — the "Server 2016 staged update required" dialog: **Stage CU first** / **Install minor updates only** / **Cancel**, the Settings-vs-scan KB-mismatch warning, and the inline minor-only reboot caution (Proceed / Back). Returns a `StagedInstallChoice`.
  - `StagedInstallInteraction.cs` — the View-layer gate **every** Install entry point routes through (`MainWindow.RunInstallFlowAsync`, the right-click *Install selected*, and — as a safe skip-with-guidance fallback — *Install checked*). `ResolveAsync` plans → runs the already-current pre-check + re-plan → shows the dialog → carries out the choice (the flagged action + the normal install on the rest, concurrently). Cancel skips ONLY the flagged boxes; the rest of the fleet still installs. Also `RunStageWorkflowAsync` (the shared chip-Stage workflow: scan-gate + package-readiness loop + stage).
  - `WorkspaceViewModel` staged-patching methods: `PlanStagedInstall`, `ResolveAlreadyCurrentAsync` (the pre-dialog UBR currency check via `_patch.VerifyLcuAsync`, bounded by `_remoteSweepThrottle`, fail-open), `StageLcuForAsync` / `InstallMinorOnlyAsync` (the dialog's two actions), `SetStagedPatching` (toggle the flag + persist to `SharedSettings.StagedHosts` via the sibling-safe `_shared.Update`), `Server2016Targets()` is now flagged-only, and `HasStagedServer2016` (drives the Staged column visibility, re-tallied on row add + on a `RequiresStagedPatching` change). `InstallRowAsync` has a `minorOnly` param + a flag-aware 2016 branch (non-flagged → WUA).
  - `MainWindow.ResyncStagedPatchingFlags` — re-seeds every loaded row's `RequiresStagedPatching` from `StagedHosts` after a Settings remove/clear, so an edited list never leaves a stale flag.
  - `WorkspaceView` `StagedColumn` — the narrow "Staged" pill column (visible only on flagged 2016 rows; neutral styling, distinct from the amber "STAGED — needs Reboot Wave" tag). A `DataGridColumn` can't bind `Visibility`, so the View drives it from code-behind via the VM's `HasStagedServer2016` (`OnVmPropertyChanged` / `UpdateStagedColumnVisibility`). `BuildContextMenu` adds the **Mark as Staged patching** / **Remove Staged flag** items (2016 + Patching only, acting on the right-clicked row).

## ⚠ Computer.cs observability + the live-filtered grid (load-bearing, reusable)

**Stale-in-an-open-panel = a non-observable property.** If a value shows correctly in a freshly-opened
Machine Details panel/tab but won't update in place after a re-check (e.g. Check Vitals), the property it
binds through isn't raising PropertyChanged. Two flavors: a plain auto-property (`Vitals`, `VitalityReasons`
— fixed `5e6ddee` via `[ObservableProperty]`) or a computed property with no notify (`VitalsSummary`,
`LastRebootDisplay`, `MonthlyCuDisplay`, `LcuPackagesFolder` — still deferred where only the grid reads them).
Fix = make the *container* observable: one `[ObservableProperty]` on `Vitals` re-resolves every `Vitals.*`
reading at once.

**Before making ANY `Computer.cs` property observable, run this 2-question safety check** (the `7d8abd4`
cross-thread crash was an off-thread write to a *live-filtered* property re-shaping the grid's CollectionView
on the wrong thread):
1. **Is the property in the live-filtered set?** (the predicate inputs in `WorkspaceViewModel.cs` ~855-862:
   `Name`, `IsOnline`, `PatchState`, `RebootRequired`, `LastError`, `UpdateError`, `UpdatesAvailable`,
   `MissingUpdates`, `VitalityBand`, `OsBuild`, `UpdatePhase`, `ScheduledNextRun`.) If YES, a change re-shapes
   the grid → it MUST be written on the UI thread (marshal, or route via `IProgress`).
2. **Is the write on the UI thread?** Confirm the call path keeps the UI `SynchronizationContext` (no
   `ConfigureAwait(false)` / `Task.Run` upstream) — and remember callbacks handed to a Core runner run on the
   runner's `ConfigureAwait(false)` context, so those must marshal.

Non-live-filtered + on-UI-thread (`Vitals`, `VitalityReasons`) = safe to make observable — the opposite
direction from the crash.

## Cross-Domain RDP
- `source/Vivre.Desktop/RdpSessionView.xaml.cs` (+ `.xaml`) — the embedded RDP host; owns control creation,
  `LocalScale()` (pinned to `(100,100)` for the FCM fix, `a7b8833` — THE PIN CARDINAL, read at exactly two
  sites: the connect block and `ResizeRemote`; gate greps after any RDP commit: `= LocalScale();` → exactly
  2, `_rdp.UpdateSessionDisplaySettings` → exactly 1), the client-side **ZoomLevel** magnification (logical
  framebuffer, SmartSizing off while zoomed; zoom parks to 100 BEFORE a full-screen entry attempt — mstsc's
  order — and a failed switch is logged + un-latched in both directions), and the **verified re-fit engine**
  (spaced sends, read-back verify + retries, even-both-dims sizes per MS-RDPEDISP, sends deferred while a
  drag or mouse button is live). Control stack: WPF → `WindowsFormsHost` (`RdpHostElement`) → WinForms
  `Panel` → `AxMsRdpClient9NotSafeForScripting` (the v9 OCX).
- `source/Vivre.Core/Rdp/RdpDisconnectClassifier.cs` — pure, unit-tested disconnect classifier
  (keep-by-default, close-by-exception): the tab closes ONLY on `ExtendedDisconnectReasonCode` 12
  (LogoffByUser — measured on both sign-out paths) while connected with no auto-reconnect in flight;
  codes 4/6 (the old wrong-enum silent closes) and ALL unknown codes KEEP the tab, and
  `GetErrorDescription` is reachable only for the error outcome (via `Message` — contract test-pinned).
- `source/Vivre.Desktop/ViewModels/RdpSessionViewModel.cs`, `ViewModels/CrossDomainRdpViewModel.cs` — the RDP
  session + host-tree view-models.
- `source/Vivre.Desktop/CrossDomainRdpView.xaml`(`.cs`) — the Cross-Domain RDP UI (host tree + session tabs);
  per-host settings resolve via `_creds.Resolve(host, RdpTree.AncestorsOf(_tree, host))` in `ConnectTo` — the
  hook a future per-host scale setting would use.
- **Note:** the Failover Cluster Manager context-menu fix is the 100%-scale pin (`a7b8833`); embedded-RDP
  magnification **shipped** (client-side zoom; the session stays at 100% so FCM keeps working) — see
  `vivre-rdp-scaling-and-fcm-findings.md` for the full arc, the re-fit engine, and the instrument lessons.

## Settings / data
- **Settings are SPLIT across TWO stores (v1.16.0) — personal per-operator vs shared machine-wide. Everything was formerly in the Roaming per-user `AppSettings`; the operational keys moved OUT to the shared store this release. Neither store holds credentials (session-only, in memory). No in-app migration — a fresh install starts at the defaults below.**
- `AppSettings` / `AppSettingsStore` (**Vivre.Desktop**, `AppSettingsStore.cs`) — the PERSONAL, per-operator store: `%APPDATA%\Vivre\settings.json` (Roaming `ApplicationData`). Holds `Theme`, `SoftwareServiceMap`, `CustomColumns`, `HiddenColumns`, `AutoCheckOnLoad` (default true), `MaxSimultaneousInstalls` (default 50, range 1–200), `WugStateConcurrency` (default 2, range 1–4), `NavPaneOpen` (default false), `BottomDockHeight` (default 170). Process-wide static cache; `Save` re-seats the cache synchronously (UI thread) then writes off-thread via a sibling-safe `MergeOntoDisk` (overlays the POCO keys onto the on-disk JSON, preserving keys this build doesn't declare; REFUSES on a present-but-unreadable file rather than dropping unread keys). Atomic write via `AtomicFileWriter`.
- `SharedSettings` / `SharedSettingsStore` (**Vivre.Core**, namespace `Vivre.Core.Configuration`) — the SHARED, machine-wide OPERATIONAL store: `C:\ProgramData\Vivre\settings.json` (`CommonApplicationData`). Holds `MonthlyCu` (`Kb`, `Arch` = "x64", `TargetUbr`, `MonthTag` month label, computed `Display`), `LcuPackagesFolder` (default `C:\Vivre\VivrePackages`), `PackagesFolder` (the stage-software dropdown source, empty default), `WugServer` (default `10.70.25.111`), and `StagedHosts` (`HashSet<string>` OrdinalIgnoreCase — the source of truth behind `Computer.RequiresStagedPatching`; `Load` re-normalizes via `StagedHostMatching.Normalize` after each deserialize because a JSON round-trip resets the comparer to ordinal). The folder is created on first save WITH an inherited Authenticated-Users-Modify ACL (baked into `DirectorySecurity` at create; an existing folder gets a best-effort ACE ensure) so every operator can read/write. `Load` = fresh UNCACHED disk read, tolerant (safe defaults + a loud `ActivityLog` report on any read failure, never throws — called from unguarded ctors). **The file has EXACTLY ONE writer — `Update(Action<SharedSettings>)`:** re-reads the file fresh, REFUSES (throws) if a present file can't be read/parsed (a degraded read must NEVER feed a save — the wipe vector that once cleared `StagedHosts`), applies the delta, then merges only the POCO-declared keys onto the raw on-disk JSON — preserving every unknown/future key and deleting ONLY the two obsolete duplicate keys (`MaxSimultaneousInstalls`, `WugStateConcurrency`, now personal). A reflection guard THROWS if a credential-shaped field (name matching `password|secret|credential|token|pwd`, or a `SecureString`/`NetworkCredential` type) is ever added. **`MonthlyCu.ExpectedSizeMb` was REMOVED** — it was display-only; the package is matched by KB + architecture, never size. Pure case-insensitive `StagedHosts` membership helpers live in `StagedHostMatching` (Vivre.Core/Updates).
- **Numeric Settings boxes validate + snap back on `LostFocus` ONLY — never `TextChanged`** (`SettingsPage.xaml`/.cs — `MaxInstallsBox` → `OnMaxInstallsChanged`, `WugStateConcurrencyBox` → `OnWugStateConcurrencyChanged`). `TextChanged` fires per keystroke and reverts a half-typed value instantly (at "2" with range 1–4, every appended digit is momentarily out of range → revert), so the box feels dead to normal typing. This shipped latent for a month in `d8ac98c` (the installs box) and was faithfully copied into the WUG concurrency box by "mirror `MaxSimultaneousInstalls` exactly"; fixed in `96e7f70` by deleting the two `TextChanged` attributes. **`LcuUbrBox` is the correct reference pattern** — identical snap-back logic, `LostFocus`-only, types fine. Adding any future numeric setting: wire snap-back to `LostFocus` only, and do NOT mirror any box that carries `TextChanged`.
- `source/Vivre.Core/Computers/ComputerListStore.cs` — the computer list store.
- `source/Vivre.Core/IO/AtomicFileWriter.cs` — crash-safe whole-file write (same-dir temp + `File.Replace`; `File.Move` first-write fallback). Behind BOTH `AppSettingsStore.Save` (`d600009`) and `ComputerListStore.Save` (`1add64f`) — a crash mid-write leaves the prior good file (settings.json / a named list) intact. Callers serialize their own writers.
- `RebootOutcomeMessages.cs` (Vivre.Core) — the 8 ready-to-use reboot-and-verify outcome strings ("Back online · installed N · up to date", "… N remaining", "… N failed", `BackOnlineRescanFailed()` → "Back online · couldn't rescan — re-check", and `BackOnlineRebootUnknown()` → "Back online · couldn't confirm reboot state — re-check"). Wired via the pure, truthfulness-first `RebootOutcomeSelector.Select` (tri-state `bool?` reboot-pending; nullable consume-once install counts — the "installed N" clause is omitted when no meaningful install ran) → `WorkspaceViewModel.ReportPostRebootOutcomeAsync` — a scan failure, probe failure/timeout, or failed updates never read as a clean "up to date".

## Pure decision helpers (Vivre.Core/Updates)
Extracted UI/IO-free predicates, each unit-tested:
- `SoftwareShaping` (Vivre.Core/Software) — the software check's match/sort/service-state parity seams (see the Software check section above).
- `RebootVerifyMenu.ShouldOfferRebootVerify(Computer, bool isUpdateMode)` — per-row visibility of the right-click **Reboot & verify…** item: Patching-mode AND (`PatchState.RebootPending` OR `PatchState.Error` with `RebootRequired == true` STRICTLY — a null/unknown reboot state never enables it). The Error arm (which includes the Unreachable "Can't reach WU" phase) is the AZRVISIONBT-SQ1 fix — a failed install with a confirmed pending reboot must still offer the DCOM-capable Reboot & verify, since Force reboot's WinRM channel dead-ends on Kerberos-broken boxes.
- `Lcu2016RowState` — maps terminal/in-flight agent status onto a 2016 staged-patching grid row; enforces the load-bearing **Deferred ≠ Staged** invariant.
- `ScopeToggleRule` — on the Applicable/Installed scope-toggle, preserves a row's existing message for terminal + in-flight states instead of swapping in the target scope's cached scan message.
- `ComponentCleanupClassifier` / `ComponentCleanupMessages` — 2016 component-cleanup outcomes, incl. the `CleanedFilesLocked` access-denied case (DISM exit 5 = EDR/AV holding WinSxS handles → neutral **Cleaned**, not red).
- `ScheduledTaskCancelOutcome` — verify-by-absence for the scheduled-task cancel: the Scheduled chip clears ONLY on an exact full-line `REMOVED` with a clean error stream; anything else keeps it ("task may still fire") — the no-false-green rule on a reboot path.
- `ScheduleRegistrationOutcome` — the register-side ASYMMETRY: an unconfirmed reboot-schedule registration (timed out, dropped mid-request, cancelled mid-request, or any unprovable escape — `IsUnconfirmedFailure` buckets the thrown types) is treated as SCHEDULED ("couldn't confirm — verify on the box"), never silently unscheduled; a row goes dark ONLY on proof the command never ran (connect-phase loss / Kerberos / shell-init) or the box's own failure report.

## Tests
- `source/Vivre.Core.Tests/...` — **984 green** (v1.16.0, verified via `dotnet test` on 2026-07-18) — run `dotnet test` for the exact count; the increments below are point-in-time history (344 at the WUG resolution; 360 after the pluggable-wave
  refactor; +7 across the reboot-and-verify build; +11 across the smart-scan build; +49 across the
  staged-patching toggle; +61 across the transient WUA retry / no-false-green build — `TransientWuaError`,
  `TransientRetryRunner`, the WinRM + SMB non-clean-search "never up-to-date" tests, and the
  `PatchPhase.Unreachable`→Error mapping; +10 WUG maintenance-state parse tests in `3f8ada1` —
  `WugMaintenanceStateParseTests`: tri-state true/false/null, single-object `devices` shape, marker
  extraction, case-insensitive keys, fail-open on malformed/empty output; +26 software-DCOM-fallback
  tests in `f4fad69`/`fa837e6` — `SoftwareShapingTests` (name/publisher match, cross-match-type sort,
  hive precedence, empty-≠-failed, service-state normalization) + `SoftwareProbeRoutingTests`
  (Kerberos AND session-loss reroute to DCOM, double-failure throws naming both transports — never a
  false Found=false, OCE propagates unwrapped, no-reader passthrough)). Includes the wave behavior tests
  (graceful→forced, not-ready refusal, rollback=red, late-return-still-verifies-green, never-returns=red,
  **per-box independence**, **reboot-gate enter/release**), the LCU classifier + `RebootVerifyLaneFor`
  routing tests, the `RebootOutcomeSelector` + `ReadyConfirmation` tests, the phase→state mapping tests,
  the `RebootOutcomeMessages` tests, the
  `ParsePreflight` result-classification tests (now incl. the safe-default contract + `__WUGRESULT__`
  marker extraction — failure cases must NOT claim the module is missing), the
  **`WritePs51Script_writes_utf8_with_bom`** BOM regression guard (`Vivre.Core.Tests/Wug/`), and
  the **`StagePreconditions`** unit tests (IsAlreadyStaged, IsAlreadyCurrent fail-open, UnscannedThisSession),
  and the **staged-patching toggle** tests — `StagedHostMatching`, `Lcu2016CuMatcher` (`FindCuKb` ambiguity +
  `CuKbs` conservative exclude + .NET exclusion), `StagedInstallPlanner` (`Plan` partition + KB mismatch +
  `PartitionByCurrency` fail-open: all-current → none, mixed → only non-current, null/Unreachable → included),
  and the override-aware `RebootVerifyLaneFor(osBuild, requiresStaging)` routing. **+29 across the WUG
  streaming state-check** (823 → 852): the streaming-parse tests (`ParseDeviceLine` kept in lockstep with
  `AddDevice`; `ParseMaintenanceState` now REQUIRES `__WUGRESULT__` and rejects a trailing `__WUGDEV__`
  line as the summary — the fabricated-clean-read guard), the `WugRowText` string locks (incl. the distinct
  `NotChecked`), `ComposeAbortError` / `ComposeStoppedMessage`, and **6 real-`powershell.exe` process tests**
  (incremental per-line delivery while the child runs, the stall-watchdog kill, caller-cancel kills the
  child in both launchers, and the PSModulePath strip on both launch paths) — the process tests took the
  suite from ~5s to ~23s. **+21 across the identity-verify resolver** (`b67ed55`, 852 → 873): the
  dot-boundary `Test-WugNameMatch`, the MatchedByName/ByIp/NoDevice/Ambiguous/LookupError outcomes,
  error≠no-match, and the set-path fail-safe. **+24 across the pooled state read** (873 → 897): +22
  pool/clamp/fan-out-trap tests (`WugConcurrencyTests` — `ClampConcurrency` bounds, the `StateReadMaxConcurrency`
  ceiling, and the T1/T2 + single-sourced-resolver + module-override string-locks; `WugPoolProcessTests` —
  real-`powershell.exe` pool streaming/connect-once-per-runspace/error-isolation/N=2-overlap/degradation
  tests) + 2 (the real-module smoke test + the set-path parse lock). The pool process tests ride the committed
  stub-module fixture via `VIVRE_WUG_MODULE_OVERRIDE`, so the suite wall-clock stayed in its ~87s ballpark
  instead of paying the real module's ~8s cold-load per runspace. **+87 across v1.16.0** (897 → 984): the
  settings-split (`SharedSettingsStoreTests` — one-writer merge / refuse-degraded-read / credential guard /
  obsolete-key removal, `MonthTagSuggestionTests`), the CU auto-read (`MsuIdentityTests` — product-pin +
  KB cross-check + every refusal reason), Force reboot's channel runner (`ForceRebootRunnerTests` — WinRM,
  the narrow Kerberos→DCOM fallback, and no-fallback on ambiguous failures), the reboot-message narration
  (`RebootMessageTextTests`), and the `Unverified` / `RebootOutcomeKind` plumbing (`RebootOutcomeSelectorTests`,
  `ComputerPatchStateTests`, `ScopeToggleRuleTests`, `RebootVerifyMenuTests`).

## Docs in repo

- The doc inventory (every doc, one-line purpose, read-when cue) lives in **docs/README.md** — the single map.
- **`tools/`:** `Get-VivreFreezeLog.ps1` — the harvester for the freeze/disconnect instrument log lines (see docs/freeze-hunting-playbook.md ▸ Tools); `RemoteRun` — the dev console for exercising remote PowerShell.
- Retired: the nav-refactor plan doc (refactor complete) and the overnight Kerberos status doc (spent) were removed; their content lives in CLAUDE.md / windows-patching-lane.md / this file.

## Restore points
Use `git log` - it is the authoritative restore-point list. (A hand-maintained commit-SHA list used to live here; it decayed on every commit and after a history rewrite, so it was removed in the 2026-06-23 audit.)
