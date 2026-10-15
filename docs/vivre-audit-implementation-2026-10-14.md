# Comprehensive audit implementation — acceptance ledger

Baseline: 8a6e5e9. Shaun authorized F26–F45 fixes with independent red-team and regression validation. No live server operations or deployment. F12 and force-reboot fast-watch remain explicitly deferred.

Operational product work, not a prototype. Completion claims require actual evidence. Dummy tests do not substitute for live servicing, native RDP soak or assistive-technology checks.

| Findings | Capability | Status | Evidence |
|---|---|---|---|
| F26/F40 | Safe publish paths and replacement artifacts | Implemented; independent review and dummy tests passed | PowerShell 5.1 and 7 unsafe paths, lease contention, rollback, ZIP byte verification |
| F27 | Script operation ownership | Implemented; independent review and real VM probe passed | Same shared coordinator, uncertain mutation retention, captured target list |
| F28/F29/F31 | Truthful repair/update scripts | Implemented; independent review and local regression passed | Actual PS5.1 with harmless adapters; native exit status, partial WUA results, both service restart attempts |
| F30 | Bounded script output | Implemented; independent review and Core regression passed | Real local PS streams drained; errors retained, bounded text, visible truncation; VM probe passed |
| F32 | Safe bundled-script updates | Implemented; Core migration and real editor probes passed | Prior-bundle/legacy hashes, atomic replacement, user-edit preservation, exact-content policy |
| F33 | Responsive package loading | Implemented; independent review and isolated WPF checks passed | Dispatcher responsive during blocked dummy loader; stale result cannot overwrite browsed choice |
| F34/F35 | Cross-process protection/settings writes | Implemented; independent review and regression passed | Separate-process lease tests; settings fail fast; staged-path writes preserve other writers |
| F36 | Stalled servicing recovery guidance | Implemented; independent lifecycle review passed | No child termination or terminal-state claim |
| F37 | Unsaved script edit protection | Implemented; independent review and real editor probes passed | Both editors: save/switch, cancel, refresh, save failure, busy close and transactional owner veto |
| F38 | Explicit scheduled/script reboot approval | Implemented; independent runner review and scheduling probe passed | WPF schedule count/non-default action probe passed |
| F39 | Accessible names/status/keyboard behavior | Implemented; local peer checks passed | Native screen-reader use remains untested |
| F41 | Truthful test prerequisites and validation entry point | Implemented; checks run individually | 1492 Core passed; 0 failed; one explicit skipped file-symlink theory; directory junction checks actually executed |
| F42 | Reproducible dependency resolution | Implemented; independent review and locked restores passed | SDK10.0.303 latestPatch; PowerShell7.6.7 pinned; normal/win-x64/win-arm64 Core lock graphs |
| F43 | Retire removed custom-column values | Implemented; independent review and isolated WPF check passed | Production removal path with injected temporary settings store |
| F44 | Shared script presentation and one Settings implementation | Implemented; independent review and both editor probes passed | Shared library policy/snapshot, dirty state, incremental projection and output formatting; unused SettingsWindow removed |
| F45 | Accurate Help instructions | Updated; compact toolbar, approval and editor instructions reviewed | Matches compact command bar and explicit approval behavior |

## Validation boundaries

- Resolve or record independent red-team findings before batch completion.
- Build Desktop and run Core tests; preserve reboot gates, user data, keep-alive hosting and pinned RDP scale.
- Use isolated temporary stores and dummy targets; no repair/install/cleanup/reboot/live remoting.
- Record actual checks, skips, limits and commits here as work progresses.

## Review corrections and checkpoints

