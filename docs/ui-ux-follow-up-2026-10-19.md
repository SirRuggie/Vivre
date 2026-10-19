# UI/UX follow-up review — 2026-10-19

Point-in-time record — never edited after the fact. Live decisions/status belong in [the U backlog](vivre-ui-ux-backlog.md).

Baseline: `fb46160` on master. This is a source-backed heuristic review with isolated executable checks and existing offscreen renders, not a hands-on usability study or proof of optimality. Windows Computer Use initialization timed out twice, resetting its kernel; no native window interaction was possible. No production machine actions, AD queries, clipboard writes, or user-library writes occurred. Product code is unchanged.

## New findings

- **U-28 · High — Protect saved scripts from silent replacement.**
  - **Why:** A new name or sanitized filename collision can overwrite another script without warning.
- **U-29 · High — Make Server 2016 Stage clearly say it installs.**
  - **Why:** Stage software only copies files, but the similarly named CU action starts installation.
- **U-30 · Medium — Rename the misleading Staged badge.**
  - **Why:** The badge means a machine uses the CU lane, not that its update has been staged.
- **U-31 · Medium — Reset stale service suggestions when changing products.**
  - **Why:** A new software query can keep and remember the previous product's service name.
- **U-32 · Medium — Protect custom columns from accidental replacement.**
  - **Why:** Adding an existing column name replaces its script and immediately runs the replacement.
- **U-33 · Medium — Keep identity columns visible in People.**
  - **Why:** Horizontal scrolling hides the person or candidate name while comparing other details.
- **U-34 · Medium — Make People copy scope explicit.**
  - **Why:** Copy resolved emails includes hidden rows even when the grid is filtered.
- **U-35 · Lower — Show which People entries can be retried.**
  - **Why:** Retry unresolved stays enabled with nothing eligible and does not retry No email or Needs review.
- **U-36 · Lower — Avoid false unsaved-result warnings in People.**
  - **Why:** Pasting only existing entries marks an exported session dirty without changing its results.
- **U-37 · Lower — Remember grid widths and column order.**
  - **Why:** Visibility is saved, but operators must repeat manual sizing and rearranging after reopening.

## Evidence and smallest useful changes

| ID | Evidence at baseline | Simple direction |
|---|---|---|
| U-28 | `ScriptLibrary.cs:75` sanitizes names/categories and `:344` replaces existing files; both ScriptsViewModel.SaveScript and ScriptRunnerViewModel.SaveScript call it without destination approval. Isolated actual-library test: `Dummy:A` and `Dummy?A` mapped to the same file and the original content was replaced. | Distinguish saving the open script from replacing a different script; validate names and reuse the saved-list collision/approval approach where applicable. |
| U-29 | `WorkspaceView.xaml:571` labels the action Stage; installation is explained in its tooltip. `StagedInstallInteraction.cs:163` starts StageLcuForAsync when preflight is ready, without a separate install acknowledgment. Software staging explicitly promises no installation. The CU action can target all eligible lane machines when none are selected. | Label it Install CU (no reboot), show the actual target count/scope beside it, and remove the blanket business-hours safety claim; keep the underlying workflow. |
| U-30 | `WorkspaceView.xaml:1070` sorts by RequiresStagedPatching and its Staged badge depends only on OS build and that configuration flag. Right-click offers Mark as Staged patching/Remove Staged flag. | Use CU lane or DISM lane consistently in badge/menu/settings; reserve staged/completed wording for actual results. |
| U-31 | `SoftwareCheckWindow.xaml.cs:57` updates the service field only for recognized product names. Changing CrowdStrike to an unrecognized product leaves CSFalconService checked and populated; Run then remembers the new pairing. | Track suggested versus manually edited service text; clear or recalculate only stale suggestions and keep an intentional override. |
| U-32 | `WorkspaceViewModel.cs:1888` replaces a same-name custom column; `ColumnsWindow.xaml.cs:116–119` immediately executes that spec across the loaded machines. | Detect an existing name and explicitly offer replacement before saving/running; show the affected count without a new multi-step wizard. |
| U-33 | Neither People DataGrid declares FrozenColumnCount. Existing 800px render shows horizontal scrollbars in both results and candidates; fleet grids already pin Name. | Reuse the fleet identity-column pattern for Original input and candidate Name, with reasonable width limits. |
| U-34 | `PeopleView.xaml.cs:51` passes all Model.Rows into CopyEmails; status filter affects only Results. Actual model test showed one visible Not found row while all-row copy produced two resolved email values. This may be intentional global-copy behavior, but its scope is not stated as clearly as Export all. | Rename to Copy all resolved emails with a count; keep right-click selected copying and deduplication. No need for another export workflow. |
| U-35 | `PeopleViewModel.cs:132` retries only Lookup failed, Stopped and Not found; toolbar enablement checks only IsBusy. No email stays No email in the executable check. | Label/count eligible retries and disable at zero; direct uncertain matches to Review rather than silently doing nothing. Do not automatically discard reviewed choices. |
| U-36 | `PeopleViewModel.Add` calls Refresh even with zero additions; Refresh sets HasUnexportedResults whenever rows exist. Executable check: MarkExported then duplicate-only Add sets the flag again. | Mark dirty only on actual input/result/decision changes; keep exit protection. |
| U-37 | SaveColumnLayout persists CustomColumns and HiddenColumns only; DisplayIndex is read for exports but no width/order persistence handler exists. People columns use fixed initial widths. | Remember widths/order per grid using existing settings; provide a reset and preserve pinned identity columns. |

