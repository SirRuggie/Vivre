# Hunting a frozen UI thread — the playbook

> **This doc is portable.** It was written from Vivre, but almost nothing in it is Vivre-specific.
> Use it on any WPF / WinForms / desktop app that hangs, stutters, or "feels slow" and you don't
> know why.
>
> **Three independent hunts in this codebase had the same disease and the same cure.** Each burned
> hours or days on confident, wrong theories. Each was cracked — and could *only* have been cracked
> — by an instrument. This exists so the fourth one takes an hour instead of a week.

---

## The law

**Your theory is wrong until an instrument says otherwise. Especially the one you're certain about.**

The track record in this repo:

| Hunt | Confident theories disproven | What actually cracked it |
|---|---|---|
| **Cold-start freeze** (1.14.2) | **6** | A background-thread thread-pool watchdog |
| **RDP drag freeze** (1.15 arc) | **2** | A background-thread UI-liveness watchdog |
| **WUG "module not installed"** | a full session's worth | Reproducing the *exact bytes* the real launcher writes |

Eight-plus theories. **Zero** cracked by reasoning. **Three of three** cracked by measurement.

This is not a knock on anyone's reasoning. These bugs live in the gap between what the code says and
what the runtime does — a gap you cannot read your way across.

---

## The real enemy: instruments that lie

Every one of these felt like evidence. Every one was wrong. Catalogue, growing:

| # | The instrument | What it *actually* measured | What we read it as |
|---|---|---|---|
| 1 | `Control.DeviceDpi` | the process-wide **cached** system DPI | the DPI **of this window** |
| 2 | `refit=OK` | the call **didn't throw** | the resize **applied** |
| 3 | `fb=` on the diag strip | the **latest computed** request | what `Connect()` actually **passed** |
| 4 | "no sleep/wait/lock anywhere" | the code never **explicitly** blocks | the UI thread **isn't blocked** |
| 5 | `comCalls=0` during the freeze | **our** COM calls into the control | the control **was idle** |
| 6 | A PS harness writing the test script with `Set-Content -Encoding UTF8` | a file **the harness wrote** (with a BOM) | the file **the app writes** (no BOM) |
| 7 | A log-summary heuristic counting "slow COM calls" | **every** slow call in the file, including session-open | slow calls **during the block** |
| 8 | The harvester's line filter | only the `[RDP xxx]` **families listed in its regex** when it was written | **all** instrument lines in the window | 

### They all have the same shape

**Each one measured something *adjacent* to the thing we cared about, and we read it as the thing itself.**

So the test, every time you look at a number:

> **What does this literally measure? Is that the same as what I want to know?**

If there's a gap — however small — **that gap is exactly where the bug is hiding.** Every number on a
diagnostic must say what it **is**, not what you assume it means. Name fields for the literal
quantity measured (`physBtn` vs `wpfBtn`, never a single `button`) — because **their disagreement is
often the entire signal**.

---

## Instrument design — what to build, and why each piece

This is the reusable engineering. It took two hunts to converge on. Rebuild it in ~30 minutes for
any WPF/WinForms app.

**Reference implementation: `git show instrument/ui-freeze-watchdog` (tag).** Throwaway by design —
tagged, then stripped from the tree (strip commit `b374041`) — recover it via the tag.

### 1. UI-thread liveness — **the discriminator**, and the one non-negotiable trick

A `DispatcherTimer` on the UI thread does nothing but stamp a timestamp every 250 ms.
**A separate BACKGROUND timer samples that timestamp and does the logging.**

> **This is the whole trick.** A logger that runs on the UI thread **goes silent during the exact
> block it exists to measure.** It cannot see its own blindness. The watchdog *must* live on another
> thread. Both cracked hunts turned on this and nothing else.

```
[uithread] gapMs=<n> modal=<0|1> physBtn=<0|1> wpfBtn=<0|1> captured=<0|1>
[uithread] recovered blockedMs=<n> calls=<n> callMsTotal=<n>
```

Emit **only** on anomaly (`gapMs > 500`). `gapMs` large = the UI thread is genuinely blocked. That
single line separates "the app is dead" from "the app is alive but the input is confused" — which are
different bugs with different fixes, and which a human under stress **cannot reliably tell apart in
the moment.** Don't ask them to. Measure it.

