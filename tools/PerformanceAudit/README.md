# Local performance audit

Runs isolated probes against existing Vivre production classes. Windows and the .NET 10 SDK are required. From the repository root:

```powershell
dotnet run --project tools/PerformanceAudit/PerformanceAudit.csproj -c Release -- docs/performance-local.json
```

Use a new output filename for each run; the supplied output file is overwritten. The harness creates and deletes its own uniquely named temporary directory containing dummy files and computer names. It creates a basic WPF Application, not Vivre.App, opens no windows, performs no server operations, and does not save user settings. Workspace construction reads existing local settings. Remote dependencies are inert and monitoring is disabled before inserting rows. No new packages or replacement application services are introduced.

The probes cover actual collection filtering, Clear versus Remove subscription lifetimes, ActivityLog dispatcher admission, SplitThrottle contention, protected staging registry parsing/writes, and package manifest preparation. Run sequentially without concurrent builds/tests. Timings depend on JIT, machine load, filesystem and antivirus. They are diagnostic samples, not release thresholds.

Limits: no rendered DataGrid, native RDP, network, live patching, disk log sink, or full-app idle/working-set soak. Logging intentionally pauses the UI dispatcher and uses a sink-free logger; it measures backlog amplification, not a naturally occurring freeze. Its callback count follows one callback per completed submission. Heap deltas are managed memory only; allocation totals are not retained-memory measurements. The reset probe deliberately retains an old row. A collectible object cycle alone is not a leak.

Reflection into production internals and SemaphoreSlim queue fields is diagnostic-only and may require updating after implementation/runtime changes. Queue traversal is sampled when the tested pool is held or draining; it is not a production monitoring API. Do not integrate this executable into application startup.

See [the October 14 report](../../docs/vivre-performance-review-2026-10-14.md) for findings, raw evidence, controls and excluded samples.
