# Vivre UI/UX backlog

Living checklist — update as decisions, implementation, and validation progress.

Reviewed: 2026-10-19. Original audit baseline: `15663ab` (1.18.0 plus unpublished changes).

Current release: **1.19.1** (2026-10-19). U-21/U-22 specific native validation remains open; Shaun confirmed U-39 resolved on 2026-10-20. Earlier version references below describe the implementation checkpoints at that time.

## How to use

- Keep all U identifiers stable; record decisions, commits, and validation under the matching item.
- An unchecked item is a finding or recommendation, not authorization to implement it.
- Mark complete only after its agreed behavior is implemented and validated; state remaining live-validation limits.
- Preserve existing operational behavior and reuse current components where practical.
- **U-21/U-22:** local fixes/tests delivered; native DPI/physical keyboard/spoken screen-reader validation remains open. U-18 is implemented and locally validated; follow-up reviews are recorded below, with native interaction still unverified.

## Completed batch acceptance — U-8 (2026-10-19)

- **Implemented and locally tested:** Health tab context menu → Open tab in Patching copies all underlying names into a new same-title Patching tab, including unselected/filtered-out machines.
- **Preservation:** Source rows, results, selection, and work remain unchanged; existing Patching tabs are not reused or overwritten, and saved-list identity/results are not copied.
- **Idle defaults:** Fresh update defaults; new monitoring stopped before rows are added; auto-check suppressed for this handoff only, without changing global settings or normal loads.
- **Independent red team:** Separate `gpt-5.6-sol` reviewer inspected implementation and probe, found no concrete defect, and made no edits; parent ran validation.
- **Validation:** Debug and Release builds passed; production XAML/menu/handler/shell probe passed for inactive right-click source, all names, isolation/defaults, navigation, idle target with auto-check enabled, repeated/empty/stale/non-Health cases, and factory failure. Existing workspace safety regression passed.
- **Limits:** Local dummy workspaces and temporary settings only; no live hosts, visible-window/high-DPI, or screen-reader validation. No-work assertion uses idle flags after dispatcher drain with null remote sentinels, not explicit service call counters. Cleanup after an AddComputers exception was reviewed statically; the exercised failure occurs at factory creation.
- **Packaging:** Existing missing-signing-certificate build warning remains; no signed package produced; version stays 1.18.0 plus Unreleased.
- **Delivery:** Included in the commit containing this checklist update; see Git history for its identifier.

## Partial validation — U-21/U-22 (2026-10-19)

- **Implemented/tested locally:** wrapping filters, captions and script actions; resizable/wrapping CU comparison; focus rings, tab-menu keyboard routing, field/status names, and polite error feedback.
- **Automated evidence:** 48 workspace raster/layout cases across dark/light themes and three widths/four output scales; five dialogs at minimum/default widths; focus/automation probes; editor/settings/tab-handoff regressions passed. Debug/Release builds passed with the existing signing-certificate warning.
- **Independent red team:** separate reviewer caught a checked-filter focus-color conflict; a separate contrasting ring fixed it, and re-review found no further concrete defect in the scoped diff.
- **Not yet verified:** actual Windows DPI/text-scaling changes, physical keyboard/Enter/Escape behavior, and spoken screen-reader output; offscreen tests are not substitutes.
- **Status:** U-21/U-22 remain open for those real-environment checks. See [validation matrix and remaining steps](ui-ux-u21-u22-validation.md).
- **Delivery:** product fixes and evidence included in the commit containing this checklist update; no live targets or signed package.

## Completed batch acceptance — U-18

- **Implemented/tested:** Four selection-only Actions shortcuts reuse software check, staging, script, and maintenance dialogs; empty or changed tab/selection/name/membership cannot widen the target scope.
- **Implemented/tested:** Connection and WhatsUp Gold right-click groups retain existing commands in Health and Patching; selected-script is disabled without selection and its empty-target handler returns.
- **Independent red team:** Separate gpt-5.6-sol reviewer found the selected-script fallback and an unnecessary count row; both fixed. Final scoped code review found no remaining material defect.
- **Validation:** Debug and Release builds passed; focused probe passed four shortcuts, five stale-target cases, empty-selection guards, both right-click modes, and toolbar selection binding/accessibility label. Existing library/script-dialog regression passed.
- **Limits:** Dummy local workspaces and intercepted dialog dispatch only; no live operations or physical-keyboard usability study. Existing unsigned-agent certificate warning remains; no package produced.
- **Documentation/delivery:** Help, README, changelog, and harness instructions updated; included in the commit containing this checklist update. Version remains 1.18.0 plus Unreleased.
- After delivery, stop. The requested hands-on usability study is deferred until Shaun says **continue**; start new findings at U-23, preserve U-1 through U-22, and report each finding with one bullet plus a Why sub-bullet. A study can compare usability, not prove universal optimality.

## Highest priority — behavior found in the code

