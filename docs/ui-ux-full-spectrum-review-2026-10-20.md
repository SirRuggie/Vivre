# Vivre UI/UX review — 2026-10-20

> **Point-in-time record — never edited after the fact.** Live decisions/status belong in [the U backlog](vivre-ui-ux-backlog.md).

Baseline: `09291756fea7ebb98f9c507dc5a00c71d0609231` on `master`; release metadata 1.19.2 with subsequent work under Unreleased.

## Verdict

Keep the navigation, workspace tabs, operational grids, and right-click shortcuts. The main weakness is the ordering and emphasis of work inside those screens, especially People. People exposes retry, filters, counts, and eight columns before making its simple purpose obvious: add people, resolve uncertainty, and copy addresses.

The review also found three operational interaction problems that take priority over visual changes: the CU panel can widen an ineligible selection, the credential selector can disagree with the applied identity, and dismissing the mixed CU decision can start installations on the other machines.

This is a screenshot and source-backed expert review with external research, **not a measured user study or proof of optimality**. No application code, settings, saved lists, directory records, or remote machines were changed.

## Review method and acceptance

Shaun supplied current screenshots of Patching with cleanup active, empty Health, and empty People. The parent inspected these images and reconciled source findings with three independent `gpt-6-astra` research reviews: People; Health/Patching and operations; navigation, Settings, Scripts, RDP and accessibility. Workers received separate, bounded tasks and made no edits. Their source/research findings were checked by the parent; they did not independently inspect the screenshot pixels.

| Requested review capability | Result and evidence boundary |
|---|---|
| Evaluate current screenshots and People flow | Completed: visual hierarchy, entry, empty/results/review/completion states compared with source. |
| Review the whole app's UI surfaces | Completed as a bounded source review using the coverage inventory below; this does not mean every native state or backend execution path was exercised. |
| Research established interaction guidance | Completed: Microsoft Windows, NN/g and IBM Carbon primary guidance; applicability and limits recorded below. |
| Separate defects, design improvements and existing backlog | Completed: U-45–U-57 below; U-21/U-22 remain validation work; U-40/U-42 remain user-deferred. |
| Demonstrate native usability, performance or accessibility | Not performed: no live app walkthrough, timing study, real DPI/multi-monitor test, spoken screen-reader test, AD request or server action in this pass. |
| Implement the recommendations | Not part of this research request; all new items remain unchecked in the backlog. |

The evidence terms used below are:

- **Source-confirmed behavior:** directly follows from current bindings/handlers; live execution was not performed.
- **Screenshot-supported:** directly visible in the supplied images and consistent with source.
- **Design recommendation:** a research-informed hypothesis whose improvement should be checked with actual operator tasks.

## Ranked findings

### U-45 · High — Keep CU actions within the chosen machines

