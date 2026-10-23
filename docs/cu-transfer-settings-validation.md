# Configurable CU file transfers — 2026-10-23

Follow-up requested by Shaun to the 1.20.2 one-slot mitigation: an adjustable setting beside install concurrency, default **2**, without changing remote installation/reboot behavior.

## Acceptance ledger

| Requested capability | Status |
| --- | --- |
| Settings control beside Max simultaneous installs, default 2 | Implemented and tested: production control focus-loss handler, inline validation, accessible name and two local card renders |
| Preference survives restart; old settings use default 2 | Isolated settings-file round-trip passed, older JSON defaults to 2, install limit/future keys preserved; startup application reviewed in source |
| Shared across tabs; raising/lowering affects waiting work without canceling active transfers | Implemented and tested with controlled active jobs/waiters; same static counter covers all lane instances in one process |
| Remote installs remain independent; integrity checks retained | Focused full-package/SMB/integrity regressions passed; remote installation remains outside the file slot |
| Documentation and independent review | README, Help, changelog and architecture/backlog links updated; independent read-only review completed without a substantive finding |
| Commit/push | Included in the commit containing this ledger; verify remote master at handoff |
| APVPatching production responsiveness | Unverified; no live update, replay or deployment in this task |

This change remains a load-control mitigation. The setting caps concurrent per-host CU file jobs in one Vivre process, not bandwidth or separate app instances. Initial package-choice preflight remains separate. No claim of resolving the production freeze is made.

## Behavior

- **Settings ▸ Patching ▸ Max simultaneous CU file transfers**, directly below **Max simultaneous installs**: default **2**, range **1–200**, stored in personal `AppSettings.MaxSimultaneousCuTransfers`.
- Valid input applies on focus loss. Invalid, empty, fractional or out-of-range input stays visible with an error and leaves the active/saved limit unchanged. Settings use the existing asynchronous persistence/error-reporting mechanism.
- Startup applies the saved value before constructing workspaces. An absent key uses 2; an out-of-range stored integer is clamped to 1–200. No settings migration is needed.
- Increasing the limit wakes queued jobs; reducing it waits for active file jobs to drain below the new limit. The shared gate is not replaced, and active jobs are not canceled by a setting edit.
- The limit covers per-host CU resolution/checking and the CU drop/copy/verification step. Normal Windows Update concurrency, install target selection, reboot approval and source/destination integrity checks are unchanged.

## Validation

- Release solution build passed with zero errors and the existing missing local update-agent signing-certificate warning. No signed ZIP was built or deployed.
- Final focused regression run: **45 passed, 0 failed, 0 skipped**, covering CU admission/cancellation, full-package stage, SMB lane, copy integrity and personal settings persistence. Real local dummy copies were checked at limits 1, 2 and 5; a controlled test proves two active jobs can overlap and reducing to one keeps the third waiting until both earlier holders drain.
- Production Settings-control probe passed with isolated temporary stores: numeric correction, real `LostFocus` event wiring, runtime limit changes, unchanged values after invalid edits, no application while partially typing, accessible input naming and existing settings regressions.
- The new production card was rendered and visually inspected at 600 and 1000 pixels; the label, default-2 input and explanatory text were legible. The fresh render page needed a normal layout pass before detaching the card; the corrected render harness passed. These are local WPF renders, not physical keyboard/screen-reader/DPI or RDP testing.
- A separate `gpt-5.6-sol` reviewer inspected gate accounting, limit-change signaling, cancellation ownership, settings startup/persistence, UI validation and the tests. No substantive defect remained; the reviewer did not duplicate the parent's test run.
- Application version remains **1.20.2**; this follow-up is recorded under **Unreleased** per the repository's version-on-release rule. The next normal publish from this source includes the setting and a fresh build/commit identity.
- No production host operation, live SMB throughput measurement or deployed restart was performed. Existing staging jobs should finish before replacing the running application, as already advised for this incident.
