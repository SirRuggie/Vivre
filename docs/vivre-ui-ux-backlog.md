# Vivre UI/UX backlog

Living checklist — update as decisions, implementation, and validation progress.

Reviewed: 2026-10-19. Original audit baseline: `15663ab` (1.18.0 plus unpublished changes).

## How to use

- Keep U-1–U-22 identifiers stable; record decisions, commits, and validation under the matching item.
- An unchecked item is a finding or recommendation, not authorization to implement it.
- Mark complete only after its agreed behavior is implemented and validated; state remaining live-validation limits.
- Preserve existing operational behavior and reuse current components where practical.
- Next recommended discussion: **U-8**, deciding the Health-to-Patching handoff; it remains unimplemented pending the desired transfer behavior.

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

- [ ] **U-8 · Medium — Add an explicit handoff from Health to Patching.**
  - **Why:** Switching sections opens a separate workspace instead of carrying the machines the user was inspecting.

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

- [ ] **U-18 · Lower — Make frequent secondary actions easier to discover.**
  - **Why:** Software, scheduling, deployment, and maintenance actions compete inside a large right-click menu.

- [x] **U-19 · Lower — Use action-specific dialog buttons and consistent wording.**
  - **Delivered:** Paste machines uses Add machines or Replace machines according to mode; Rename says Rename, and Save list retains its specific action label.
  - **Scope:** Existing operational and native confirmation dialogs remain unchanged; no additional confirmation step or dialog framework was introduced.

- [x] **U-20 · Lower — Update the documentation index's release pointer.**
  - **Delivered:** Index identifies 1.18.0 and Unreleased; 1.17.3 validation is explicitly historical.

## Validation gaps — not confirmed visual failures

- [ ] **U-21 · Medium — Verify narrow-window and high-DPI layouts.**
  - **Why:** Fixed-width search controls and non-wrapping filter rows create clipping risks despite the adaptive main toolbar.

- [ ] **U-22 · Medium — Complete keyboard and screen-reader testing.**
  - **Why:** Some controls have explicit accessibility names, but icon statuses and remaining fields need end-to-end verification.

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