- Corrected initial audit wording for F38: the existing scheduling dialog already disclosed force and lost work. The implementation adds count and an explicit action instead of an implicit Enter default; no redundant confirmation modal.
- Rejected initial 15-second settings-lease waits on the dispatcher. Shared settings now refuse contention immediately with existing save-error reporting; staging registry callers retain background waiting.
- Publish review found/fixed ancestor-reparse checks, invalid PowerShell rollback reversal, candidate ZIP extension, simultaneous publishing, and lease cleanup. Archive verification now compares every file and its SHA-256 bytes against the candidate directory.
- Isolated WPF audit probe passed package-loading responsiveness/stale choice, scheduled reboot count/no default, AutomationPeer names/live-region setting, and actual removed-column retirement using a dummy preference store. This is not a screen-reader/native session test.
- F28 red-team checked the [Microsoft winmgmt contract](https://learn.microsoft.com/en-us/windows/win32/wmisdk/winmgmt): repository inconsistency can return error 1358, so treating every nonzero verification result as a generic failure would retain the repair defect. The correction and harmless native-exit fixtures passed the accepted Core run.
- Build environment reports the configured signing certificate unavailable in CurrentUser/My. No signed package or production-ready release is claimed; no certificate was created/replaced.

- Publish safety tests repeated successfully under Windows PowerShell 5.1 and PowerShell 7 after rollback/backup-cleanup review corrections. Normal, win-x64 and win-arm64 locked dependency restores passed.
- First broad run exposed missing Windows file-symlink privilege. Directory reparse checks now use real temporary junctions; file-specific cases report an explicit skip, never a passing unexecuted assertion. No system privilege/Developer Mode changes are authorized or performed.

- F34–F36 focused validation passed 58/58; UpdateAgent Release build passed with the known signing-certificate warning. Independent re-review accepted per-PID baseline retirement/reuse, bounded observer disposal and advisory/terminal ordering under the writer lock. This proves local control logic, not a live DISM servicing outcome.

## Broad regression checkpoint

- Final Core run: **1492 passed, 0 failed, 1 skipped**, 5m38s. Evidence: `%TEMP%\Vivre-audit-20261014\core-accepted.trx`.
- The skipped theory contains the three owned-file symlink scenarios (owner metadata, lock and payload). Windows file-symlink creation privilege/Developer Mode is unavailable here. These scenarios are **not validated**; directory-reparse scenarios execute using actual temporary junctions and passed.
- Earlier broad runs were rejected: transitional WMI fixture/script mismatch and missing symlink privilege first; then test-junction teardown ordering. Both directory protections and cleanup were rerun after correction before the accepted broad run.
- F31 independent review found that one combined service-start call could skip BITS after a Windows Update restart error. Recovery now attempts both independently; the actual PS5.1 harmless fixture proves this behavior.
- F37 independent review identified [WPF owner-close behavior](https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.closing?view=windowsdesktop-10.0): owned windows do not receive Closing when the owner closes. Main-window preflight now checks every owned runner before closing; cancellation preserves all still-dirty editors. Real-control editor evidence is recorded below.

- Final publish review also rejected descendant junctions inside otherwise owned artifacts. A non-following tree scan now guards existing output, candidates, archive enumeration and recursive cleanup; unsafe temporary output is retained with a warning. Real temporary-junction sentinel tests passed in both Windows PowerShell 5.1 and PowerShell 7.
- RDP scale cardinal checks remain unchanged: two `LocalScale()` assignments and one native display-settings call. No native RDP session code was edited.

- Final publisher review accepted the non-following artifact-tree guards. It also rejected a cached runner-close approval that could survive a later close veto; that redundant flag and unused API were removed.
- Integrated local run passed package dispatcher responsiveness, custom-column retirement, actual automation peers, unloaded status-control garbage collection and actual runner VM guard/output checks. Its first editor fixture run failed during offscreen list setup; dispatcher/layout initialization and a hidden owner handle corrected the fixture before the accepted focused run.

- Final editor review caught initial runner category not matching the selected script, which could save a duplicate in My Scripts. The constructor now captures the original category before marking the editor clean; regression coverage checks the original file is updated.

## Final local acceptance

- Release solution and latest Desktop/PerformanceAudit builds passed with **0 errors** and the single expected missing-signing-certificate warning.
- Focused integrated audit probes passed: actual runner operation guards/output bounds, package-load responsiveness, stale-result refusal, custom-column retirement, automation peers/live settings, and status-control subscription collection.
- Both actual script editors passed save/switch consistency, Cancel preservation, dirty/clean external refresh and built-in notices. Save failure and busy execution veto closing; owner preflight preserves dirty text when a later runner cancels. These use temporary libraries, injected choices/errors and inert transports; no live script execution or production preferences writes.
- Red-team found and corrected category-list Clear triggering editable ComboBox writeback. Shared incremental category updates now preserve the typed save folder; the clean-refresh test passed afterward.
- `tools/validate-local.ps1` parses; its component commands were run separately. The wrapper itself was not run end-to-end.

## Remaining validation and authorized deferrals

- **Not validated:** owned-file symlink cases without Windows privilege; actual screen-reader/high-DPI keyboard experience; native RDP/full-app long-run heap/handle soak; live server repair, staging, patching, cleanup or reboot behavior.
- **At initial audit handoff:** no signed package or deployment was produced because the executing account could not see the configured certificate. The later F40 follow-up below supersedes the signing/package limitation; no deployment or server state change has occurred.
- **Still deferred:** F12 cross-process host-operation coordination and force-reboot fast-watch, per earlier decisions. File-write leases do not make operation guards cross-process.
- Bounded script capture constrains retained output, not arbitrary allocations inside user PowerShell. Local lifecycle checks are not proof that every possible native or application leak is absent.
- Implementation and local regression acceptance do not establish universal regression freedom. The frozen audit remains unchanged; this ledger is the current evidence record.

## Accepted integrated run

- Latest Release PerformanceAudit build: **0 errors**, one known signing warning. Full harness exited **0** and wrote `%TEMP%\Vivre-audit-20261014\performance-final.json`.
- All existing workspace, log, throttle, registry, manifest, completion-banner and rendered-grid probes completed, followed by all audit, script-execution and editor assertions, including typed category preservation in both editors.
- At 3,190 dummy rows, the actual offscreen DataGrid realized 16 rows; rapid filter setters took 0.43 ms and settled filtering/rendering took about 501 ms. These are single-run diagnostics, not a before/after speedup or visible-user latency guarantee.
- Independent final review found no unresolved known blocker in publish artifact-tree protection, bounded capture or editor workflow changes. The original F26–F45 audit file is unchanged; `git diff --check` passed.

## Delivery

- Implementation and validation harness: [`1b7e87a`](https://github.com/SirRuggie/Vivre/commit/1b7e87a) — `fix: harden audited operations and editor workflows`, pushed to `origin/master`. No release package was published or deployed.

## F40 follow-up — actual Windows PowerShell publish failure

- Shaun's full signed publish succeeded through signing but failed ZIP file-count verification. The original flat-file fixture missed Windows PowerShell 5.1 writing backslash ZIP paths and an empty directory entry being counted as a file.
- Reproduced the exact failure with nested module content, an empty script directory and the ownership marker before changing the verifier. Entry paths now normalize separators before directory exclusion, duplicate detection and file lookup. Nonempty directory entries are rejected; length and SHA-256 verification remain mandatory. Count errors include both counts.
- Expanded publish safety tests passed under Windows PowerShell 5.1 and PowerShell 7, including same-length tampering and separator-alias duplicate rejection. No application code changed.
- The configured signing certificate is now accessible from the executing build account with its private key. Earlier missing-certificate observations remain historical and no longer block this publish attempt.

- Independent follow-up review passed. Its requested adversarial directory-entry-with-data test was added; both PowerShell 5.1 and 7 suites passed again.
- Full signed Windows PowerShell 5.1 publish now passed end-to-end: **864 files**, 231.5 MB folder, **93.1 MB ZIP**, every file checked by path/length/SHA-256 before promotion. Agent Authenticode signature verified with the configured certificate.
- Package: `C:\src\Vivre\publish\Vivre-audited-8b0c2be.zip`; SHA-256 `5DFBFC0A274DA70836F487C74A36329C21E00D46A5F1455F91E1D41CA6C56A37`. App identity remains `1.17.3+build.202610142250.8b0c2bee821542c2c99bdf725705aacefdae4f28`; this follow-up changes packaging verification/tests/docs only, not app code. No deployment or live-server pilot was performed.

## Release alignment — 1.17.4

- Shaun confirmed retaining **master** as GitHub's default branch. The audit and packaging fixes are on that branch; the parked `feat/rdp-popout-window` and `perf/clean-measure` experiments remain excluded as documented in the RDP findings and backlog.
- The audited 1.17.3 build is superseded by release **1.17.4**. VersionPrefix, the dated changelog and README now agree. This changes release metadata only; prior application regression results remain applicable.
- Current package location: `C:\src\Vivre\publish\Vivre-1.17.4-win-x64.zip`, with adjacent `.sha256` and a manifest in the output folder. Build from the committed release revision so the informational version identifies its exact source. Older ZIPs remain historical, not current.
- A signed, verified package does not establish live pilot acceptance. No broad deployment, remote operation or server-state change is part of this release alignment.