- **Why:** Selecting only ineligible machines can make Install CU target other eligible machines in the tab.
- **Evidence:** [WorkspaceViewModel.cs:3870](../source/Vivre.Desktop/ViewModels/WorkspaceViewModel.cs#L3870) filters the selection for CU eligibility and falls back to all eligible rows if the filtered result is empty; [WorkspaceView.xaml.cs:1187](../source/Vivre.Desktop/WorkspaceView.xaml.cs#L1187) passes that set to the install workflow. When scan/package gates pass, [StagedInstallInteraction.cs:159](../source/Vivre.Desktop/StagedInstallInteraction.cs#L159) dispatches it.
- **Concrete case:** Select unflagged Server 2016 machine A while flagged machine B exists in the same tab: A is filtered out and B becomes a target. Verify shares this fallback. The toolbar tooltip says all machines only when none are selected.
- **Recommendation:** Choose selection versus whole tab **before** filtering eligibility, reusing the rule already used by `ComponentCleanupTargets`; show an empty-eligible-selection explanation and the actual target count. Preserve all package, scan, coordination and reboot gates.
- **Confidence/acceptance:** High, source-confirmed; inert-service tests must prove zero dispatch for a nonempty selection containing no eligible rows, plus no-selection, mixed and eligible selections. This is a CU-panel exception, not a reopening of general U-24 scope fixes.

### U-46 · High — Make the credential choice match the identity in use

- **Why:** Switching to Windows login disables Apply while the previously applied explicit credentials remain active.
- **Evidence:** [SettingsPage.xaml:116](../source/Vivre.Desktop/SettingsPage.xaml#L116) disables the credential-fields grid when `UseExplicitCredentials` is false, but Apply is inside it at line 142; [SettingsViewModel.cs:41](../source/Vivre.Desktop/ViewModels/SettingsViewModel.cs#L41) clears the active store only when Apply executes. No selection-change hook applies the radio choice.
- **Recommendation:** Keep Apply outside the disabled fields, and distinguish the applied identity from an edited choice until application succeeds. Retain session-only credential storage.
- **Confidence/acceptance:** High, source-confirmed and independently rechecked; exercise explicit credentials → Windows login → Apply using an in-memory store and verify both display and store agree without contacting a server.

### U-47 · High — Separate skipping CU machines from cancelling an install

- **Why:** Cancel or closing the CU decision can still start installations on the other machines in a mixed batch.
- **Evidence:** [StagedInstallDecisionDialog.xaml.cs:33](../source/Vivre.Desktop/StagedInstallDecisionDialog.xaml.cs#L33) defaults to Cancel; [StagedInstallInteraction.cs:87](../source/Vivre.Desktop/StagedInstallInteraction.cs#L87) routes that outcome to the normal-machine install. The dialog does display “Other machines in this run install normally,” and the Cancel tooltip explains the partial skip, so this is not wholly undisclosed behavior.
- **Recommendation:** Offer an explicitly named partial-skip choice, including the other-machine count, and a genuine cancel/dismiss path for the not-yet-started batch. Renaming one button alone does not address Escape/title-bar dismissal. Agree this interaction change before implementation; do not attempt to undo work already running.
- **Confidence/acceptance:** High source confidence; deliberate partial continuation is an existing design choice, but its Cancel semantics are misleading. Test explicit skip, Cancel, Escape and close separately with captured dummy targets.

### U-48 · Medium — Keep the operation badge and progress message consistent

- **Why:** A machine can say Scanning while its message and progress describe component cleanup.
- **Evidence:** The supplied Patching image shows this contradiction. [SmbAgentLane.cs:197](../source/Vivre.Core/Updates/SmbAgentLane.cs#L197) emits a Scanning bootstrap status; [WorkspaceViewModel.cs:4346](../source/Vivre.Desktop/ViewModels/WorkspaceViewModel.cs#L4346) records later Cleaning progress without restoring the phase, whereas other statuses pass through `ApplyStatus`. The cleanup badge in [WorkspaceView.xaml:966](../source/Vivre.Desktop/WorkspaceView.xaml#L966) requires the Cleaning phase.
- **Recommendation:** Present the actual active operation consistently in the row and detail view while retaining terminal/error/cancellation precedence; keep elapsed time and real progress, without inventing an ETA.
- **Confidence/acceptance:** High, screenshot-supported and source-confirmed; test bootstrap → cleanup progress → terminal/Stop sequences. This does not establish that cleanup itself is stuck or slow.

### U-49 · Medium — Organize People around add, review and copy

- **Why:** Retry leads the screen while the main output action is hidden under Actions.
- **Evidence:** [PeopleView.xaml:18](../source/Vivre.Desktop/PeopleView.xaml#L18) places retry/Stop/Actions before input; [PeopleView.xaml.cs:95](../source/Vivre.Desktop/PeopleView.xaml.cs#L95) puts all-resolved-email copy in the menu. The empty screenshot shows no visually dominant starting action. Entry controls also occupy a different horizontal position from Fleet.
- **Recommendation:** Use Fleet's stable toolbar/input alignment, make Paste/Add obvious at first use, and keep Review matches and Copy all resolved emails directly discoverable when useful. Keep retry, export, reset and clearing as secondary actions; keep Stop accessible during work. Use restrained status emphasis for Needs review/failure and retain text, not color alone.
- **Confidence/acceptance:** Strong screenshot/source evidence, with the proposed hierarchy a design recommendation; compare first-use and repeat-use tasks before choosing final placement. Preserve right-click, Enter, session-only results and the absence of People tabs/saved lists.

### U-50 · Medium — Make the review queue button match its count

- **Why:** Review matches (0) can remain enabled and open no pending review.
- **Evidence:** [PeopleViewModel.cs:80](../source/Vivre.Desktop/ViewModels/PeopleViewModel.cs#L80) counts Needs review for the label, while `HasReviewable` at line 87 checks any candidates; [PeopleView.xaml:33](../source/Vivre.Desktop/PeopleView.xaml#L33) combines these. `StartReview()` at line 215 only finds Needs review when no row is supplied.
- **Recommendation:** Give the queue button queue-specific eligibility; preserve selected-row Review/change match for resolved or skipped rows and clarify the footer's selected-or-next behavior.
- **Confidence/acceptance:** High, source-confirmed; check unresolved, all-resolved, skipped-only and selected-resolved states. Do not remove the selected-row capability as a supposed duplicate.

### U-51 · Medium — Give People review enough space and context

- **Why:** A short, wide candidate grid makes similar accounts harder to compare.
- **Evidence:** [PeopleView.xaml:61](../source/Vivre.Desktop/PeopleView.xaml#L61) fixes the candidate viewport at 160 DIPs with 1,075 DIPs of default columns; results use 1,270 DIPs. [PeopleView.xaml.cs:110](../source/Vivre.Desktop/PeopleView.xaml.cs#L110) brings the bottom panel into view; acceptance advances the queue without a remaining-count indicator in its title.
- **Recommendation:** Reuse the existing panel with a resize handle, clearly identify the input under review and remaining queue, and put the fields needed to distinguish accounts together. Reduce secondary detail's default prominence while retaining access to all fields and always exposing disabled-account/no-email warnings.
- **Confidence/acceptance:** Source dimensions confirmed; actual comparison difficulty at a given display size is a design hypothesis, not a measured clipping failure. Check similar names, long emails, multiple candidates and filtered queues at native laptop/RDP widths. Preserve deliberate candidate selection, frozen identity and saved layouts.

### U-52 · Medium — Explain empty and filtered views, with a direct next action

- **Why:** Blank results or unchanged totals can leave users unsure whether to add, wait or clear a filter.
- **Evidence:** People has no initial/no-match presentation, and [PeopleViewModel.cs:79](../source/Vivre.Desktop/ViewModels/PeopleViewModel.cs#L79) reports whole-session counts even when `Results` is filtered. Health's [Get started card](../source/Vivre.Desktop/WorkspaceView.xaml#L1282) tells users to find Paste but its only button opens help. [ScriptsPage.xaml.cs:47](../source/Vivre.Desktop/ScriptsPage.xaml.cs#L47) deliberately retains the open script through search; [HelpViewModel.cs:43](../source/Vivre.Desktop/ViewModels/HelpViewModel.cs#L43) can return no topics without an explanatory view.
- **Recommendation:** Reuse common empty-state styling: Paste/Add or Open list for first use, Showing X of Y and Clear filters for filtered-empty, and distinct loading/error states. Label a retained open script as such rather than implying it matched. Fleet already has a no-filter-match state: reuse it, do not rebuild it.
- **Confidence/acceptance:** High source confidence; verify initial empty, no matches, retained script, loading and error cases without changing filtering semantics or discarding edits. Shared empty-state styling is currently non-interactive, so interactive buttons require an appropriate container rather than inheriting `IsHitTestVisible=False`.

### U-53 · Medium — Give Fleet commands a clearer visual hierarchy

- **Why:** Patching, health checks and workspace housekeeping compete across several equally prominent bands.
- **Evidence:** [MainWindow.xaml:379](../source/Vivre.Desktop/MainWindow.xaml#L379) groups Ping, Vitals, Monitor, Remove Offline, Stop, Clear results, source/options and patch operations; the screenshot adds tabs, saved-list summary, filters and the CU panel above the table.
- **Recommendation:** Lead Patching with Scan/Install, keep health checks together and move occasional housekeeping into the existing menu. Keep Stop and selected-target context predictable. Consider an explicit Options label while retaining the current source in the tab/banner. Keep the useful CU sequence contextual.
- **Confidence/acceptance:** Screenshot-supported design recommendation, not a demonstrated overflow failure. Test experienced operators before relocating familiar commands; do not remove source visibility, right-click access, keyboard shortcuts or selected/all distinctions.

### U-54 · Medium — Restore normal scrollbar track behavior

- **Why:** Clicking the scrollbar track cannot page through long grids, editors or panels.
- **Evidence:** The global [App.xaml:180](../source/Vivre.Desktop/App.xaml#L180) template disables both track repeat buttons at lines 189/198 and uses an 8-DIP bar. Wheel scrolling and thumb dragging are separate and are not claimed broken.
- **Recommendation:** Restore normal track paging and an easier pointer hit area while retaining the slim visual treatment; reuse standard scrolling behavior.
- **Confidence/acceptance:** High markup confidence; verify vertical/horizontal track clicks, dragging, wheel and keyboard scrolling across a grid, editor and dialog. Native pointer accessibility remains part of U-21/U-22.

### U-55 · Low — Shorten the path for moving a saved list

- **Why:** Organizing one list requires navigating several cascading menus.
- **Evidence:** [MainWindow.xaml.cs:1757](../source/Vivre.Desktop/MainWindow.xaml.cs#L1757) builds Lists → Organize lists → Move list → list → destination; folder rename/remove also reuse a search box labeled Search lists.
- **Recommendation:** Keep bookmark folders and top-level lists, but offer Move to folder directly for a chosen list, using a small destination picker if needed; use folder-specific search labels. Do not add an Ungrouped category or a full file-manager screen.
- **Confidence/acceptance:** Source-confirmed path, lower-priority design refinement; compare organizing a few lists with the current route. U-44's implemented persistence and file safety remain valid.

### U-56 · Low — Allow removal of an accidental People entry

- **Why:** A mistaken addition can be edited, but removing it requires clearing the whole session.
- **Evidence:** [PeopleView.xaml:41](../source/Vivre.Desktop/PeopleView.xaml#L41) disables deletion and the [row menu](../source/Vivre.Desktop/PeopleView.xaml.cs#L204) offers review/edit/copy only.
- **Recommendation:** Add Remove selected from results through the existing row menu, with clear local scope and simple undo where practical; define safe behavior during an active lookup.
- **Confidence/acceptance:** High source confidence; a workflow enhancement, not data loss already observed. Preserve all other rows and review decisions, and make clear this never deletes an AD account.

### U-57 · Low — Make People completion and exit choices explicit

- **Why:** After copying emails, the exit warning still talks about unexported results without offering export there.
- **Evidence:** [PeopleView.xaml.cs:120](../source/Vivre.Desktop/PeopleView.xaml.cs#L120) uses a Yes/No discard warning; copy reports success but only CSV export marks full-session results exported.
- **Recommendation:** State what would be lost and offer Export CSV, Discard and Cancel where applicable, accounting for active lookup. Keep the normal copy-only task easy to finish.
- **Confidence/acceptance:** Source-confirmed behavior; the warning is not inherently false because copied email addresses omit unresolved inputs and review history. Never equate partial clipboard copy with a complete export; preserve the session on export failure/cancellation.

## Recommended People arrangement

This is a proposed arrangement to validate, not a request for another workspace system:

| Area | What belongs there |
|---|---|
| Top toolbar | Stable positions matching Fleet; add/name input and Paste on the right; common review/copy actions visible; secondary commands in Actions. |
| Filter/status row | Search, status filter, Showing X of Y, selection count; durable directory/session context separate from transient feedback. |
| Main content | An actionable first-use state, a scannable result grid, or a clear no-match/error state. |
| Review area | Existing in-place panel, resizable; original input, candidate comparison, remaining count, Use selected match, Skip and Close. |
| Bottom bar | Existing shell-wide visual treatment and Open review access; no return of the rejected Row actions dropdown. |

Retain a table: users need to compare and copy many people efficiently. Make input, result identity, email and unresolved status easy to scan, with additional detail available when it helps a decision. Avoid adding tabs, saved People lists, a wizard, or cosmetic cards for every row. Keep commands in stable positions rather than constantly moving them as state changes.

## What is already working well

- Shallow Fleet/Health/Patching/People/Scripts navigation and pinned Settings provide recognizable destinations.
- Fleet's distinct all/selected controls, selection count, Select shown and guarded context-menu scope already address most bulk-action ambiguity.
- Right-click menus now contain useful actions with logical groups; preserve them as shortcuts while keeping common tasks visible.
- Per-tab update settings and source labels are valuable safeguards; their visual repetition can be refined without losing context.
- List append/replace/new-tab choices, saved-name fidelity, overwrite protection, deletion recovery and folders protect real operator work.
- People retains existing results, freezes identity columns, remembers layouts, requires explicit ambiguous-match review and never fabricates an email address.
- Software staging states that it copies only; software checks offer reviewable quick picks and separate service checking.
- Scheduling exposes target counts and time zone; details separate readable diagnosis from technical error text.
- Script search, dirty-edit protection, resizable editors and shared execution handlers are useful foundations to reuse.
- RDP keeps sessions alive across navigation and exposes connection guidance; its fixed session-scale invariant must remain intact.

## Coverage inventory and limits

| Surface | Examined | Result |
|---|---|---|
| Shell/navigation/toolbar/status | MainWindow, App resources, section and selection bindings | Hierarchy recommendations; credential and scrollbar findings elsewhere; no demonstrated general selected/all regression. |
| Health/Patching | WorkspaceView, relevant WorkspaceViewModel commands, context menus, cleanup status transport | U-45/U-48/U-52/U-53; actual server operations not exercised. |
| People | PeopleView, PeopleViewModel, row/review/copy/export/exit handlers and reused paste dialog | U-49–U-52/U-56/U-57; no AD requests or native input study. |
| Lists/input/columns | MainWindow list handlers, LoadComputersWindow, TextPromptWindow, ColumnsWindow | U-55; existing list choices/guards retained; U-40/U-42 not reopened. |
| CU dialogs | StagedInstallDecisionDialog, StagedPatchingNeededDialog, LcuPackageNeededDialog, LcuPackageReadDialog and relevant interaction code | U-47; positive explicit installation/package guidance; no package changes. |
| Software/maintenance/scheduling | DeployWindow, SoftwareCheckWindow, MaintenanceWindow, WugStateWindow, ScheduleWindow, ExcludeUpdatesWindow; bounded handlers | Scope/action/error wording reviewed; no additional confirmed workflow defect beyond listed findings; no live dispatch. |
| Machine details/activity | ComputerDetailWindow and shell activity/dock bindings | Diagnosis/detail separation is good; operation-phase consistency needs follow-up. |
| Scripts | ScriptsPage, ScriptRunnerWindow, relevant view models and editor/search handlers | U-52; existing dirty-edit and execution approvals preserved. |
| Settings | SettingsPage/SettingsViewModel and credential application handler | U-46; grouped cards are a useful foundation. |
| RDP | CrossDomainRdpView, RdpSessionView inventory, RdpHostDialog/RdpFolderDialog and targeted handlers | Existing U-22 needs explicit credential-field labels/error announcements in older RDP dialogs; native effect not verified. No RDP connection or scaling change. |
| Help/About | HelpWindow/HelpViewModel, targeted HelpContent and AboutWindow presentation | U-52 search feedback; About is lower-frequency content, not a reason to redesign the operational shell. |

Coverage is a targeted review of all named surfaces, not every source line or every state permutation. No new performance benchmark, memory-leak investigation, native contrast measurement or security penetration test was performed. A screenshot cannot establish response time or accessibility conformance.

U-21/U-22 remain open for native scaling, keyboard and spoken screen-reader checks. In particular, RDP host/folder credential inputs use adjacent text without explicit automation-name associations, and their validation messages lack the newer announcement helper; add these to that existing work rather than duplicating an identifier. U-39 remains operator-confirmed resolved. U-40/U-42 remain intentionally deferred until their rare conditions matter or storage delays are measured.

## Research and how it applies

These are design inputs, not mandatory pixel specifications or evidence that any one arrangement is universally best.

| Source | Applied principle |
|---|---|
| [Microsoft: command bars](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/command-bar) | Prioritize common commands, group related work and keep shared actions in consistent locations. |
| [Microsoft: menus and context menus](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/menus-and-context-menus) | Keep context shortcuts while giving frequent actions direct access when space permits. |
| [Microsoft: dialog controls](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/dialogs-and-flyouts/dialogs) | Use specific decision labels and a safe dismissal path. |
| [Microsoft: panning](https://learn.microsoft.com/en-us/windows/apps/develop/input/guidelines-for-panning) | Preserve expected mouse and keyboard scrolling interactions. |
| [Microsoft: accessibility checklist](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-checklist) | Verify accessible names, focus and announcements with assistive technology. |
| [Microsoft: navigation basics](https://learn.microsoft.com/en-us/windows/apps/design/basics/navigation-basics) | Shallow, predictable paths support movement between related tasks. |
| [NN/g: empty states](https://www.nngroup.com/articles/empty-state-interface-design/) | Explain absence of content and provide an immediate relevant next action. |
| [NN/g: data-table tasks](https://www.nngroup.com/articles/data-tables/) | Arrange information for locating, comparing and acting on records. |
| [NN/g: system-status visibility](https://www.nngroup.com/articles/visibility-system-status/) | Make feedback agree with what the system is actually doing. |
| [NN/g: progressive disclosure](https://www.nngroup.com/articles/progressive-disclosure/) | Keep the main task visible and reveal secondary detail when needed. |
| [IBM Carbon: data tables](https://www.carbondesignsystem.com/building-blocks/core/components/data-table/guidelines) | Distinguish table-wide utilities from selected-row actions and use overflow judiciously. |

Windows/Carbon component examples use technologies other than Vivre's WPF stack; transfer the interaction principles without adding a new framework. Numeric toolbar limits are heuristics, not pass/fail rules. Forum anecdotes can suggest questions, but do not prove the best layout for this operator or justify undoing working expert shortcuts.

## Suggested batches and validation

1. **Operational correctness: U-45, U-46, U-47, U-48.** Separate small commits are appropriate even if reviewed together; use inert services to verify targets, dismissal and displayed state before any live pilot.
2. **People flow: U-49, U-50, U-51 and the People portion of U-52.** One coherent layout pass using existing handlers, with no tabs or saved lists.
3. **Shared navigation: U-53, U-54, U-55 and remaining U-52 surfaces.** Reuse existing menus/styles; validate stable keyboard paths and pointer behavior.
4. **People recovery/completion: U-56/U-57.** Smaller quality-of-life work after the main flow is agreed.

To assess improvement rather than just appearance, use the same representative tasks before and after changes: paste a mixed People list, correct an input, resolve similar accounts, recover from an empty filter, copy the intended addresses; open/append/save/move a machine list; select a subset for an inert patch preview; interpret cleanup status; cancel a mixed install before dispatch; find and edit a script. Observe successful completion, wrong-target/action mistakes, backtracking, time to the intended control and ability to explain the current state.

Check normal and smaller native windows, 100/125/150/200% Windows scaling, keyboard-only operation and Narrator as already scoped under U-21/U-22. Use synthetic identities and inert services first; live actions require their own authorization. Do not claim statistically proven optimality from a single walkthrough or passing offscreen probes.
