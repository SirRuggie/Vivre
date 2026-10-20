# People email lookup delivery ledger

Scope: real, read-only AD lookup in a separate People section; no machine-operation changes.

| Capability | Acceptance | Status |
|---|---|---|
| Paste | Names/usernames/emails, one per line; preserve commas; add without losing results | Implemented and tested locally |
| Directory lookup | Real signed/sealed LDAP using current Windows account/default domain, bounded background requests, stop/retry | Implemented and tested locally |
| Matching | Unique exact matches; uncertain/nickname/multiple-account matches reviewed; no invented email | Implemented and tested locally |
| Review | Candidate panel, reason, department/account state, use/skip/close and revise | Implemented and tested locally |
| Results | Grid/filter, selected field copying, deduplicated resolved-email copy, explicit CSV export | Implemented and tested locally |
| Isolation | Keep results across navigation; no fleet operations or settings mutation; cancellation on close | Implemented and tested locally |
| Validation | Matching/error/stop/review/export tests, existing regressions, independent red team | Implemented and tested locally |
| Live AD | Reachable domain/controller and directory-read permission needed for environment validation | Passed current-domain smoke; scope below |

Results are session-only; export explicitly for durable output. No automatic directory-data files or email sending.

## Validation — 2026-10-19

- Release application build passed; 44 focused core tests passed (People matching/input plus AD Linux labels, install target scope and reboot approval).
- People Release probe passed exact/review/no-email, deduplicated copying, CSV formula escaping, paste preservation, append-during-lookup, Stop/late result, directory outage, per-entry failure, revising/skipping, filter and export-state assertions.
- Actual XAML offscreen layout checked at 800/1280 widths and 400/680 heights; normal height avoids extra page scrolling and short height retains scroll access to review controls. Dark-theme renders inspected. Light theme, physical input, screen-reader narration and native DPI are not claimed verified by these renders.
- Existing tab-handoff, workspace-safety and Actions menu Release probes passed; real shell navigation preserves People state and fleet accelerators cannot mutate hidden fleet workspaces.
- Live read-only AD check passed: generated nonexistent username returned zero candidates; current Windows account returned one exact account with mail; domain-qualified username returned the same account. Output records only counts/booleans.
- Independent read-only red team completed two corrective passes and a final refutation with no remaining material findings. Reviewer inspected source; execution evidence came from the implementing session.

## Corrections made during review

- Validate DOMAIN\username against the current naming context's authoritative crossRef before normalizing the SAM name; reject foreign-domain qualifiers.
- Gate fleet keyboard shortcuts while People is displayed; keep People state across section changes.
- Explicit Review/change match wording supports correcting an accepted match; opening a no-candidate row clears any unrelated active review.
- Move layout margin to the outer scroll container so full-height content does not add unnecessary scrolling.
- Live check exposed subordinate naming-context referrals; current-domain person queries now use DomainScope and continue to reject residual incomplete referral results.

## Operational boundaries

Real integration is implemented; session results are deliberately not automatically persisted. Explicit CSV export is available, with discard/exit prompts. Lookup reads the current AD domain, not every trusted domain or forest. RSAT is not required. Directory outages fail visibly and halt repeated batch attempts; incomplete results require a more specific input. Stop discards late results but waits for the bounded in-flight LDAP call.

No production account was modified. Synthetic tests cover ambiguous, disabled and missing-mail records; those cases were not deliberately sought in the live directory. No live patch/install/reboot operations, native dialog/clipboard interaction, long-running memory soak, or signed package validation were performed for this feature. Existing signing material is unavailable on this build context, so successful compilation does not assert a signed deployment package.

Released in 1.19.0 (2026-10-19); the original validation below predates subsequent UI corrections recorded in the UI/UX backlog.

## People flow follow-up acceptance — 2026-10-19

