# Vivre UI/UX backlog

Living checklist — update as decisions, implementation, and validation progress.

Reviewed: 2026-10-19. Original audit baseline: `15663ab` (1.18.0 plus unpublished changes).

## How to use

- Keep U-1–U-22 identifiers stable; record decisions, commits, and validation under the matching item.
- An unchecked item is a finding or recommendation, not authorization to implement it.
- Mark complete only after its agreed behavior is implemented and validated; state remaining live-validation limits.
- Preserve existing operational behavior and reuse current components where practical.
- Next recommended item: **U-2**, preserving results when adding servers through Paste.

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

- [ ] **U-2 · High — Preserve existing results when pasting additional servers.**
  - **Why:** Paste rebuilds every row—even for unchanged lists—discarding results and selection.

- [ ] **U-3 · High — Protect the current workspace when opening or clearing lists.**
  - **Why:** These paths replace or remove rows without the replacement warning provided by Paste.

- [ ] **U-4 · High — Prevent silent saved-list overwrites.**
  - **Why:** Existing names overwrite files, and automatic filename cleanup can make different names collide.

- [ ] **U-5 · High — Warn before exiting with active fleet work.**
  - **Why:** Closing checks script work but lacks an equivalent fleet-operation warning explaining what stops or continues.

## Workflow improvements

- [ ] **U-6 · Medium — Make saved-list identity and changes visible.**
  - **Why:** Users cannot easily distinguish a saved list from its modified working copy or confirm a successful save.

- [ ] **U-7 · Medium — Make Paste and Quick Add accept the same formats.**
  - **Why:** Quick Add accepts several separators, while Paste only splits lines and positions the caret after existing names.

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
