# The reboot/monitor arc — case file

> **Point-in-time record, never edited after the fact.** Written 2026-07-28 against `master` @ `f10c080`
> (clean tree, release **1.17.1**). It captures what was proven, what was refuted, and what was still
> unknown *on that date*. Later work does not get retro-fitted into it — if the code changes, this file
> stays as written and the new truth goes in `CHANGELOG.md` / `docs/vivre-backlog.md` / a new case file.
>
> **Status at freeze: BUILT AND FIELD-VERIFIED.** Both defects below are fixed and shipped in 1.17.0 /
> 1.17.1. Every line-number citation is against the commit that introduced it, not against `f10c080`.
>
> **Scope:** this file is about the MONITOR and the post-reboot verify arc. The DCOM 1191 →
> forced-escalation work is a separate arc with its own case file —
> **[docs/dcom-1191-reboot-fallback-findings.md](dcom-1191-reboot-fallback-findings.md)** — and is not
> repeated here. Sections F and G below touch SentinelOne only where this investigation learned something
> the 1191 file does not record.

---

## Why this file exists

The tab-count theory consumed **three full investigation rounds** before a field reproduction refuted it,
and two further hypotheses were formally disproven inside those rounds. None of that was written down
anywhere. The backlog records what is OPEN; this records what is already KNOWN, so the next pass starts
from proven ground instead of re-chasing dead ends.

---

## A. The first defect and its root cause

The post-reboot verify arc ran **inline on the monitor's critical path with no deadline**.

`ProbeRebootPendingAsync` armed a 120-second linked `CancellationTokenSource`, then called
`ReportPostRebootOutcomeAsync` passing the **RAW monitor token** rather than the linked one. Cancelling a
linked source never cancels its parent, so the deadline covered nothing. Inside, the arc looped up to
`PostRebootRescanAttempts` uncapped WUA rescans; `ScanAttemptTimeoutSeconds` was applied at exactly one
other site and not there.

Because the arc was awaited inside `MonitorRowAsync`, which runs under `Task.WhenAll` for the whole tab,
and `MonitorLoopAsync` awaits that whole pass before its next delay, **one armed row could stall an entire
tab's monitor pass indefinitely** — every row on the tab, not just the rebooted one.