- [x] **U-1 · High — Make update settings independent per tab.**
  - **Why:** Shared operational settings could differ from another tab's displayed settings.
  - **Decision:** Shaun chose tab-specific settings instead of synchronizing settings globally.
  - **Delivered:** Independent source, drivers, exclusions, and applicable/installed scope; source labels in each Patching header and the active toolbar.
  - **Defaults:** New tabs use Windows Update, drivers off, no exclusions, and applicable updates; settings are session-only.
  - **Commit:** `cd6f6b7` — `fix: isolate update options per tab`, pushed to `master`.
  - **Validated:** Real workspace isolation, new-tab defaults, scan/install arguments with inert services, and production header/toolbar bindings across tab switches.
  - **Builds:** Debug and Release passed with a missing agent-signing-certificate warning; no signed package produced.
  - **Regression:** 1,543 passed, one skipped, one unrelated WUG timing failure that passed when rerun alone; the initial full run was not entirely green.
  - **Limits:** No live-server operations or visible-window/DPI testing; local probe: `tools/PerformanceAudit/TabOptionsProbe.cs`.
  - **Independent red team (2026-10-19):** A separate `gpt-5.6-sol` reviewer found two issues, independently re-reviewed the fixes, and found no remaining concrete U-1 issue within the reviewed scope.
  - **Finding 1 — existing scan consistency gap, fixed:** An in-flight scan could observe later changes to source/drivers/exclusions/scope; scan dispatch now clones one settings snapshot for all retries, fallback, and result handling.
  - **Finding 2 — compact-toolbar regression, fixed:** The new source label omitted the existing compact-mode style; restoring that style collapses the toolbar text in compact mode while the tab-header source remains available.
  - **Failure reproduction:** The delayed-scan test failed before the snapshot fix; with only that fix applied, the compact-mode test failed before the style was restored.
  - **Follow-up validation:** Expanded real-workspace/WPF probe passed for delayed scans, compact/expanded labels, scheduled installs, and uninstalls; existing install-approval probe passed; 56 focused WUA/retry tests passed with no skips.
  - **Follow-up builds:** Debug and Release passed with zero errors and one existing missing-signing-certificate warning each; no signed package produced.
  - **Evidence limits:** Review and tests used local inert services, not live servers; retry/fallback snapshot reuse was code-reviewed rather than remotely exercised, and visible-window/DPI checks remain outside this validation.
  - **Regression history:** The earlier full-suite result above remains the full-run evidence; this follow-up reran focused tests, not the entire suite.

- [x] **U-2 · High — Preserve existing results when pasting additional servers.**
  - **Delivered:** Paste defaults to Add with empty input; existing row objects, results, selection, and focus survive additions and unchanged membership.
  - **Replacement:** Explicit Replace reconciles membership and requested order while preserving retained rows.

- [x] **U-3 · High — Protect the current workspace when opening or clearing lists.**
  - **Delivered:** Paste replacement, Open list, and Clear share removal confirmation; cancellation preserves the workspace.
  - **Safeguards:** Active/draining work blocks removal, changed membership invalidates approval, and a missing saved file reports an error instead of clearing the tab.
  - **Performance:** Full clear uses one collection Reset; near-total replacement preserves the actual grid selection.

- [x] **U-4 · High — Prevent silent saved-list overwrites.**
  - **Delivered:** Invalid names show inline errors instead of silent filename cleanup; existing names require explicit overwrite approval.
  - **Concurrency:** Revision checks and a shared file lease prevent stale save/delete approvals from overwriting newer changes made by another Vivre instance.
  - **Compatibility:** Legacy names remain readable/deletable; contention and filesystem failures are displayed without losing the save dialog or crashing deletion.

- [x] **U-5 · High — Warn before exiting with active fleet work.**
  - **Delivered:** Normal and tray Exit check all tabs for active work, monitoring, and scheduled operations; cancelling keeps the application open.
  - **Wording:** The warning explains that local control stops, cancellation is requested, and remote or scheduled work may continue.

### U-2–U-5 acceptance and independent review — 2026-10-19

- **Acceptance ledger:** All four requested capabilities are implemented and locally tested; no requested implementation is deferred or blocked.
- **Independent review:** A separate `gpt-5.6-sol` agent refuted the implementation, reviewed corrections, and reported no remaining concrete issue within U-2–U-5; runtime results were supplied by the parent, not independently rerun.
- **Corrections from review:** Required-file reads, draining-monitor protection, retained-row ordering, single-Reset clear, revision-bound save/delete, legacy-name access, and handled delete failures.
- **Core regression:** 1,557 passed, one skipped, zero failed; this full run preceded the final narrow delete correction, after which 28 focused storage/settings tests passed.
- **Skip:** The Windows PowerShell file-reparse test could not exercise its privileged file-symlink case; it remains an explicit skip.
- **Workspace validation:** Real view models and production WPF templates passed add/replace/cancel, stale approval, busy/draining work, saved-list open/clear, name errors, exit decisions, and selection-preservation probes with dummy data.
- **Performance sample:** The offscreen 1280×720 grid replaced 3,190 rows with one retained selected row in 332.8 ms including layout; full clear took 25.4 ms with one Reset in the final probe run.
- **Reporting correction:** The probe snapshots the full-clear Reset count before later cleanup can increment it; the original one-Reset assertion already passed.
- **Builds:** Debug and Release passed with zero errors and one existing missing agent-signing-certificate warning each; no signed deployment package was produced.
- **Limits:** No live-server operation, visible-window/high-DPI check, native tray interaction, or full-application soak; timings are diagnostic samples rather than a universal responsiveness guarantee.
- **Storage boundary:** File leases coordinate Vivre instances; unrelated programs that ignore those leases are outside that concurrency guarantee.
- **Delivery:** Included together in the commit containing this checklist update; see Git history for its identifier.

