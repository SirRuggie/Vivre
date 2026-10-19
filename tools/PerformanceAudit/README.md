# Local performance audit

Runs isolated probes against existing Vivre production classes. Windows and the .NET 10 SDK are required. From the repository root:

```powershell
dotnet run --project tools/PerformanceAudit/PerformanceAudit.csproj -c Release -- docs/performance-local.json
```

Use a new output filename for each run; the supplied output file is overwritten. The harness creates and deletes its own uniquely named temporary directory containing dummy files and computer names. It initializes Vivre.App resources without running application startup, shows no windows, performs no server operations, and does not save user settings. Workspace construction reads existing local settings. Remote dependencies are inert and monitoring is disabled before inserting rows. No new packages or replacement application services are introduced.

The probes cover actual collection filtering, Clear versus Remove subscription lifetimes, ActivityLog dispatcher admission, SplitThrottle contention, protected staging registry parsing/writes, and package manifest preparation. Run sequentially without concurrent builds/tests. Timings depend on JIT, machine load, filesystem and antivirus. They are diagnostic samples, not release thresholds.

Limits: no visible-window input/compositor timing, native RDP, network, live patching, disk log sink, or full-app idle/working-set soak. The rendered probe exercises the actual WorkspaceView/DataGrid offscreen at 1280x720 with inert dependencies and local theme resources. Logging intentionally pauses the UI dispatcher and uses a counting sink; it measures backlog amplification, not a naturally occurring freeze. Its callback count follows the single scheduled batch drain; assertions verify queue caps, retained recent entries, overflow reporting, sink admission, and Clear ordering. Heap deltas are managed memory only; allocation totals are not retained-memory measurements. The reset probe deliberately retains an old row. A collectible object cycle alone is not a leak.

Reflection into production internals and SemaphoreSlim queue fields is diagnostic-only and may require updating after implementation/runtime changes. Queue traversal is sampled when the tested pool is held or draining; it is not a production monitoring API. Do not integrate this executable into application startup.

See [the October 14 report](../../docs/vivre-performance-review-2026-10-14.md) for findings, raw evidence, controls and excluded samples.

To isolate WPF live-property subscription cost, run this optional control/experiment/reversal (3,190 dummy rows, explicit Refresh, twelve properties versus Name only):

```powershell
dotnet run --project tools/PerformanceAudit/PerformanceAudit.csproj -c Release -- --filter-experiment docs/filter-experiment-local.json
```

This temporarily changes the diagnostic view only. Production always tracks Name plus the exact dependencies of its active state filter. The post-fix runs also assert live inclusion/removal for state changes. Historic pre-fix JSON and intermediate post-fix run 1 are retained; final results are identified in the implementation report.

Settling probes wait at least 300 ms (including the production 200 ms debounce) before evaluating the result. Their settle duration is not a direct timer or input-latency measurement. Immediate state-refresh timings and the isolated property experiment measure synchronous view work separately.

## Explicit shared-drive smoke check

Only with authorization for the destination, use a fresh release-* child of Z:\Vivre\testing:

```powershell
dotnet run --project tools/PerformanceAudit/PerformanceAudit.csproj -c Release -- --share-smoke 'Z:\Vivre\testing\release-unique-name' 'docs/share-smoke-result.json'
```

Uses production CU copy/hash and package publication/rollback methods on dummy files over the mapped drive. Leaves dummy files for inspection. It does not exercise remote PowerShell, agent launch, install, cleanup, reboot, or RDP.

The main probe now loads production application resources (without running App.OnStartup), constructs a MainWindow with no operational services, and checks its real completion InfoBar offscreen. It verifies overflow/error severity, ordering and dismissal; no visible window or native tray interaction is asserted. The existing app icon is linked as a resource in the harness because MainWindow uses an entry-assembly pack URI. Run just this check with `--banners-only <output.json>`. Workspace probes also verify task tracking/display advancement and preservation of a cancellation warning.


The default run also checks the F33/F38/F39/F43 fixes using isolated production controls: blocked package loading with a responsive dispatcher, stale result protection, explicit reboot scheduling/count, accessible field names and removed custom-column values. A focused run is available with `--audit-fixes-only <new-output.json>`. The column probe injects a temporary preference store and flushes it before cleanup. These are WPF automation-peer/offscreen checks, not screen-reader, real RDP or live servicing validation.

`tools/validate-local.ps1` runs the Release solution build, Core tests and these existing probes, saving logs/TRX/JSON into a new output directory. Missing file-symlink privileges produce an explicit skipped theory instead of silently counting unexercised assertions as passed. Directory-reparse tests use real temporary junctions. Signing warnings must be reviewed separately before publishing.

The script-execution probe uses the actual runner view model with temporary script libraries and a fake transport. It verifies host-conflict refusal, error/cancellation uncertainty, captured targets, local/remote bounded APIs and per-host/aggregate output bounds. It does not execute the displayed dummy mutation text.

The script-editor probe uses actual runner and library-page controls with temporary scripts and injected choices/errors. It checks Save/Discard/Cancel, refresh preservation, close vetoes and built-in notices without interactive dialogs or production stores. The status probe also verifies that an unloaded live-status control can be garbage-collected.

`--cu-stage-only <output.json>` measures actual FullPackageLcuLane call-entry time and queued dispatcher callback latency for 1 and 8 dummy targets using a 128 MiB local dummy MSU, real SHA-256 validation and an inert SMB implementation. It performs no server operation. First-sample callback latency includes cold WPF dispatcher startup; compare synchronous call-entry and the subsequent 8-target sample, not cold callback latency as network latency.