| Requested capability | Status |
|---|---|
| Fleet-like command bar, Quick Add and editable Paste list; no tabs or saved lists | Implemented and locally tested |
| Obvious bottom-right review launcher and instruction; preserve right-click/double-click | Implemented and locally tested |
| Responsive paste/result handling with measurements and Stop preserved | Implemented and synthetically measured; reported host pause not reproduced |
| Independent red team, regression checks, docs, commit/push | Review/tests/docs complete; commit contains this ledger |

### Follow-up validation and review

- Real People view/model probe passed matching/review/no-mail preservation, input validation with no partial mutation, search, pinned identity columns, duplicate export-state retention, navigation and hidden-fleet-shortcut isolation. Shared paste dialog hides machine add/replace choices and shows inline validation.
- Offscreen renders at 800/1280 by 400/680 verify fixed footer action geometry; short content remains scrollable. This is not native Windows DPI or physical input validation.
- Reused the Fleet toolbar style (moved unchanged to App resources), existing multiline list-entry dialog, and existing row-menu handlers. No People tabs or saved-list storage added; AD matching unchanged.
- Nine core People tests passed; Release workspace-safety, actions-menu and tab-handoff regressions passed after the shared style/dialog changes.
- Independent gpt-5.6-sol read-only reviewer found a footer menu placement reset; fixed and re-reviewed. Parent additionally caught and removed invalid DeferRefresh use during ObservableCollection insertion. Final independent review found no remaining delivery-blocking defect within the diff.

### Responsiveness measurement

Baseline, isolated dummy data: five additions plus layout 80.7ms; 2,000 additions plus layout 172.5ms; 2,000 immediately completed fake lookups plus layout 1,022.4ms in one synchronous call. These figures exclude native clipboard transfer and real AD.