## U-6, U-7, U-9, U-19 acceptance and independent review — 2026-10-19

- **Acceptance ledger:** U-6, U-7, U-9, and the agreed U-19 list-entry/rename wording scope are implemented and locally tested; no requested capability in this batch is deferred or blocked.
- **Simplicity:** One status line; existing dialogs, collection timer, and grid controls reused; no new save step, automatic saving, disk polling, or operational confirmation flow.
- **Independent review:** A separate `gpt-5.6-sol` agent read the diff and found one medium risk: an unbounded frozen Name could consume the viewport; width is now capped at 320 with ellipsis and full-name hover text, and the reviewer accepted the correction.
- **Focused tests:** 22 parser/list-store tests passed, zero skipped; the previous full regression evidence remains in U-2–U-5 above, and the full suite was not repeated for this UI/parser batch.
- **Workspace probe:** Saved identity, membership changes/restoration, machine rename, failed-save preservation, deletion detachment, actual mixed-separator Paste, action labels, and real status binding passed alongside the U-2–U-5 safety checks.
- **Scrolling:** Both production grids passed actual horizontal-offset checks at 720 pixels with a 206-character name; Name remained fixed and bounded while the rest scrolled.
- **Performance sample:** 3,190-to-one replacement including layout took 405.6 ms; full clear took 26.9 ms and one Reset; these are local samples, not universal latency guarantees.
- **Linux regression:** The existing 1,000-row AD-label probe passed, including rendered Linux badge, bounded delayed lookup, and stale-result protections; its old StackPanel-only diagnostic cast was generalized to Panel for the new name layout without removing assertions.
- **Builds:** Debug and Release passed with the existing missing agent-signing-certificate warning; no signed package produced.
- **Limits:** Offscreen production controls and dummy services/storage; no live targets, visible-window/high-DPI sweep, screen-reader run, or full-application soak; the reviewer did not independently rerun parent-owned tests.
- **Delivery:** Included in the commit containing this checklist update; see Git history for the identifier.

## U-10, U-11, U-15 acceptance and independent review — 2026-10-19

- **Acceptance ledger:** All three requested capabilities are implemented and locally tested; nothing in this batch is deferred or blocked.
- **Simplicity:** Existing options, dialogs, and approval gates reused; no extra normal confirmation step, background polling, or live operation.
- **Independent review:** A separate `gpt-5.6-sol` reviewer found a high-severity existing target-name race in scheduling; both handlers now reuse InstallTargetScope and reject renamed targets or closed tabs before dispatch, and the reviewer accepted the correction.
- **Focused tests:** 19 timezone-formatting, reboot-approval, and target-snapshot tests passed with zero skips; the full suite was not rerun for this UI batch.
- **Production-control probe:** Per-tab summary bindings, Health hiding, install/reboot/cancel callback routing, schedule counts/host zone, summer/winter offsets, repeated-hour disclosure, invalid-time rejection, and captured-name protection passed with dummy targets.
- **Regression probes:** Existing per-tab options and install-anyway approval probes passed, including delayed scan snapshots, compact toolbar, scheduled installs, and uninstalls.
- **Builds:** Debug and Release passed with zero errors and the existing missing agent-signing-certificate warning; no signed package produced.
- **Limits:** Offscreen WPF and inert callbacks/services; no live install, reboot, task registration, visible-window/high-DPI sweep, or screen-reader run; independent reviewer did not rerun parent-owned tests.
- **Delivery:** Included in the commit containing this checklist update; see Git history for its identifier.

## U-12, U-13, U-14 acceptance and independent review — 2026-10-19

- **Acceptance ledger:** All three requested capabilities are implemented and locally tested; nothing in this batch is deferred or blocked.
- **Simplicity:** Reused the existing Activity queue, settings stores, and Settings page; one grouped CU Save and an explicit Reload saved CU action, with no extra normal confirmation dialog.
- **Independent review:** A separate `gpt-5.6-sol` reviewer identified incorrect conflict-recovery guidance, missing origin in copied Activity, and error-driven numeric-column expansion; all were corrected and independently re-reviewed with no further concrete finding.
- **Save integrity:** Typed and package saves share one guarded shared-store update; stale KB/build/month/architecture baselines refuse overwrites, strict reload preserves drafts on read failure, and package reading disables competing CU edits until completion.
- **Focused tests:** 14 shared-settings tests passed, zero skipped; the full suite was not rerun for this batch.
- **Production-control probe:** Actual Settings controls and temporary stores passed grouped/no-partial save, invalid input, stale-save refusal, reload, corrupt-file failure, package/typed baseline continuity, and numeric-error width checks; actual Activity passed historical origin capture, same-host/different-tab attribution, display binding, search, and clipboard-free TSV formatting.
- **Logging regression:** Existing bounded-log probe passed at 2,000 and 10,000 messages; paused dispatch retained the newest 512 behind one queued drain plus one overflow summary, preserving sink admission. Counting sink only; no production disk-I/O or full-app memory claim.
- **Builds:** Debug and Release passed with zero errors and the existing missing agent-signing-certificate warning; no signed package produced.
- **Limits:** Offscreen WPF, dummy log entries, and temporary files; no live targets, production settings writes, visible-window/high-DPI sweep, or screen-reader run. Package-save integration was exercised without reading a live MSU; independent reviewer did not rerun parent-owned tests.
- **Delivery:** Included in the commit containing this checklist update; see Git history for its identifier.

