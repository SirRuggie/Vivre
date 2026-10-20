# Vivre docs — index

The human-facing map of this folder ([CLAUDE.md](../CLAUDE.md) already routes the AI). One line and a
"read when…" cue per doc. **Case files are point-in-time records — never edited after the fact**; the
live status of anything they raise is tracked in the backlog.

## Living reference (kept current)

- **[list-organization-validation.md](list-organization-validation.md)** — U-58/U-59 list organization/save-folder flow included in 1.20.1, review corrections, focused checks and native-validation limits.

- **[release-1.20.1-validation.md](release-1.20.1-validation.md)** — current release metadata, simplified list organization/save-folder changes, reused regression evidence and packaging limits.

- **[release-1.20.0-validation.md](release-1.20.0-validation.md)** — historical 1.20.0 release metadata, list/People/UI changes and validation limits.

- **[release-1.19.2-validation.md](release-1.19.2-validation.md)** — historical U-41 and saved-list-name acceptance ledger and checks for 1.19.2.

- **[people-lookup-validation.md](people-lookup-validation.md)** — People lookup capabilities, independent review corrections, tests and live AD validation limits.

- **[ui-ux-u21-u22-validation.md](ui-ux-u21-u22-validation.md)** — layout/accessibility fixes, local test evidence, and remaining native Windows validation.

- **[vivre-ui-ux-backlog.md](vivre-ui-ux-backlog.md)** — living U-1–U-59 checklist, decisions, completion evidence, and strengths to preserve; U-45–U-57 included in 1.20.0 with local evidence and native-testing limits. *Read when: choosing or updating UI/UX work.*

- **[vivre-audit-implementation-2026-10-14.md](vivre-audit-implementation-2026-10-14.md)** — F26–F49 acceptance ledger and follow-ups, independent review corrections and exact local/live validation limits.

- **[../CHANGELOG.md](../CHANGELOG.md)** — current release **1.20.1**, with future work under **Unreleased**; see the UI/UX checklist for current local validation limits.

- **[release-1.17.3-validation.md](release-1.17.3-validation.md)** — historical 1.17.3 candidate validation: multiple-schedule fix and expanded banner checks; not validation of the current build.

- **[release-1.17.2-validation.md](release-1.17.2-validation.md)** — release acceptance ledger, package evidence, shared-drive checks and remaining pilot gaps.

- **[vivre-backlog.md](vivre-backlog.md)** — reconciled current validation gaps, explicit deferrals, implemented findings and legacy candidates requiring revalidation. *Read when: choosing the next investigation or checking whether an old finding was already fixed.*
- **[key-file-path-map.md](key-file-path-map.md)** — the load-bearing file locations and per-area
  rules, so new sessions don't re-derive them. *Read when: you need to know where something lives
  before touching it.*
- **[../CLAUDE.md](../CLAUDE.md)** — architecture + conventions: layout, cardinals, commit/release
  rules. *Read when: starting any work in this repo.*

Top-level (repo root): **[../README.md](../README.md)** — the human-facing overview + roadmap ·
**[../CHANGELOG.md](../CHANGELOG.md)** — user-facing changes, newest first (work-in-progress under Unreleased).

## Lane specs / how-to

- **[windows-patching-lane.md](windows-patching-lane.md)** — the Windows Update (WUA) lane deep-dive
  and its load-bearing reliability constraints. *Read when: touching update or remoting code.*
- **[2016-LCU-lane-spec.md](2016-LCU-lane-spec.md)** — canonical spec for the Server 2016 full-package
  DISM patching lane: root cause, scope, build requirements. *Read when: changing the 2016 patch lane.*
- **[2016-LCU-panel-spec.md](2016-LCU-panel-spec.md)** — the 2016 CU panel as-built record + standing
  maintenance constraints. *Read when: touching the 2016 panel UI or its flows.*
- **[freeze-hunting-playbook.md](freeze-hunting-playbook.md)** — the portable method for hunting a
  frozen/slow UI thread: instrument first, predict, control-run. *Read when: any hang/stutter/"it's
  slow" report — BEFORE theorizing.*

## Case files / findings (point-in-time, never edited)

- **[vivre-backlog-snapshot-2026-10-20.md](vivre-backlog-snapshot-2026-10-20.md)** — verbatim pre-reconciliation tracker, including older open/closed claims and decisions; consult the current backlog for superseding status, not this snapshot for instructions.

- **[ui-ux-full-spectrum-review-2026-10-20.md](ui-ux-full-spectrum-review-2026-10-20.md)** — U-45–U-57 screenshot/source-backed review of People and the full UI surface, operational interaction findings, research, proposed batches and native-study limits.