**Fixed** by bounding the arc (`VerifyArcTimeout`, 5 minutes, sized explicitly rather than inheriting the
probe's 120 s) and then **detaching** it from the pass entirely — started, never awaited. Fire-and-forget
preserves UI-thread affinity **without** `Task.Run` and **without** `ConfigureAwait(false)`: an un-awaited
async method started on the UI thread still captures `SynchronizationContext.Current` at each of *its own*
awaits, and a callee's internal `ConfigureAwait(false)` cannot strip the caller's context. A three-leg
freshness gate (generation / row-claim / one-arc-per-host) keeps the detached arc from overwriting newer
information.

---

## B. Field reproduction — the evidence that settled it

**2026-07-28 17:26.** ONE Health tab. Seven rows. Host in no other tab. Two rows force-rebooted.

```
17:26:37  NYC-FP1     Forced reboot (shutdown /r /f /t 5)
17:26:44  Export-VFP  Forced reboot (shutdown /r /f /t 5)
17:29:36  NYC-FP1     NYC-FP1: couldn't rescan — re-check   [Health · Test Servers #1]
```

Between **17:26:44 and 17:29:36 the activity log emitted ZERO lines** — 2 m 52 s, with
`MonitorIntervalSeconds = 20`, so roughly 8–9 passes should have run. No "Went offline" for either box
despite both being down by ping for that whole window. All seven rows froze; every Online pill stayed green
with two machines provably down.

**THE SILENCE WAS THE PROOF.** The monitor was not failing to *detect* the outage — it was **not running**.
A detection failure would have produced passes that logged nothing about those two rows while still
servicing the other five. Total silence across the tab is only consistent with the pass itself being
stalled, which is exactly what an unbounded inline await does.

Every line in the window carried exactly one tab tag, independently confirming a single instance.

---

## C. Three hypotheses formally refuted — do not re-chase these

### 1. Tab count / multiple instances — REFUTED

Consumed **three investigation rounds**. Refuted by the single-tab reproduction in section B: one tab, host
in no other tab, still froze.

What IS true and was proven along the way (keep it, it is correct — it just is not the cause): each tab
builds its own `Computer` instance and runs its own instance-level monitor loop; only the throttles are
static, and every other piece of monitor state is per-instance; so **two grids over one host are two
independent state machines that never reconcile and can legitimately disagree indefinitely.**

### 2. Throttle starvation (`_monitorThrottle`) — REFUTED

The throttle is acquired around the reachability probe and **released before** the reboot block, so it
never holds the row across the expensive work. It is also shared: during the same outage, other tabs
completed their probes normally on the same 32 slots. And it is symmetric — it offers no mechanism that
would starve a Health instance twice while never touching a Patching one.

### 3. `HostWinRmGate` contention — REFUTED on three independent grounds

- **The reachability probe never touches the gate at all.** `HostPinger` is pure ICMP and `WmiHostProbe`
  builds its own DCOM `CimSession`; neither holds an `IPowerShellHost`. The gate therefore cannot delay the
  `IsOnline` flip — its only reachable effect is stalling a pass.
- **Background work can never starve operator-class work.** A background acquire takes a background slot
  (cap 2) *then* a total slot (cap 4), so background can hold at most 2 of 4 and at least 2 total slots are
  always reachable by operator-priority callers.
- **The gate is symmetric.** One `PerHost` per key, two plain semaphores, no tab identity, no mode, no
  priority beyond background-vs-operator — nothing that could select a consistent loser.

Additionally: admission to the reboot probe requires `online`, so once a box stops answering, contention on
that host drops to **zero** for the entire outage — precisely the interval the hypothesis needed squeezed.

---

## D. The second defect — the monitor's detection floor

`MonitorIntervalSeconds = 20` and `OfflineConfirmThreshold = 2`: a previously-online box needs **two
consecutive** failed probes before `IsOnline` flips. Two consecutive probes span at least one full
interval, so the **reliable detection floor is ≥ 40 seconds**. A machine that reboots faster is down and
back *between* two probes and is structurally invisible — a 20-second outage can contain at most one probe
instant, and that single failure is absorbed by the threshold.

Everything downstream of the offline→online transition is gated on a transition that never happens, so the
row sticks permanently on "Reboot forced — going down", `Last reboot` never updates, and **it does not
self-correct**: the verify arc clears its own marker and the recheck budget, after which a Health tab
admits the row to nothing.

**Field evidence.** Export-VFP (a VM) rebooted in ~20 seconds and stuck **twice** on 2026-07-28.
NYC-FP1 (physical, ~90–108 s down) tracked correctly every time. The operator's fleet is heavily
virtualised, so **this is the common case for VMs, not an edge case.**

**Fixed** with a fast boot-time watch: capture an uptime baseline at dispatch (before the box is told to go
down), poll that one row every 5 seconds for a 2-minute window, then hand back to the monitor — which
handles slower reboots correctly and needs no help. On proof the row is written exactly as the transition
would have written it; if nothing can be proven by the wave's forced go-offline window, the row lands
**Unverified** rather than stuck.

**Verified 2026-07-28 21:54**, one Health tab, two rows force-rebooted in one click:

```
Export-VFP  21:54:34  Forced reboot
            21:55:41  Back online 21:55   [Health · Test Servers #1]   <- NO "Went offline" line
            21:56:17  Back online · up to date
NYC-FP1     21:55:53  Went offline — TimedOut
            21:57:42  Back online 21:57 (down 1m 48s)
```

The VM resolved **with no drop ever observed** — the new path — while the physical machine resolved the old
way in the same run. Both mechanisms correct simultaneously; the new one did not break the existing one.

---

## E. The clock-immunity requirement — LOAD-BEARING, do not lose this

The first cut of the watch reused `ReadyConfirmation`, which compares **two raw `LastBootUpTime`
readings**. That is clock-**dependent**: `LastBootUpTime` is derived from the target's wall clock, so an NTP
correction on a drifted VM — or a manual set, or a forward DST step — moves it without any reboot and fakes
a reboot on a machine that never restarted.

In the wave that mattered less, because the wave also watches for the drop. Here **the boot time is the
SOLE evidence and the box is by construction continuously reachable**, so a false positive would assert a
reboot that never happened — the exact false-success class this whole arc exists to prevent.

Replaced with **uptime collapse** (`UptimeRebootProof`), the clock-immune rule already in the repo: both
values come from the target in one query, so a clock step moves both and their difference is unchanged. If
the box never rebooted, `current.Uptime ≈ baseline.Uptime + elapsed`; a real reboot collapses it.

> **Any future work here must not regress to a raw boot-time comparison.** A test asserts the clock-step
> case specifically; a mutation reverting to the raw form is killed by it.

---

## F. The BatchPatch comparison — UNRESOLVED

This answers a question the operator had carried for months, but only partly. **Recorded as unresolved,
not as a finding.**

Windows event **1074** on the target names the initiating process. Observed on NYC-FP1:

| Initiator in 1074 | Meaning |
|---|---|
| `wmiprvse.exe` (local) | WMI/DCOM reboot — what BatchPatch uses, and Vivre's primary path |
| `wininit.exe` (10.70.120.25) | `shutdown /r /m` from the console |
| `wininit.exe` (local) | Vivre's forced reboot run on the box |

BatchPatch **does** create remote services (`BatchPatchExeSvc-<source>`, five installs observed) and those
did **not** alert in SentinelOne, while Vivre's `Vivre_Reboot_*` service **did** — same host, days apart.

**The discriminator was never isolated.** Candidates, none tested: the `cmd.exe` image versus a
purpose-built binary; code signing; a random-GUID service name versus a stable one. Do not treat "BatchPatch
does it too, so services are safe" as established — it is not.

---

## G. SentinelOne forensics quirks — these cost real time

- **S1 raises TWO threat records per fallback reboot.** A raw threat count roughly doubles the real number
  of events. Count events, not rows.
- **The threat list shows REPORTED time; the drill-down shows IDENTIFIED time**, and the lag between them is
  minutes. **Always correlate on Identified** — using Reported will misalign against Vivre's own log.
- **The uppercase `/C` in S1's CMD column is a display artifact** — only the first token is capitalised. It
  does **not** indicate a build/source mismatch. Time was spent chasing that.

---

## H. Method notes worth carrying

- **Absence of log lines is evidence.** The 2 m 52 s silence in section B proved more than any line would
  have. When a subsystem is suspected of not running, look for what it did *not* say.
- **Check WHEN a log line was introduced before treating its absence as a finding.** Two conclusions in this
  arc were built on lines that did not exist in the running build and had to be retracted. `git log -S` on
  the string, against the deployed commit, before drawing anything from a missing line.
- **Log lines carried the host but not the tab**, which left three investigations unable to attribute
  anything when one machine was open in several tabs. Per-tab tagging (`[Health · Test Servers #1]`) was
  added for exactly this reason — it is a diagnostic affordance, not decoration.
- **A worker's finding is a starting point.** Several agent conclusions in this arc were confidently wrong
  and were caught only by re-reading the cited code; two red-team passes found real defects in fixes that
  had already built green and passed the full suite.