## Completed batch acceptance — U-16, U-17, U-20 (2026-10-19)

- **U-16 — implemented and locally tested:** Search Open/Delete/Restore menus and both script libraries; preserve open scripts, dirty edits, and selection, including saves under a new name/category.
- **U-17 — implemented and locally tested:** Same-volume recovery of approved deletions survives restarts; restore requires an unused valid name and leaves current workspace membership unchanged.
- **U-20 — implemented and verified:** Index points to actual 1.18.0 plus Unreleased; historical release validation files remain unchanged.
- **Independent red team:** Separate `gpt-5.6-sol` reviewer found stale script-path pinning and linked recovery-payload gaps; fixes and expanded tests were re-reviewed with no remaining concrete defect found within this scope. Reviewer inspected code/evidence and did not rerun parent-owned tests.
- **Core tests:** 22 passed, zero failed, one explicit file-symlink-privilege skip; restart/collision/repeated deletion/invalid path/failed archive and actual folder-junction cases passed. Linked source/payload guards were reviewed; their file-symlink runtime cases were not executed on this host.
- **UI probes:** Production native menu helper passed offscreen focus, routed typing, clear-button automation, no-match feedback, submenu persistence, and duplicate recovery-record selection. Both production editors passed dirty filtering, active-query Cancel/Discard/Save, previous-pin removal, renamed/category saves, and existing editor guards.
- **Regression:** Existing workspace safety probe and audit-fixes/script execution/editor probes passed with temporary data and inert services.
- **Builds:** Debug and Release passed; existing missing agent-signing-certificate warning remains, and no signed package was produced.
- **Limits:** No live hosts, production data changes, physical visible-window input, high-DPI sweep, or screen-reader testing. Only deletions made by this version can be restored; recovery entries remain locally until restored, with no automatic purge.
- **Delivery:** Included in the commit containing this checklist update; see Git history for its identifier.

## Workflow improvements

- [x] **U-6 · Medium — Make saved-list identity and changes visible.**
  - **Delivered:** Each tab shows its associated list and unsaved membership changes; successful saves show Saved and reuse the associated name next time.
  - **Boundary:** Compared with the last open/save snapshot, not continuous disk state; scan results, sorting, selection, and tab renaming do not count as membership changes.

- [x] **U-7 · Medium — Make Paste and Quick Add accept the same formats.**
  - **Delivered:** Both use one parser for lines, tabs, spaces, commas, and semicolons, preserving first-seen order and removing case-insensitive duplicates.

- [x] **U-8 · Medium — Add an explicit handoff from Health to Patching.**
  - **Delivered:** Right-click a Health tab → Open tab in Patching copies all names into a fresh same-title tab, preserving the source and starting no automatic operations.

- [x] **U-9 · Medium — Keep machine names visible during horizontal scrolling.**
  - **Delivered:** Name stays fixed at the left of both grids; its width is bounded, long names have ellipsis/full-name hover text, and other columns retain resizing/reordering.

- [x] **U-10 · Medium — Show active update options beside patching actions.**
  - **Delivered:** A short summary above each Patching grid shows source, drivers, and parsed exclusion count; it stays visible in compact mode, and the existing options button exposes the same summary on hover.

- [x] **U-11 · Medium — Make forced reboot a clearly separate choice.**
  - **Delivered:** Install anyway is primary; forced reboot is a separate secondary choice with its pending-target count and lost-work warning.
  - **Behavior:** Install does not reboot; reboot-first only reboots and requires starting installation again afterward; Cancel does neither.

- [x] **U-12 · Medium — Show the originating tab in Activity.**
  - **Delivered:** Activity displays, searches, and copies the originating workspace; titles and stable instance numbers are captured when entries are emitted, with app-wide entries left untagged.

- [x] **U-13 · Medium — Save related CU settings together.**
  - **Delivered:** KB/build/month remain an explicit draft until Save CU settings commits them together; success, conflict, and reload errors are shown inline, and Reload saved CU discards the draft only after a successful read.

- [x] **U-14 · Medium — Explain invalid settings beside their fields.**
  - **Delivered:** Invalid install concurrency, WUG concurrency, servicing wait, and CU build input stay visible with corrective text; the prior settings remain active, and valid corrections clear errors.

- [x] **U-15 · Medium — Show timezone and target count when scheduling.**
  - **Delivered:** Both schedule dialogs show captured target counts and the Vivre host timezone with the chosen date's UTC offset; invalid clock-change times are refused and repeated times disclose the chosen offset.
  - **Safeguard:** Changing selection does not retarget approval; target-name changes or a closed tab cancel before scheduling.

## Discoverability and polish

- [x] **U-16 · Lower — Add search to growing list and script libraries.**
  - **Delivered:** Search existing saved-list menus and grouped script libraries; keep the current script visible and edits protected.

