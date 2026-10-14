# Performance implementation checkpoint — October 14, 2026

> Point-in-time case file — never edited after the fact.
> Implements the F13–F20 assessment against baseline `89c1840`. F12 remains skipped. No release version bump, deployment or live server action.

## Delivered behavior

- **F13:** free-text filtering coalesces rapid input with a 200 ms timer. State chips apply immediately. Live filtering observes Name plus only the active state predicate dependencies, avoiding unnecessary WPF subscription rebuilds. Counts are cached and updated from view membership changes. The final WPF view refresh is still synchronous and scales with fleet size; the measurements below distinguish typing cost from final refresh cost.
- **F14:** registry loading deduplicates with HashSet; registrations batch, unchanged writes are skipped, and initial persistence runs off the UI thread. Unique owned transfer/archive/candidate paths retire only after confirmed cleanup. Durable GUID-temporary-file writes and disk flush remain. Existing unknown history, final targets, sources and uncertain recovery paths stay protected. Same-process instances synchronize by file; cross-process coordination remains a prior limitation.
- **F15:** a tracked subscription set detaches outgoing rows on Reset and all rows on Dispose; disposed workspaces cannot restart dirty/filter timers.
- **F16:** at most 512 recent pending log entries and one queued UI drain, with an omission summary; existing 2,000 displayed-entry cap remains. Every message still attempts the Serilog sink. Reentrant writes are deferred, and Clear invalidates older captured batches. Sink admission is tested; actual disk-full durability is not guaranteed.
- **F17:** preparation and hashing check cancellation between reads. SMB passes manifest JSON as a bound parameter. WinRM manifests above 32 KiB UTF-8 use 64 KiB chunks through the existing owned transfer lane; offsets, owner, regular-file shape and SHA256 are checked before publication. Metadata still materializes a full JSON byte array and scales with file count. Existing publication/rollback remains non-cancellable once replacement starts; blocking filesystem calls and an individual local File.Copy cannot be interrupted mid-call; the existing File.Copy behavior is preserved.
- **F18:** passive semaphore races cancel and observe their losing wait; a concurrently acquired second permit is returned exactly once.
- **F19:** publication gates count holders and waiters, remove an idle key under the same lock, and tolerate cancelled waiters/double disposal. This follows the existing coordinator ownership pattern.
- **F20:** completion backlog retains four recent pending banners plus one overflow summary; every original summary goes to the existing log. Native tray/visible banner interactions were reviewed, not exercised through a live MainWindow.

## Independent review and corrections

Review found and corrected a durable-writer regression before delivery (restored the existing unique temporary file and Flush(true)), and a UI logging reentrancy bug (Clear could otherwise resurrect captured entries). Tests/probes were strengthened where an initial setup could miss actual contention or return before a debounce fired. A controlled all-properties / Name-only / all-properties reversal established the WPF subscription cost. The first dynamic-property run also caught a direct Refresh call inside DeferRefresh; it was replaced with a deferred Filter assignment and all state-transition checks then passed. Staging cleanup attestations, corrupt-manifest rejection and gate ownership were independently challenged; no remaining actionable staging defect was identified by that review.

## Validation

- Full Release Core suite: **1,454 passed**, 5m38s. The runner reports zero skips, but existing privilege-conditional reparse tests can return early; this does not establish those branches.
- Focused staging/gate run: **69 passed**, including actual local Windows PowerShell 5.1 large-manifest publication, corruption refusal and owned cleanup.
- After final registry-writer restoration: **31 registry/throttle tests passed**. After preserving the original File.Copy behavior: **18 deployment/preparation/gate tests passed**. These focused reruns cover late changes without repeating unrelated tests.
- Final Desktop/Core/agent/audit Release build: zero errors; existing missing agent-signing-certificate warning remains. Nothing was deployed.
- Final probes exercise 15 live Name/state-property inclusion/removal cases (one Reset per state-chip transition), cached counts, disposed-row collection, bounded log backlog, reentrant Clear/Add, full sink admission, losing-waiter drainage, registry reads and offscreen real-grid rendering. Final run evidence below.
- Banner cap/summary/logging received source review and compiled with Desktop. Visible banner/tray interaction remains unverified; it is not included in the automated pass count.

[Final run 2](vivre-performance-after-2026-10-14-run2.json) and [final run 3](vivre-performance-after-2026-10-14-run3.json) use the final implementation. [Intermediate run 1](vivre-performance-after-2026-10-14-run1.json) predates the live-property optimization and is retained transparently. The [controlled property experiment](vivre-performance-live-properties.filter-experiment.json) measured 3,190-row direct refresh: all twelve properties 473–617 ms, Name only 14–34 ms, then all twelve again 301–422 ms. This supported reducing subscriptions while retaining each active predicate's exact dependencies.

| Measure | Final local runs 2/3 |
|---|---|
| 319 rows | Ten rapid text edits: 0.781–0.843 ms; immediate return-to-All refresh: 1.2–1.9 ms. |
| 3,190 rows | Ten rapid text edits: 0.026–0.034 ms; immediate return-to-All refresh: 15.1–16.8 ms. |
| 10,000 rows | Ten rapid text edits: 0.073–0.087 ms; immediate return-to-All refresh: 51.2–85.6 ms. |
| Registry, 10,000 paths | Reads 13.2–16.7 ms (baseline 488–864 ms); duplicate registration 12.9–13.1 ms, with no rewrite. |
| Log, 10,000 paused-UI messages | 1.03–1.04 MiB retained managed data (baseline ~24.6 MiB); one scheduled drain, 512 recent messages plus summary; all 10,000 admitted to the counting sink. |
| Retention / throttle | Both Remove and Reset workspaces collected while old rows remained held; zero losing waiters after 3,190 contended passive acquisitions. |
| Real offscreen grid, 3,190 rows | Correct final 1,000 matches, 16 realized rows; settle+render 698–745 ms. Intermediate pre-subscription-optimization run 1: 1,219 ms. |

Original filtering samples measured each immediate key plus a count read. Final rapid-typing samples measure setters, with the coalesced refresh measured separately; those are different quantities and must not be divided into a speedup ratio. Settling probes impose a minimum 300 ms observation window, including the 200 ms debounce. Rendered times additionally include dispatcher/binding work and final offscreen rendering; they are not visible input-latency guarantees. State-filter timings still grow at large row counts. Manifest hashing/file enumeration still takes time proportional to file count; cancellation and transport bounds address the identified risk without skipping integrity checks.


The offscreen rendered probe uses the actual WorkspaceView/DataGrid and a fixed viewport with existing WPF-UI resources. It excludes a visible Window, input/compositor latency, native RDP and the production startup graph. Its app-level empty-state text styles are minimal equivalents; it is not a pixel-perfect app screenshot. The baseline report had no rendered measurements, so this is a rendered correctness/performance check, not a like-for-like rendered before/after claim.

## Remaining operational evidence

Live SMB/WinRM/EDR, privileged reparse cases, power-loss/multiprocess publication, real Kerberos/DCOM/WUG and native RDP soak remain unverified as detailed in the [baseline review](vivre-performance-review-2026-10-14.md). Local PowerShell tests execute real scripts only against isolated dummy files. Passing tests do not close these operational gaps.

The fixes reuse existing views, timers, logger, registry, ownership conventions and staging transfer scripts. No new package dependency, alternate remoting engine or synthetic-only replacement of production behavior was introduced.
