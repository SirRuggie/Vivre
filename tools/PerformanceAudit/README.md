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