- [x] **U-17 · Lower — Offer recovery for deleted saved lists.**
  - **Delivered:** Restore deleted list offers recoveries across restarts and refuses name collisions without overwriting.

- [x] **U-18 · Lower — Make frequent secondary actions easier to discover.**
  - **Why:** Software, scheduling, deployment, and maintenance actions compete inside a large right-click menu.

- [x] **U-19 · Lower — Use action-specific dialog buttons and consistent wording.**
  - **Delivered:** Paste machines uses Add machines or Replace machines according to mode; Rename says Rename, and Save list retains its specific action label.
  - **Scope:** Existing operational and native confirmation dialogs remain unchanged; no additional confirmation step or dialog framework was introduced.

- [x] **U-20 · Lower — Update the documentation index's release pointer.**
  - **Delivered:** Index identifies 1.18.0 and Unreleased; 1.17.3 validation is explicitly historical.

## Validation gaps — not confirmed visual failures

- [ ] **U-21 · Medium — Verify narrow-window and high-DPI layouts.**
  - **Delivered locally:** Wrapping filters/script controls and CU comparison improvements; automated narrow-layout/raster checks pass.
  - **Remaining:** Actual Windows DPI/text scaling and broader visible-window sweep.

- [ ] **U-22 · Medium — Complete keyboard and screen-reader testing.**
  - **Delivered locally:** Distinct focus rings, keyboard tab-menu access, field/status labels, and polite errors; offscreen focus/Automation checks pass.
  - **Remaining:** Physical keyboard/default/cancel behavior and spoken screen-reader verification.

## Strengths to preserve

- **Clear bulk scope:** Selected-machine counts and distinct selected/all actions reduce targeting mistakes.
- **Explicit install approval:** Confirmation captures the intended machines before execution.
- **Controlled reboots:** Update installation does not automatically reboot servers.
- **Useful information layering:** Compact software summaries keep technical diagnostics available separately.
- **Clear staging boundaries:** Deployment explains that copying packages does not install them.
- **Honest uncertainty:** Unknown, incomplete, deferred, and unverified states avoid false success.
- **Efficient daily navigation:** Keyboard shortcuts, filters, sorting, and column customization support repeated administration.
- **Protected script work:** Unsaved-edit guards and execution confirmations provide useful safeguards.
- **Persistent working views:** Tab keepalive preserves embedded sessions during navigation.
- **Practical help:** Searchable help and F1 support users without forcing them out of the app.
- **Appropriately limited Linux labeling:** AD-based identification satisfies the requested scope without pretending to provide Linux management.

## Evidence and review limits

The audit combined source/workflow inspection and supplied screenshots with published guidance and forum discussions; it was not a live end-to-end usability study or a guarantee that every issue was found.

Key implementation references:

- [Workspace behavior](../source/Vivre.Desktop/ViewModels/WorkspaceViewModel.cs)
- [Paste dialog](../source/Vivre.Desktop/LoadComputersWindow.xaml.cs)
- [Window and list handlers](../source/Vivre.Desktop/MainWindow.xaml.cs)
- [Main window layout](../source/Vivre.Desktop/MainWindow.xaml)
- [Grid layout](../source/Vivre.Desktop/WorkspaceView.xaml)
- [Settings handlers](../source/Vivre.Desktop/SettingsPage.xaml.cs)

Research references:

