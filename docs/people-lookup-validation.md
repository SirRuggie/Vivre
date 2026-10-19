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

Version remains 1.18.0 with this work recorded under Unreleased, consistent with repository release policy.