### 2. Physical vs framework input state — three fields, never collapsed

| Field | API | Sees |
|---|---|---|
| `physBtn` | `GetAsyncKeyState` | **physical hardware.** Cannot be corrupted by a lost message. |
| `wpfBtn` | WPF `Mouse` | **message-derived.** Blind inside a foreign HWND and inside the modal resize loop. |
| `captured` | Win32 `GetCapture()` | **any** capture — WPF, WinForms, or a hosted ActiveX control. |

- **`physBtn=0` + `captured≠0`** (debounced 2 samples) = **stuck capture / lost button-up.** Definitive.
- **A framework mouse-state check would have been structurally unable to fire** for a button held
  inside a hosted control or during a border drag. We nearly shipped exactly that detector. It would
  have come back silent and we'd have "ruled out" a live theory.
- `GetCapture()` is **thread-specific** — sample it on the UI tick. From a background thread it always
  returns NULL.

### 3. Modal-loop markers

Hook `WM_ENTERSIZEMOVE` (0x0231) / `WM_EXITSIZEMOVE` (0x0232) on the window's `HwndSource`. **Windows
runs its own inner message pump between these two.** It is the highest-exposure window in the app, and
it is invisible from managed code unless you hook it.

```
[modal] ENTER
[modal] EXIT durMs=<n> calls=<n> callMsTotal=<n>
```

⚠ **A `GridSplitter` drag raises NO modal messages.** It uses WPF mouse capture instead. A
modal-loop-only fix silently misses it. **This is why the physical-button condition is mandatory, not
belt-and-braces.**

### 4. Per-call timing — report from `finally`

Stopwatch every synchronous call into the suspect component. **Report from `finally`, so a slow call
that *throws* is still captured.** Emit only above a threshold (50 ms).

### 5. Aggregate counters over the **block window**, not just the modal window

Death by a thousand cuts is invisible otherwise: 200 calls × 10 ms = 2 s of dead UI, and **not one of
them crosses a 50 ms threshold.** Snapshot monotonic counters at block onset; report the delta on the
recovery line.

### Hard constraints (all learned the hard way)

- **INFO-level real log lines. NEVER `Debug.WriteLine`.** You test a **Release** build; Debug lines
  compile out and you get a silent, confident nothing. *This has bitten this codebase twice* — it's a
  closed audit finding in its own right (settings-save failures were invisible in Release for exactly
  this reason).
- **Threshold-gate everything.** A human has to read and paste this. One freeze = 10–30 lines, not
  10,000.
- **Verify the log sink writes through while the UI is dead.** If it queues onto the UI thread, your
  background watchdog is useless.
- **Mark it THROWAWAY at every insertion point.** Then **tag the commit** before stripping it, so the
  next person gets it for free.

---

## The protocol — this is what actually cracked it

The instrument is necessary. The protocol is what makes it **conclusive** instead of just noisy.

### 1. Ask the cheapest question FIRST: **does the previous release do it?**

**We asked this eighth. It should have been first.** The PM asserted "master has the identical hole
and never froze — inherited, not a regression," and it was believed for two rounds. **Nobody had run
master.** When we finally did: master was clean. It *was* a regression, and the search space collapsed
instantly to "what did we change?" — three things.

> **RULE: never accept "it's pre-existing" from anyone, including yourself, without running the
> previous release.** It costs two minutes and can end the hunt.

### 2. Bisect with **free manual tests** before writing a line of code

The ladder that solved the RDP freeze, in about five minutes and zero builds:

| Test | Result | Ruled out |
|---|---|---|
| Same gesture with the feature **not visible** (other tab) | no freeze | it's not the engine — it needs the control **rendering** |
| Same gesture on a **different screen** with no such control | no freeze | it's not the shell / not WPF generally |
| Same gesture on the **previous release** | no freeze | **it's ours, and it's new** |

Each of these is a yes/no a human can answer without a build. Exhaust them **before** you instrument.

### 3. **Commit to a prediction before the run. In writing.**

State what you expect the numbers to look like, **and what result would falsify you** — *before* the
operator runs it. This is not ceremony. It is the only thing that stops a wrong theory from surviving
by quietly reinterpreting the data afterward. (Our PM predicted a clean control run and pre-agreed what
a noisy one would mean. When the data came back, nobody could move the goalposts.)