## Coverage and strengths preserved

| Area reviewed | Outcome |
|---|---|
| Paste, Quick Add, open/save/delete lists and tabs | Existing add-by-default, replacement guards, named list identity, collision checks and deleted-list recovery remain sensible; no new duplicate U-item. |
| Fleet Actions and right-click menus | Copy/Connection/Software/Export/Schedule/WUG grouping and target labels reduce ambiguity; preserve shared handlers and stale-target safeguards. U-29/U-30 concern the remaining CU action/flag terminology. |
| Health details, software checks, staging | Separate software diagnostics and explicit file-copy destination previews are good; stale service autofill is U-31. |
| Patching, source/options, schedules and reboot | Per-tab source summary, separate reboot choices and timezone/count information are strengths; CU labels remain inconsistent. No live operational validation attempted. |
| Scripts library and runner | Search and unsaved-edit protection are good; destination overwrite protection lags behind saved lists (U-28). |
| Columns/settings | Grouped CU saves and inline numeric errors are good; custom-column replacement and remembered grid arrangement need attention. |
| People paste/matching/review/copy/export/retry/exit | Explicit uncertain-match review, no invented email, navigation preservation and CSV escaping are strengths; U-33–U-36 are follow-up usability gaps, not evidence that all matching is broken. |
| Activity/help/navigation | Source-attributed activity and keep-alive sections remain useful; documentation and search provide recovery paths. |
| Cross-domain RDP | Reviewed connect/edit/remove/session menu structure; removal describes nested contents and credential retention. No live RDP or scale changes; existing pinned-scale constraints preserved. |
| Keyboard/DPI/screen readers | Existing U-21/U-22 still own these unverified native checks; not renumbered or falsely closed. |

## Executable evidence

A disposable console under `.artifacts/usability-review` references the real Desktop/Core projects and uses dummy ScriptLibrary storage plus an inert IPeopleLookup. It ran successfully in Release; the existing missing-agent-signing-certificate warning remains unrelated to these checks. Results:

```json
{
  "scriptNameCollision": true,
  "previousScriptReplaced": true,
  "visibleNotFoundRows": 1,
  "emailsFromAllRowsDespiteFilter": 2,
  "noEmailAfterRetry": "No email",
  "duplicatePasteMarksExportedSessionDirty": true
}
```

These are reproductions/observations, not a passing product regression suite. Wording/affordance recommendations are reviewer judgments grounded in the cited handlers; their frequency and severity have not been measured with operators. U-31 was traced through source rather than exercised through a native dialog. This follow-up had no independent agent review. Existing `.artifacts` remains untracked.

## Research basis

- [Microsoft: Menus and context menus](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/menus-and-context-menus): contextual secondary commands and direct access for frequent actions support retaining the current Actions/menu split rather than a redesign.
- [Nielsen Norman Group: usability heuristics](https://www.nngroup.com/articles/ten-usability-heuristics/): consistent terminology, visible system state, error prevention and user control guided this review. These are evaluation principles, not proof that a particular layout is optimal.

## Suggested batches

1. U-29/U-30/U-31: operational wording and stale autofill, small visible changes with focused dispatch/input tests.
2. U-28/U-32: replacement protection; reuse existing validation/confirmation concepts and test cancellation/collision cases.
3. U-33–U-36: People usability polish; no changes to matching/AD semantics required.
4. U-37: separate layout-persistence change, with defaults/reset/multi-tab checks.

No fixes were made in this review. U-21/U-22 remain open for actual Windows interaction validation.
