# Vivre UI/UX backlog

Living checklist — update as decisions, implementation, and validation progress.

Reviewed: 2026-10-19. Original audit baseline: `15663ab` (1.18.0 plus unpublished changes).

## How to use

- Keep U-1–U-22 identifiers stable; record decisions, commits, and validation under the matching item.
- An unchecked item is a finding or recommendation, not authorization to implement it.
- Mark complete only after its agreed behavior is implemented and validated; state remaining live-validation limits.
- Preserve existing operational behavior and reuse current components where practical.
- Next recommended item: **U-6**, making saved-list identity and unsaved membership changes visible.

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

## Workflow improvements

- [ ] **U-6 · Medium — Make saved-list identity and changes visible.**
  - **Why:** Users cannot easily distinguish a saved list from its modified working copy or confirm a successful save.

- [ ] **U-7 · Medium — Make Paste and Quick Add accept the same formats.**
  - **Why:** Quick Add accepts several separators, while Paste only splits lines; U-2 removed the old-name prefill but did not unify parsing.

- [ ] **U-8 · Medium — Add an explicit handoff from Health to Patching.**
  - **Why:** Switching sections opens a separate workspace instead of carrying the machines the user was inspecting.

- [ ] **U-9 · Medium — Keep machine names visible during horizontal scrolling.**
  - **Why:** Neither main grid freezes the identity column, making distant status columns harder to associate with servers.

- [ ] **U-10 · Medium — Show active update options beside patching actions.**
  - **Why:** Driver inclusion and exclusions still require opening menus despite affecting results.
  - **Progress:** U-1 completed the visible-source portion; visibility of drivers/exclusions remains undecided and unimplemented.

- [ ] **U-11 · Medium — Make forced reboot a clearly separate choice.**
  - **Why:** The pending-reboot dialog presents “Force-reboot first” as its primary action after the user chose Install.

- [ ] **U-12 · Medium — Show the originating tab in Activity.**
  - **Why:** Tab attribution exists in file logs but is absent from the shared Activity display.

- [ ] **U-13 · Medium — Save related CU settings together.**
  - **Why:** KB, target build, and month currently save separately as fields lose focus.

- [ ] **U-14 · Medium — Explain invalid settings beside their fields.**
  - **Why:** Some invalid numeric values silently revert, leaving users uncertain why their edit disappeared.

- [ ] **U-15 · Medium — Show timezone and target count when scheduling.**
  - **Why:** The scheduling flow should make both execution time and affected scope immediately unambiguous.

## Discoverability and polish

- [ ] **U-16 · Lower — Add search to growing list and script libraries.**
  - **Why:** Flat list menus and browsing-only script navigation become cumbersome as collections grow.

- [ ] **U-17 · Lower — Offer recovery for deleted saved lists.**
  - **Why:** Confirmation exists, but deletion has no Undo or recovery path.

- [ ] **U-18 · Lower — Make frequent secondary actions easier to discover.**
  - **Why:** Software, scheduling, deployment, and maintenance actions compete inside a large right-click menu.

- [ ] **U-19 · Lower — Use action-specific dialog buttons and consistent wording.**
  - **Why:** Labels such as “Save list” communicate consequences better than “OK,” and mismatched control names hinder discovery.

- [ ] **U-20 · Lower — Update the documentation index's release pointer.**
  - **Why:** It still calls 1.17.3 the latest candidate despite the current 1.18.0 release.

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
