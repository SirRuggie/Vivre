# Saved-list organization — U-58/U-59 acceptance ledger

Living implementation record, 2026-10-20. Scope: simplify organization and allow folder choice while saving; preserve list names/files, overwrite approvals, tab state, patching and flat top-level lists. This work is included in **1.20.1**; see [release validation](release-1.20.1-validation.md). The execution evidence below records the implementation checkpoint before the release metadata update.

| Requested capability | Status |
| --- | --- |
| U-58: one searchable organizer instead of cascading move/rename/remove menus | Implemented; actual window/store probe passed |
| Existing right-click Move to folder remains available | Existing handler/picker reused; navigation regression passed |
| U-59: Save list folder dropdown and New folder action | Implemented using the existing TextPromptWindow and shared ListFolderChoice; actual nested prompt/save checks passed |
| New lists default top-level; existing submitted names retain their folder unless explicitly changed | Implemented; default, edited-name overwrite and concurrent-folder-move cases passed |
| Saved-list contents/overwrites/tab rename and folder persistence remain correct | Actual storage and workspace checks passed; existing row identity/results/selection retained |
| Cancellation, stale folders and write failures produce truthful outcomes | Tested: canceled save/create, overwrite denial, missing destination, failed content save, saved content/failed folder update and created folder/failed refresh |
| Independent review and corrections | Separate read-only reviewer accepted the corrected source with no remaining concrete blocker; no reviewer-run tests/builds |
| Documentation and delivery | Help, README, changelog, trackers and probe instructions updated; delivered in the commit containing this ledger |

## Behavior and boundaries

- Lists > Organize lists opens a single window with search, List/Current folder columns, folder choice and Move. Create/rename/remove folder actions are in the same window; removing a folder retains its lists at the top level.
- Save list uses the same folder selector. Its default follows the exact entered saved-list name from an in-memory snapshot while typing, without disk reads on each keystroke. Untouched overwrites never rewrite the folder assignment; an explicit selection moves the exact submitted name. New names default to top level, including reuse of a deleted name with an orphan assignment.
- Creating a folder is an explicit immediate operation; canceling a later list save does not delete that newly created folder. Canceling the folder-name prompt creates nothing.
- List contents and folder metadata remain separate existing stores. If contents save succeeds but the folder write fails, the tab retains its successful save/name and an explicit message says the folder could not be updated; no claim of an atomic two-file transaction is made.
- Unreadable optional folder metadata disables organization controls but permits list-content saves without rewriting that metadata. A destination removed after the dialog opened is rejected before saving when detected; later folder-write failures are reported as partial success.
- No core list storage, list-open behavior, patching, credentials, release metadata or live servers changed. S-1/U-40/U-42 and existing native U-21/U-22 validation remain open/deferred as previously recorded.

## Independent review corrections

- Prevented an overwrite under a different name from inheriting the original tab's folder or silently returning it to top level.
- Prevented an untouched save dialog from undoing an external folder move; explicit destination selection still applies.
- Kept Top level available and added visible accessible feedback when folder creation succeeds but refresh fails.
- Corrected an unintended text-encoding regression caught by source review and the exact menu-label probe; UTF-8 checks and final builds/probes pass.
- Added List/Current folder headers and explicit Top level values in the organizer; compact rows expose more lists without more menus.

## Execution evidence and limits

- Final Release Desktop/harness build: **zero errors, one existing missing local agent-signing-certificate warning**. No signed ZIP or deployment was produced.
- Existing list/folder storage tests: **25 passed, one skipped**; the skipped linked-payload recovery test requires unavailable Windows file-symlink privilege.
- Final `--list-organization-only`, `--workspace-safety-only`, and `--navigation-clarity-only` all passed. The latter retains right-click folder-picker and script-editor regressions; the new dialog/store probe replaces only the removed cascading move-menu assertions.
- Eight actual WPF layout renders cover Save/Organize at 440/640 widths in scoped light/dark themes; control bounds passed and representative renders were inspected. These are output/layout checks, not actual Windows DPI switching or application-wide theme certification.
- Dummy identities/list files, temporary settings and intercepted confirmations only. No native physical input, spoken screen-reader, production data, live servers or new whole-app performance/soak certification. Existing store primitives are reused; no background worker, polling or new scanning work was added.
- `git diff --check`, local documentation links, explicit UTF-8 text and unchanged Core/UpdateAgent/version scope checked before delivery. Existing generated `.artifacts/` is excluded from the commit.