- [Nielsen's usability principles](https://www.nngroup.com/articles/ten-usability-heuristics/)
- [Microsoft dialog guidance](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/dialogs-and-flyouts/dialogs)
- [Carbon data-table guidance](https://www.carbondesignsystem.com/building-blocks/core/components/data-table/guidelines)
- [Microsoft accessibility checklist](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-checklist)
- [Administrator discussion of patching-tool problems](https://www.reddit.com/r/sysadmin/comments/1c0oq5l)
- [Bulk-selection UX discussion](https://www.reddit.com/r/UXDesign/comments/1tttvmd/showing_only_selected_items_in_data_tables/)

Forum anecdotes informed recommendations; they are not evidence that Vivre has a particular defect.

## Follow-up menu review - U-23 through U-27

These findings came from source/workflow inspection after Windows UI automation repeatedly failed; they are not results of a completed hands-on usability study.

- [x] **U-23 - High - Clarify and confirm scheduled-task cancellation.**
  - **Why:** The singular menu command immediately removes all Vivre_* tasks on selected machines without review.
  - **Delivered:** Cancellation reviews all Vivre_* schedules and the captured machine count; Keep schedules does nothing; changed names, removed targets, or a closed tab prevent dispatch.
- [x] **U-24 - High - State each right-click action's target scope.**
  - **Why:** Commands mix clicked-machine, selected-machine, and whole-tab targeting without consistent labels/counts.
  - **Delivered:** Leaf labels distinguish this machine, selected count, all in tab, and shown rows; every menu action rejects changed row identities/membership/selection before dispatch.
- [x] **U-25 - Medium - Use specific, consistent action names.**
  - **Delivered:** Actions says WhatsUp Gold maintenance; the grid submenu says SCCM client actions; help and README match.
- [x] **U-26 - Medium - Disable commands that cannot act.**
  - **Delivered:** Copy Selected rows, Enable WinRM, and SCCM client actions disable without selection and re-enable with selection in both modes; existing handler guards remain.
- [x] **U-27 - Lower - Add Save tab as list to the tab menu.**
  - **Delivered:** Health and Patching tab menus resolve the clicked tab, including inactive tabs; both entry points reuse the existing save dialog, duplicate-prompt guard, name validation, and overwrite checks.
  - **Safeguard:** Missing, closed, or removed tabs cannot fall back to the active tab; membership is checked again after popup teardown.

## Batch acceptance - U-25/U-26/U-27

- **Implemented and locally tested:** All three capabilities above; no new dialog or operational service.
- **Validation:** Debug/Release builds passed; actions-menu, tab-handoff (including save routing), and workspace-safety probes passed.
- **Test limits:** Save-menu binding/resolution and hidden-window handler guard exercised with dummy workspaces; persistence regression uses isolated dummy storage. No physical mouse/keyboard walkthrough or live hosts.
- **Independent review:** Separate gpt-5.6-sol read-only reviewer found no concrete defect in targeting, stale-tab handling, duplicate-dialog guarding, labels, enablement, or probe changes; visible-modal/physical input testing remains unverified.
- **Delivery:** Included in the commit containing this checklist update; version stays 1.18.0 plus Unreleased. Existing missing-signing-certificate warning remains; no signed package.
- **At that batch:** U-23/U-24 were deferred; delivered in the following batch below. Hands-on validation remains open.

## Completed batch acceptance - U-23/U-24

- **Implemented and locally tested:** cancellation review states all Vivre_* install/reboot schedules, captured count, and that already-started work is not stopped; no approval or empty selection causes no dispatch.
- **Target fidelity:** approval retains the reviewed machines if selection changes during cancellation; changed names, removed rows, or closed tabs abort. Enable WinRM separately refuses changed confirmation targets before calling its existing operation.
- **Menu scope:** leaf labels identify this machine, selected count, all in tab, shown rows, or this view; online/offline copy explicitly says whole tab without implying the whole tab is copied.
- **Stale menu guard:** first routed Click handler rejects changed identities, tab membership, selection, or closed tab before the existing action; SCCM invokes its same command only after that guard.
- **Validation:** Debug/Release builds and focused actions-menu probe passed; tests cover labels in both modes, fresh/stale routed clicks, cancellation approval/refusal, selection change, renamed/removed targets, empty selection, and tab closure.
- **Independent red team:** found stale-menu dispatch and Enable WinRM post-confirmation drift; both fixed. Final separate gpt-5.6-sol read-only review found no remaining concrete defect in the scoped diff.
- **Limits:** local dummy workspaces and intercepted dispatch only; no live task removal, visible confirmation/physical keyboard study, or signed package. Existing signing-certificate warning remains.
- **Delivery:** included in the commit containing this checklist update; version remains 1.18.0 plus Unreleased.

## Follow-up usability review — U-28–U-37 (2026-10-19)

Review baseline: `fb46160`. Findings only; no implementation approved by this review. See [evidence, coverage and limitations](ui-ux-follow-up-2026-10-19.md).

- [x] **U-28 · High — Protect saved scripts from silent replacement.**
  - **Why:** A new name or sanitized filename collision can overwrite another script without warning.

- [x] **U-29 · High — Make Server 2016 Stage clearly say it installs.**
  - **Why:** Stage software only copies files, but the similarly named CU action starts installation.

- [x] **U-30 · Medium — Rename the misleading Staged badge.**
  - **Why:** The badge means a machine uses the CU lane, not that its update has been staged.

- [x] **U-31 · Medium — Reset stale service suggestions when changing products.**
  - **Why:** A new software query can keep and remember the previous product's service name.

- [x] **U-32 · Medium — Protect custom columns from accidental replacement.**
  - **Why:** Adding an existing column name replaces its script and immediately runs the replacement.

- [x] **U-33 · Medium — Keep identity columns visible in People.**
  - **Why:** Horizontal scrolling hides the person or candidate name while comparing other details.

- [x] **U-34 · Medium — Make People copy scope explicit.**
  - **Why:** Copy resolved emails includes hidden rows even when the grid is filtered.

- [x] **U-35 · Lower — Show which People entries can be retried.**
  - **Why:** Retry unresolved stays enabled with nothing eligible and does not retry No email or Needs review.

- [x] **U-36 · Lower — Avoid false unsaved-result warnings in People.**
  - **Why:** Pasting only existing entries marks an exported session dirty without changing its results.

- [x] **U-37 · Lower — Remember grid widths and column order.**
  - **Why:** Visibility is saved, but operators must repeat manual sizing and rearranging after reopening.

## People flow follow-up — U-33–U-36, U-38/U-39

- [x] **U-38 · Medium — Make People follow the Fleet entry and review flow.**
  - **Why:** A flat button row and hidden review interactions made People feel disconnected from the rest of Vivre.
  - **Delivered:** Shared Fleet toolbar style; Quick Add; existing multiline paste dialog reused with People validation, no replacement/tabs/saved-list controls; fixed bottom-right Open review, plus explicit double-click/right-click guidance; the later row-menu correction below removes the redundant Row actions button.
- [x] **U-39 · Medium — Keep People additions and result updates responsive.**
  - **Why:** Synchronous clipboard access and repeated grid refreshes could interrupt the input flow.
  - **Delivered:** Paste opens without reading the clipboard automatically; validated additions yield every 100 rows, fast results every 25, and expensive aggregate/filter refreshes coalesce to 100ms. Stop during adding keeps submitted inputs as Stopped and prevents automatic lookup restart.
- **U-33–U-36 delivered within this flow change:** identity columns pinned; Actions explicitly says Copy all resolved emails; Look up again is enabled only for eligible retry states; duplicate-only paste no longer dirties exported results.
- **Evidence:** Release build, People/dispatcher probes and Fleet workspace/menu/tab regressions passed; independent read-only reviewer caught the footer-menu anchor issue, corrected before final re-review. See [People validation](people-lookup-validation.md) for measurements and remaining host-specific validation.
- **Limits:** The reported five-person remote clipboard/host pause is not reproduced; native/RDP/DPI/physical-input checks remain open under U-21/U-22. Shaun confirmed U-39 resolved on 2026-10-20 based on deployment use; this closes the reported responsiveness issue without claiming a universal performance guarantee.

## U-29 / U-30 delivery ledger (2026-10-19)

- Implemented and tested: **Install CU (no reboot)** in the Server 2016 toolbar and decision/package dialogs; copy explicitly says installation can affect performance and reboot is later.
- Implemented and tested: **CU lane** configuration badge/header, row-menu labels, Settings labels, README and in-app how-to help; genuine post-install state messages are retained.
- Implemented and reviewed: legacy hidden-column key `Staged` maps to `CU lane`; eligibility updates now honor the hidden preference rather than re-showing the column.
- Preserved: patch routing, target selection, stored host flags, handlers, execution, guards, and reboot behavior; no Core or agent implementation changes.
- Independent red team: found the visibility override and remaining ambiguous wording; both fixed and re-reviewed with no remaining blocking findings.
- Validation: Release build passed; 114 focused CU/lane/routing/planner tests passed; real-WPF PatchingClarity probe passed, including action wording, lane binding, hidden/show/no-eligible states and existing reboot-choice/scheduling checks. `git diff --check` passed.
- Limits: local dummy-data validation only; no live patching, reboot, signed package, or native multi-DPI usability certification. Build warned that the configured signing certificate is absent on this build account. U-21/U-22 hands-on validation remains separate.

## People row-menu correction (2026-10-19)

- Requested: remove footer Row actions; expose review, edit, and copy directly on row right-click; retain Open review.
- Implemented: one shared row menu overrides cell copy menus, with review/edit first; blank-space clicks do not act on stale selection.
- Verified: Release build and full local People probe passed, including handled row-menu events, keyboard opening, blank-space suppression, review/edit ordering, and removed footer button. Synthetic data only; no live AD changes or deployment-host mouse testing.

## Paste into new tab (2026-10-19)

- Requested: three choices in Health/Patching Paste: Add, Replace, Create new tab; no tabs in People.
- Implemented: existing workspace factory and AddComputers reused; fresh default settings, original rows/results untouched, same section selected; blank input creates no tab.
- Verified: Release build, tab-handoff/new-tab probe, workspace-safety probe, and People probe passed with dummy data. Includes both sections, preserved source rows, deduplication, empty-input rejection, default Add selection, and hidden tab options in People. No live server operations or deployment-host UI testing.

## U-28 / U-32 delivery ledger (2026-10-19)

- Implemented and tested: both script editors and dirty-close/selection saves confirm the exact destination before replacing; cancellation keeps dirty text, while stale or deleted revisions refuse the save. Name/content/category are captured before confirmation.
- Implemented and tested: reuse the existing cross-process file lease and atomic writer; filename sanitization collisions require approval. If an external editor changes the file between validation and replacement, preserve the exact displaced contents in a unique `.previous` recovery file and report its path. Uncontended recovery cleanup is best-effort.
- Implemented and tested: duplicate custom-column names (case-insensitive) are rejected before mutation or execution; existing script/values and entered fields stay intact. Remove deliberately or choose another name. Loading legacy duplicate definitions keeps the first and logs a warning.
- Independent red team: found legacy duplicate-load bypass, external-write race, and recovery cleanup failure reporting; addressed all three and re-reviewed with no remaining blocking findings.
- Validation: Release build passed; 36 script/atomic-file tests passed; real-WPF editor/library and column regression probes passed using temporary libraries, dummy rows, and fake execution hosts. No live script execution or production file changes.
- Scope limits: recovery protects contents displaced at the atomic swap; it cannot stop a separate editor from writing after the save. No deployment-host mouse/DPI verification or signed package produced; build account lacks the configured signing certificate.

## U-31 / U-37 delivery ledger (2026-10-19)

- Implemented and tested: a changed software product clears the previous service and checkbox; known products load only their own remembered suggestion. Case/whitespace-only edits preserve manual service text; quick-pick and manual opt-in remain available.
- Implemented and tested: shared grid layout persistence uses the existing asynchronous settings store for Health, Patching, People results and People review independently. Widths and display order survive a fresh-store JSON reload; pinned identity stays first and existing visibility settings remain unchanged.
- Reset: Fleet right-click/header/keyboard menus offer Reset column layout for that view; People Actions offers separate results/review resets. Defaults and automatic column sizing are restored. Existing sibling tabs use saved preferences on their next load; they are not forcibly rearranged while being used.
- Performance/lifecycle: save only on user resize/reorder interactions when values actually differ; scan/layout churn performs no writes. Input/collection handlers detach on unload. New custom columns use distinct keys and unknown saved columns are ignored.
- Independent red team: fixed missing keyboard/row reset access, combined People resets, and malformed-key handling. Added uncached disk round-trip proof after review; final re-review found no blocking issues.
- Validation: Release build, seven AppSettingsStore tests, settings/grid/service probe, Fleet actions-menu probe and People probe passed. Checked service transitions, width/order restoration, reset, malformed preferences, identity pinning, redundant-write suppression and unload detachment using dummy data and temporary settings.
- Limits: no live server checks, native mouse/high-DPI certification, or signed deployment package. U-21/U-22 and deployment-host U-39 validation remain separate; build account lacks the configured signing certificate.

## Release metadata — 1.19.0 (2026-10-19)

- Requested: roll accumulated feature/fix work into the app version and synchronize current documentation.
- Updated: VersionPrefix single source, dated changelog, README, documentation index, repository guidance and living validation ledgers. Stable publish output remains `publish/Vivre.zip`; About/build metadata derive from VersionPrefix.
- Verified: Release build passed; generated assembly/file version is 1.19.0.0 and product/About informational version begins 1.19.0 with build/commit identity. Current patching references and README operation sections reconciled. No signed ZIP or deployment is claimed; the build account still lacks the configured signing certificate.

## Patching handoff and status banner correction (2026-10-19)

- Requested: normal vitals/checks when opening Health machines in Patching, distinguish the copied tab, and replace stacked status lines with a neutral one-line banner.
- Implemented: normal AddComputers path respects Auto-check on load and fresh-workspace monitoring; title uses source name plus Patching and a collision suffix. No automatic update scan/install/reboot is added.
- Implemented: themed banner combines Temporary list/saved-list state with per-tab update source, drivers and exclusions; long text truncates with full tooltips.
- Verified: Release build and focused tab-handoff, patching-clarity/banner, and workspace-safety probes passed using real WPF controls with dummy hosts and temporary settings. Auto-check enabled/disabled, duplicate titles, source preservation, and list protection were exercised. No live-host or deployment visual validation is claimed; configured signing certificate remains unavailable.


## People footer consistency (2026-10-19)

- Implemented: full-window footer placement matching Health/Patching, compact review button, themed background/divider, and one-line help with full tooltip. Existing review handler and People bindings are reused.
- Verified: Release build passed; existing People regression suite passed on rerun (actions, navigation, narrow/light/dark layouts with synthetic directory data). Inspected the dark rendered footer. Initial responsiveness run exceeded its 500 ms dispatcher threshold (555 ms); rerun measured 439 ms. This does not close deployment-host responsiveness validation. No lookup behavior changes or live directory operations; build warning remains for unavailable signing certificate.


## Saved-list opening choices (2026-10-19)

- Implemented: populated tabs offer Append list, Replace list, or New tab (default); empty tabs open directly. Reuses the paste dialog and new-tab creation. Append preserves current identity/results; replacement records opened-list identity and retains active-work/stale-membership guards. New tabs stay in the originating Health/Patching section.
- Verified: Release build, tab-handoff and workspace-safety probes passed with dummy data and temporary storage. Covered default choice, saved-list identity/section, empty saved-list creation, source preservation, and existing replacement safeguards. No live server checks or native user interaction certification; signing certificate warning remains.


## Saved-list banner width (2026-10-19)

- Removed the 300-pixel list-status cap. Health uses the full available banner width; Patching reserves only the space needed for its update-options summary. Ellipsis applies only when the available space is actually exhausted; full text remains in the tooltip.
- Verified: Release build and existing patching-clarity/banner probe passed. This is a layout-only change; no live operations were run. Build retains the known missing-signing-certificate warning.


## Operator validation update (2026-10-20)

- U-39 closed: Shaun explicitly confirmed the People responsiveness issue resolved.
- U-21/U-22: Shaun reports other UI behavior seems fine so far in normal use; retain the specific Windows scaling, physical keyboard, and spoken screen-reader checks as unverified rather than treating them as reported defects.


## Saved-list display-name correction (2026-10-20)

- Requested: accept date/time punctuation without replacing it in the displayed list name.
- Implemented: preserve /, :, | and other filename punctuation through reversible encoding in .vlist filenames; contents remain one machine per line. Legacy .txt names and files are unchanged. Listing, loading, revisions, overwrite approval, delete and restore use the same mapping; literal percent sequences cannot alias separators.
- Existing sanitized names cannot be safely reconstructed; no guessing or automatic renaming is performed.
- Verified: 20 storage tests passed, one privileged file-symlink case skipped; Release build and workspace-safety probe passed. Tests cover the reported date/time name, collisions, reload, revision checks, delete/restore and safe separator encoding with dummy data. Existing missing-signing-certificate warning remains; no deployed package or live data changed.