- **[ui-ux-follow-up-2026-10-19.md](ui-ux-follow-up-2026-10-19.md)** — U-28–U-37 source-backed usability findings, isolated reproductions, strengths and native-UI limitations.

- **[vivre-comprehensive-audit-2026-10-14.md](vivre-comprehensive-audit-2026-10-14.md)** — F26–F45 ranked operations, resource, UX, reuse and release findings; source evidence, refutations and validation limits. Frozen review; current implementation status is in the acceptance ledger.

- **[vivre-performance-implementation-2026-10-14.md](vivre-performance-implementation-2026-10-14.md)** — F13–F20 implementation, adversarial validation, measured changes and remaining operational limits. *Read when: checking the performance fixes and their actual test coverage.*

- **[vivre-performance-review-2026-10-14.md](vivre-performance-review-2026-10-14.md)** — F13–F20 performance findings, repeated local measurements and outstanding operational validation gaps. *Read when: prioritizing responsiveness or memory work; fixes remain recommendations.*

- **[cold-start-freeze-and-threadpool-findings.md](cold-start-freeze-and-threadpool-findings.md)** —
  the cold-start UI freeze hunt: six disproven theories, the thread-pool worker-injection cause, the
  load-bearing `ThreadPool.SetMinThreads` fix. *Read when: touching App.OnStartup, the sweep, or
  large-list load.*
- **[vivre-rdp-scaling-and-fcm-findings.md](vivre-rdp-scaling-and-fcm-findings.md)** — the embedded-RDP
  scaling / Failover Cluster Manager saga: the 100% session-scale pin, client-side ZoomLevel. *Read
  when: touching Cross-Domain RDP scaling.*
- **[2016-LCU-red-team-review.md](2016-LCU-red-team-review.md)** — the adversarial review of the 2016
  LCU design and the traps that bite on box #7 of 30. *Read when: changing the 2016 install/verify/
  reboot logic.*
- **[wug-state-check-findings.md](wug-state-check-findings.md)** — the WUG state-check cycle: the IP
  substring-match reclassification and the cold-start mass-unknown SSL chain (scriptblock callback →
  compiled delegate). *Read when: touching the WUG lane's resolver or SSL/connect path.*
- **[dcom-1191-reboot-fallback-findings.md](dcom-1191-reboot-fallback-findings.md)** — why the DCOM/WMI reboot
  call Vivre sends is refused when any session is logged on (1191), and why misreading that as a dead channel
  produced the SMB/SCM service SentinelOne scored as Lateral Movement. **PARTIALLY SUPERSEDED — see
  [windows-patching-lane.md](windows-patching-lane.md) ▸ "WHAT IS ACTUALLY REFUSED"**: the 1191 evidence stands,
  but field tests on 2026-07-29 showed the refusal belongs to the WMI primitive, not to Windows — `shutdown.exe
  /r` without `/f` is not refused, and a warned countdown completes on an occupied box. The case file is frozen
  and stays as written. *Read when: touching the reboot trigger, the DCOM/SMB channel choice, or the
  forced-window selection — then read the patching-lane passage for what has since been narrowed.*
- **[reboot-path-and-guardrail-findings.md](reboot-path-and-guardrail-findings.md)** — the thirteen 2026-07-28
  reboot-path / guardrail findings in full, citations re-verified against `99995b6`: the approve-vs-execute gap,
  three reboot paths shipping with no confirm, and the known-incomplete `Win32Shutdown` gate grep (one of five
  reboot-issuing sites). *Read when: touching a reboot path, a confirm dialog, or the cardinal gate grep — and
  BEFORE claiming a reboot path already confirms; use the current backlog's stable-ID reconciliation for later F02/F03/F38 closures.*
- **[reboot-monitor-arc-findings.md](reboot-monitor-arc-findings.md)** — the monitor/verify-arc arc: the
  unbounded inline verify arc that froze a whole tab, the ~40 s detection floor that made fast-rebooting VMs
  stick forever, THREE formally refuted hypotheses (tab count, throttle starvation, HostWinRmGate), and the
  clock-immunity requirement. *Read when: touching the monitor loop, the verify arc, or the fast reboot
  watch — and BEFORE re-investigating a stuck row, so you don't re-chase a refuted theory.*

## Archive (closed records — kept for provenance, not routing)

- **[archive/vivre-audit-findings.md](archive/vivre-audit-findings.md)** — the 2026-07-01 five-lens
  audit record (26 agents, 20 confirmed findings); every finding closed or tracked live in the
  backlog. *Read when: checking a finding's origin.*
- **[archive/vivre-backlog-done-archive.md](archive/vivre-backlog-done-archive.md)** — the backlog's
  DONE history, pre-1.16.0 era (1.15.x and earlier), moved verbatim. *Read when: tracing an old fix's
  commit or rationale.*
