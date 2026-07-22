# Vivre docs — index

The human-facing map of this folder ([CLAUDE.md](../CLAUDE.md) already routes the AI). One line and a
"read when…" cue per doc. **Case files are point-in-time records — never edited after the fact**; the
live status of anything they raise is tracked in the backlog.

## Living reference (kept current)

- **[vivre-backlog.md](vivre-backlog.md)** — the running tracker: open/parked/done work in recommended
  do-next order, with commit hashes. *Read when: deciding what to build next, or checking whether a
  finding was already fixed.*
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

## Archive (closed records — kept for provenance, not routing)

- **[archive/vivre-audit-findings.md](archive/vivre-audit-findings.md)** — the 2026-07-01 five-lens
  audit record (26 agents, 20 confirmed findings); every finding closed or tracked live in the
  backlog. *Read when: checking a finding's origin.*
- **[archive/vivre-backlog-done-archive.md](archive/vivre-backlog-done-archive.md)** — the backlog's
  DONE history, pre-1.16.0 era (1.15.x and earlier), moved verbatim. *Read when: tracing an old fix's
  commit or rationale.*