### 4. **Run a CONTROL first. The delta is the bug.**

Absolute numbers discriminate nothing. "Is 12 seconds bad?" is unanswerable until you know what a
*healthy* version of the same gesture looks like.

- **Run A — control:** the same gesture, in the healthy configuration. Don't try to break it.
- **Run B — repro:** provoke the failure once.

The cold-start hunt was cracked by a *before/after* poolwatch comparison, never by a single number.
The RDP hunt's control run is what proved the clicking was irrelevant (**A→B delta: 16 ms**) — which
meant the entire premise of the acceptance test's item 5 was wrong.

### 5. **Fix the read of every outcome in advance.**

Write the table *before* the data arrives:

> - large gap + a slow call ≥ the gap → **(a) confirmed**, and the line **names** the culprit
> - large gap + no slow call → (a) is real but **it isn't our calls** — say so plainly, don't force it
> - stuck-capture fires → **(b) confirmed**, definitive
> - neither, yet it froze → **both theories are dead.** Say exactly that.

**A disproven theory is a win, not a failure.** If every branch of the table is a usable answer, you
cannot waste the run — and nobody can steer the result.

### 6. Then, and only then, fix it.

**Do not bisect *which* of your changes made it expensive if the fix is correct regardless.** Suppressing
a 60-per-second repaint during a border drag is correct behavior whether or not it's the trigger. Fix
first; bisect only if the fix fails.

---

## Case files

| Hunt | Doc | Root cause | Fix |
|---|---|---|---|
| **Cold-start freeze** | `cold-start-freeze-and-threadpool-findings.md` | Serial thread-pool **worker injection** on a 2-core box (~1 thread/500 ms) serialized ~28 blocking WinRM opens | `ThreadPool.SetMinThreads(64, 64)` — **one line.** Do not delete it. |
| **RDP drag freeze** | `vivre-rdp-scaling-and-fcm-findings.md` | The **visible** OCX repainting a 1.5×-zoomed framebuffer on **every** drag tick, synchronously, inside Windows' modal resize loop | Defer applying the host size to the control until the drag settles (`WM_EXITSIZEMOVE` **or** physical button release **or** a settle poll) |
| **WUG false "module not installed"** | `key-file-path-map.md` ▸ *two gotchas* | BOM-less UTF-8 `.ps1` → PS 5.1 reads it as ANSI → **the script never parses** | Write the temp script **UTF-8 with BOM** (`WritePs51ScriptAsync`) |

**Two recurring shapes worth internalizing:**

- **The fix is almost always small.** One line. One guard. One encoding flag. The *hunt* is the work.
- **The bug is usually in the gap between two layers** — the pool and your `await`, the control and its
  host, the file you wrote and the file the app writes. Nobody owns that gap, so nobody instruments it.

---

## Tools

| Tool | Where | What it does |
|---|---|---|
| **The instrument** | `git show instrument/ui-freeze-watchdog` | Background watchdog, input triad, modal hooks, per-call timing. Rebuild from this. |
| **The harvester** | `tools/Get-VivreFreezeLog.ps1` | `-Mark` before a run, no-args after → pulls just that run's lines, summarizes, copies to clipboard. |

**A warning about the harvester's summary line:** it is *itself* lying instrument #7 — its "slow COM
call" verdict counts every slow call in the window, including harmless session-open ones. **Read
`max gapMs` and the stuck-capture flag. Ignore the verdict.** Left in deliberately, as a reminder that
even the tool you build to catch lies can tell one.

**And #8, which cost a full round of blank results in the disconnect hunt:** the harvester's line
FILTER only matches the `[RDP xxx]` families it knew when it was written — a new instrument's lines
are silently dropped until the regex is updated. **Zero matches from a fresh run means CHECK THE
FILTER before concluding "no data"**, and adding a new line family to an instrument means updating
the harvester's regex in the same commit.

---

## Cardinal / safety note

None of this touches a reboot path. Instrumentation is read-and-time only: **it must add no writes, no
new senders, no new state.** An instrument that changes the system is not an instrument.
`Win32Shutdown` stays only in `DcomRebootTrigger.cs`. Re-grep on any merge, as always.