The final probe uses real WPF dispatcher context, a 10ms UI heartbeat and a separate background timer observing its age; unrelated queued setup/render work is drained before timing. An initial contaminated measurement included pending prior render work and was discarded after isolating setup. Additions now yield in batches, so elapsed time is not continuous UI blockage. The accepted measured run recorded 2,000 additions/layout 577.7ms total, immediate fake lookups/layout 245.9ms total, with heartbeat ticks continuing and maximum sampled dispatcher gap 343.5ms (under the probe's 500ms threshold). This demonstrates cooperative input processing, not a universal latency guarantee or faster total list insertion.

Stop was dispatched during a pending lookup and during adding; late lookup results were discarded and all 250 stop-during-add inputs were preserved as Stopped. Quick Add preserved a new draft typed while submission was pending. The application no longer calls Clipboard.GetText when opening Paste list; the user pastes into the native text box, reviews, then confirms.

The exact reported five-person pause on the deployment host remains unverified. Native clipboard redirection can still have its own latency, and cancellation of an already-running LDAP request still waits for that request. No production directory or server operation was run in this follow-up. Diagnostic artifacts remain untracked; an earlier crashed probe temporarily locked the normal harness output and could not be terminated from this execution context (access denied), so a separate output directory was used; that process had exited by the final check.

## Current release — 1.19.2

People uses Quick Add and Paste list, Open review, and a row right-click menu with review/edit before copying; the footer Row actions button was removed. Results/review column widths and order are remembered independently with separate resets under Actions. Shaun confirmed the deployment-host responsiveness issue (U-39) resolved on 2026-10-20; closure is based on operator feedback, not a new local benchmark.

## Unreleased — U-49–U-51 and People U-52 (2026-10-20)

| Approved capability | Status |
|---|---|
| Review and Copy visible; Add/Paste aligned right; retry/export/clear/reset under Actions | Implemented and locally tested |
| Queue count and enabled state agree; selected resolved/skipped rows can still be reviewed | Implemented and locally tested |
| Resizable review, current input/pending count, disabled-account/no-email notices | Implemented and locally tested |
| Initial/loading/no-filter-match guidance, shown/total counts and Clear filters | Implemented and locally tested |
| Matching, Stop, copy scopes, saved column layouts and navigation preserved | Focused regressions passed; independent source review complete |

- Release builds passed and all nine People core tests passed. The expanded People probe uses synthetic identities/responses, real XAML and temporary settings; it verifies queue advancement without automatic candidate selection, no-mail acceptance, selected-row revisiting at zero pending, filtered-out queue targets, column settings disk round trip, loading/errors and Clear filters without result/export-state changes.
- Divider movement is exercised through the WPF splitter implementation; its height survives queue advancement and closing/reopening review. Both identity columns remain pinned. Rendered review layouts cover 800/1280 widths and 400/680 heights in dark/light; the probe now asserts actual theme color rather than relying on a requested theme name.
- Independent `gpt-5.6-sol` review found no material product issue; its loading-control visibility test suggestion was added. Parent fixed offscreen visibility/theme assumptions in the probe. Representative dark/light renders were inspected.
- Existing 2,000-entry responsiveness, Stop-during-add/lookup, late-response rejection, quick-add draft preservation, right-click/menu, export and Fleet isolation checks passed in two final sequential runs of the already-built harness. Maximum sampled dispatcher gaps were 251 ms and 188 ms. One preceding run failed the unchanged 500 ms limit at 608 ms (752 ms total addition/layout); earlier samples were 148–268 ms. No cause is proven for that variation and no threshold was relaxed; these measurements do not guarantee host latency.
- No live AD, native clipboard, server work, physical mouse/keyboard, Windows DPI or spoken screen-reader validation occurred in this batch. Scoped offscreen themes do not test application-wide theme propagation. No signed package was produced; release version remains 1.19.2 with these changes under Unreleased.


## Unreleased — U-56/U-57 People recovery (2026-10-20)

| Approved capability | Status |
|---|---|
| Selected-only local removal with one undo batch, exact row order and review decisions | Implemented and locally tested |
| Preserve other rows/review; reject stale selection, absent rows and active work | Implemented and locally tested, including both submission dispatcher gaps |
| Completion/copy guidance and Export CSV and exit / Exit without exporting / Cancel | Implemented; cancel/failure/Enter/Escape/close and busy-work checks passed |
| All-row CSV, dirty active snapshots, save-dialog recheck and later shell-close veto | Implemented; temporary file and actual shell guard checks passed |

The independent read-only `gpt-5.6-sol` review found and retested two source corrections: pending additions looked idle around both dispatcher yields, and Stop before any addition expired Undo and marked unchanged exported results dirty. The new submission guard covers preparation without preventing Add during lookup; its owning submission alone releases it. Stop changes export/undo state only when rows actually change. Final source refutation found no remaining material defect; the reviewer did not independently execute tests.

Release desktop/harness build, all nine People core tests, `--people-recovery-only`, and existing `--people-only` passed. Recovery covers filtered and stale selection, removed review rows, exact restoration, undo expiry, both submission gaps, draft-preserving Stop, overlapping old lookup/new submission, clipboard versus export state, all-row CSV including hidden/unresolved entries, cancellation/write failure, busy snapshots, save-selection reentrancy, safe modal defaults, and another shell close safeguard vetoing exit. The overlap test defers one new Background continuation to ContextIdle to exercise the ownership race deterministically. Initial probe assumptions about Normal-priority continuation draining were corrected; they were not product failures.

The 2,000-entry dummy-data run recorded addition/layout **204.4 ms**, lookup/layout **125.8 ms**, and maximum sampled dispatcher gap **137.2 ms** with 15 recorded heartbeat ticks (unchanged 500 ms limit). This is local cooperative-dispatch evidence, not a guarantee for real directory/network latency. The exit dialog is rendered and checked at 540×350 and 420×300 with scoped dark/light resources; scrollable explanatory text and wrapping action buttons support the minimum size.

All new checks use dummy identities and temporary settings/CSV files. Clipboard/save-path selection is intercepted; dialogs run offscreen with routed WPF input. No live AD, server operations, physical input, native Windows DPI, native save picker, screen-reader validation, signed package or deployment occurred. Version remains **1.19.2**, with this batch under **Unreleased**.