The CU-only run also exercises the actual WorkspaceViewModel with a blocked dummy preflight: dispatcher responsiveness, busy/Stop state, prompt cancellation, and a canceled retry that cannot accumulate a second blocked reader. It uses a temporary settings store and no production hosts or writes.

### Install-anyway approval probe

Run `dotnet run --project tools/PerformanceAudit -c Release -- --install-override-only "$env:TEMP\vivre-install-override.json"` from the repository in PowerShell. This invokes the real WorkspaceViewModel against an inert patch service and verifies approval is true for the approved immediate install, false for the next default install, and false for a scheduled install. It does not contact any target, show a window, or install updates.

Software display probe: `dotnet run --project tools/PerformanceAudit -c Release -- --software-display-only "$env:TEMP\vivre-software-display.json"`. Exercises the real grid at 720 pixels and live Details diagnostics using dummy data, without remote operations.

### AD Linux label probe

`dotnet tools/PerformanceAudit/bin/Release/net10.0-windows/PerformanceAudit.dll --ad-linux-only <output.json>` runs the production workspace and Name template with 1,000 dummy rows and delayed fake AD. It checks batching, UI heartbeat while waiting, Linux rendering, unchanged operational OS facts, and stale-result protection after rename/remove/reset/dispose or directory outage. No live AD, target operations, or production writes. Core `AdLinuxLabelLookupTests` additionally exercise the actual cache/serialization/background service with a fake transport.

## Per-tab update options

```powershell
dotnet run --project tools/PerformanceAudit -- --tab-options-only tab-options-local.json
```

Exercises real workspaces sharing constructor defaults, checks bidirectional isolation of source/drivers/exclusions/scope and new-tab defaults, captures actual scan/install service arguments with inert services, and checks production Patching header and toolbar bindings across tab switches. Also checks scheduled installs, uninstall KB scope, settings changed during a suspended scan, and compact/expanded toolbar labels. No remote operations or production writes; this does not validate a visible window at every DPI or live update services.

## Workspace and list protection (U-2–U-5)

```powershell
dotnet run --project tools/PerformanceAudit -- --workspace-safety-only workspace-safety-local.json
```

Uses real workspace membership/selection, shared approval helpers, and production paste/name UI with isolated dummy settings and lists. Checks cancelled and stale approvals, busy work, a cancellation-ignoring delayed monitor, retained row order/results, saved-list opening, exit decisions, a 3,190-row clear with one Reset, and a 3,190-to-one replacement with actual DataGrid selection retained and layout included in timing. No live servers, production writes, or visible-window automation; storage race/name tests live in ComputerListStoreTests.

The workspace probe also covers U-6/U-7/U-9/U-19: saved identity and membership-only changes, failed saves, restored membership, deletion detachment, actual Paste with mixed separators, fixed Name columns in both grids, and mode-specific action text. These checks reuse the existing isolated workspace probe.

`--patching-clarity-only <output.json>` checks U-10/U-11/U-15 using production controls and inert callbacks: per-tab option summaries, install/reboot/cancel routing, schedule counts and host timezone, DST offsets/gaps, and captured target identity. No live operation or production write.

`--settings-clarity-only <output.json>` exercises U-12/U-13/U-14 with actual Activity and Settings controls, temporary shared/personal stores, and a sink-free logger: origin display/search/copy, grouped saves, conflict/reload/failure preservation, package-save integration, numeric errors and layout stability, plus existing bounded-log probes. No clipboard mutation, production settings, or live targets.

`--library-search-only <output.json>` checks U-16 using the production menu helper in an offscreen native popup: keyboard focus, routed text input, clear-button automation invocation, empty matches, and duplicate recovery labels. It also runs both script editors through dirty filtering, Cancel/Discard/Save selection, and rename/category saves during search, plus existing editor guards. It does not simulate physical input or validate high-DPI/screen-reader behavior; no live hosts or production files are touched. Core ComputerListStoreTests cover recovery/restarts/collisions/failed moves and junction rejection; file-symlink cases explicitly skip when the OS privilege is unavailable.

`--tab-handoff-only <output.json>` checks U-8 through the production Health tab-header context menu, handler, shell, and workspaces with temporary settings and dummy names: inactive source, full membership despite filters/selection, fresh rows/defaults, unchanged source/existing tabs, navigation, idle target with auto-check enabled, empty/repeated/stale/non-Health sources, and factory failure. No live targets or visible-window/high-DPI testing.

`--layout-accessibility-only <output.json>` exercises U-21/U-22 in dark and light themes: Health/Patching at 560/800/1100 DIPs, raster output at 96/120/144/192 DPI, key dialogs at minimum/default widths, settings labels and script-toolbar layout, programmatic focus traversal, tab-menu handler routing, focus-ring distinction, and Automation names/state bindings. PNG evidence is written under `u21-renders` beside the JSON. No production startup, live operations, or settings writes. This is not actual Windows monitor DPI/text scaling, physical keyboard/Enter/Escape input, or spoken screen-reader validation; name-dialog default/cancel wiring is asserted, not its native key handling. See `docs/ui-ux-u21-u22-validation.md` for retained gaps.

### Actions menu (U-18)

Run --actions-menu-only with the existing harness and an output JSON path. Covers four selected-target shortcuts, stale tab/selection/name/membership rejection, empty-selection guards, and right-click grouping in both modes. Uses dummy machines and intercepted dialog dispatch; no remote operations.

U-25/U-26 extend --actions-menu-only with explicit maintenance naming and disabled/enabled Copy selected rows, SCCM, and Enable WinRM states in both modes. U-27 extends --tab-handoff-only with production tab-menu binding, inactive Health/Patching save target resolution, missing/removed target rejection, and deferred save-guard teardown. Save persistence/overwrite regression remains --workspace-safety-only. These are local WPF probes, not a physical mouse/keyboard study.
