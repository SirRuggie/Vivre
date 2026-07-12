# Vivre — cold-start UI freeze & .NET thread-pool findings

> **Project knowledge note.** The full record of the cold-start UI freeze hunt: the symptom, the
> measurement discipline that cracked it, the **six theories disproven by instrumentation** before
> the real cause was found, the load-bearing one-line fix (and why it must not be deleted), and the
> things future work must NOT re-chase. Modeled on `vivre-rdp-scaling-and-fcm-findings.md`. Shipped
> in **1.14.2**. Nothing here touches the reboot path — `Win32Shutdown` stays only in
> `DcomRebootTrigger.cs`.
>
> **General method:** the reusable instrument + protocol distilled from this hunt (and the RDP drag
> freeze) live in [freeze-hunting-playbook.md](freeze-hunting-playbook.md) — this doc is a case file.

---

## TL;DR / status

| Item | Status |
|---|---|
| **Cold-start UI freeze on a large list** (~319 boxes) | **SOLVED — shipped in 1.14.2** (three-part fix). |
| **Root cause** | **Serial thread-pool worker injection on a low-core box** — NOT pool exhaustion, NOT the grid, NOT the sweep. |

**The symptom:** opening a large machine list (~319 machines) as the first action after launch froze
the UI for **7–38 seconds**. The freeze length **tracked the slowest WinRM connect** — a slow or
Kerberos-broken box stretched it. Grid blank, no render frames, UI thread unresponsive, then it
recovered all at once.

**The fix (one line, load-bearing):** `ThreadPool.SetMinThreads(64, 64)` at the top of
`App.OnStartup` (commit `ea70c2f`), plus two supporting fixes (`19f766b`, `0bfd362`). **Do not delete
the `SetMinThreads` line** — see *Don't delete / don't re-chase* below.

**The meta-lesson (the reason this doc exists):** **measure, don't guess.** Six confident theories
were each disproven by real millisecond instrumentation before the true cause surfaced. A perf fix
here requires throwaway-Stopwatch numbers proving the dominant cost *before* building — every time we
reasoned from theory instead, we were wrong.

---

## The three-part fix (all in 1.14.2)

| Commit | What it fixes | Mechanism |
|---|---|---|
| `19f766b` | Grid renders **blank until you resize** the window | A debounced (~150 ms) re-layout (`InvalidateMeasure` + `InvalidateArrange`) fires after the view's `SizeChanged` / `Computers.CollectionChanged` settles. The virtualizing DataGrid was first laid out before it had a real height, realized **zero** rows, arranged to zero, and nothing re-triggered layout once the size settled. Replaces three earlier narrower re-measure attempts. Confined to `WorkspaceView.xaml.cs`. **A real but SEPARATE bug** — not the post-paint freeze. |
| `0bfd362` | Shell sits **blank ~10s** on cold start with auto-check-on-load | The 319-machine vitals sweep was kicked off **synchronously in `AddComputers`**, running its prologue on the UI thread before the just-loaded grid could paint. Now the auto-check kickoff (sweep + custom-column fill) is deferred to `DispatcherPriority.Background`, so rows paint first (~1s) and vitals begin a render cycle later. Confined to `WorkspaceViewModel.AddComputers`. |
| `ea70c2f` | **The headline freeze** — 7–38s UI hard-block | `ThreadPool.SetMinThreads(64, 64)` in `App.OnStartup`. Gives all ~28 already-bounded WinRM opens a worker **immediately** so they run in parallel instead of trickling out ~2/second. Collapses the freeze. **One line; off the hot path; touches no grid/sweep/live-filter code.** |

---

## The real cause (proven by instrumentation, not theorized)

**It is NOT thread-pool exhaustion.** A pool-state watchdog (sampling on a background thread, so it
keeps logging even while the UI is frozen) showed `availWorkers ≈ 32743 of 32767` for the **entire**
freeze. There are tens of thousands of free worker slots throughout. Workers are not the bottleneck.

**It is serial worker injection.** The per-host WinRM open is `Task.Run(() => runspace.Open())`
(`PSRunspaceHost`) — a **synchronous blocking call** that holds its thread for the full connect (7s,
27s, 37s on slow / Kerberos-broken boxes). The chain runs `connect → invoke` fully off-thread with
`ConfigureAwait(false)`, and only the cheap `ApplyVitals` property-write marshals back to the UI
thread — i.e. the chain is **structurally correct**. The problem is below it, in the runtime:

- The .NET thread pool's **default minimum worker count = the logical-processor count**. On the
  deployment/test box that is **2**.
- Above the minimum, the pool **injects new threads slowly — about one every ~500 ms**.
- The fleet sweep queues ~28 blocking opens at once (bounded by `_remoteSweepThrottle`, ≈
  `MaxConcurrentScans − reserved`). Each grabs a worker and **blocks** inside `Open()`.
- So the 28 opens execute **serialized at the injection cadence (~2/second)**, taking ~38s to all
  start — and the `await` continuations that drive the sweep forward **can't get a worker** until a
  slow `Open()` finishes and frees one.

The UI thread is therefore parked for the **duration of the serial-injection window**, which is
gated by the slowest connect — exactly the observed coupling (**freeze length = slowest connect**).

**The decisive evidence (poolwatch, two runs):**

| | Before fix (min=2) | After fix (min=64) |
|---|---|---|
| `inOpen` ramp | `3 → 7 → 11 → 23` over **~38s** | `0 → 26` in **~1s** |
| Slowest `open(off-thread)`, same fleet | **37,841 ms** | **2,575 ms** (no longer queued behind 25 others) |
| Worst UI dispatcher gap | `7,309 ms` / `19,332 ms` (`priority=Background`) | one-time `1,224 ms` at startup, then all **sub-250 ms** |
| `SweepNarration` 1s timer through the connect window | **19s silent gap** (UI dead) | ticks every ~1s, no gap |

The headline log line that named it: a single multi-second
`DISPATCHER OP … priority=Background target=System.Threading.Tasks.SynchronizationContextAwaitTaskContinuation…`
— an awaited Task resuming on the UI thread — ending exactly when the slow opens completed.

---

## The six disproven theories — DO NOT re-chase these

Each was measured and ruled out by magnitude. They are written down so nobody re-derives them the
hard way.

1. **Grid stale-measure / zero-height re-layout.** *(Was a REAL bug — fixed in `19f766b` — but a
   SEPARATE one, not the post-paint freeze.)* Six grid sub-theories were also disproven first:
   CollectionView re-filter cost, Auto-columns, ItemsPanel swap (proven *harmful*), infinite-measure,
   bare-Grid-unbounded, and a MaxHeight band-aid.
2. **SDK cold-JIT** (runspace/WSMan type construction). Measured **0–3 ms** (`ctorWSMan=0ms`,
   `createRunspace=3ms`). A pre-warm was built and **reverted** — it fixed nothing, and the freeze
   also hit on a warm second tab, which a one-time JIT cannot explain.
3. **Vitals sweep prologue** (`KickAutoCheck` / `RunSweepAsync`). Entire kickoff ≈ **137 ms**
   (eligibility 0ms, BeginOperation ~5ms, WhenAll-enumeration ~14ms, custom-columns ~112ms). Cheap.
4. **Monitor kickoff / per-row continuations.** Enumeration ~50 ms; per-row sync continuation ~0.1 ms;
   ~35–42 ms total across all 319 rows. Cleared.
5. **List-load** (`AddComputers` row-add cascade). ~**65–83 ms** (parse 33ms, construction 3ms,
   `Computers.Add` cascade ~65ms, monitor sync-return ~50ms). Cleared.
6. **Auto-width DataGrid column measure.** **Quantified at 120–180 ms per pass** — a real, minor
   contributor that fires repeatedly, but **not** the freeze. (This is the backlog's long-standing
   `Width="Auto"` suspicion — now measured.)

A `ConfigureAwait(false)`-everywhere "fix" and a suspend-live-filtering "fix" were both correctly
**not** built — measurement showed neither addressed the cause.

---

## Don't delete / don't re-chase

- **The `ThreadPool.SetMinThreads(64, 64)` line in `App.OnStartup` is load-bearing.** Removing it
  reintroduces the serial-injection freeze on a **low-core machine** (it won't reproduce on a
  many-core dev box, which is exactly the trap — the deployment target is 2 cores). It does **NOT**
  widen any concurrency throttle: the sweep still runs ≤28 opens via `_remoteSweepThrottle`,
  `_monitorThrottle` is still 32, `HostWinRmGate` is still 4/host. The line only lets the
  **already-permitted** opens get their worker threads at once instead of one every ~500 ms. The
  throttles cap *how many operations run*; `SetMinThreads` changes *how fast the pool hands threads to
  already-permitted work* — orthogonal.
- **Don't re-chase the six theories above.** In particular, the Auto-width column measure (120–180 ms)
  is real but minor, the row-add cascade is small (~65–83 ms), and the runspace/JIT construction is
  ~0 ms. None of them is the freeze.
- **`SetMaxThreads` is the wrong knob** (max is already 32767). The lever is the **MIN** floor.

---

## Why the "more correct" fix wasn't taken

A genuinely-async open — `Runspace.OpenAsync()` wrapped in a `TaskCompletionSource` (it exists in the
SDK but is event-based, not awaitable) — would remove thread-blocking entirely and is the textbook
fix. It was **rejected for now** because it's a **rewrite of the load-bearing WinRM choke point**: the
abandon-path disposal, the `MaxConnectionRetryCount=0` NRE-avoidance, and the Kerberos `0x80090322`
detection all hang off the current `Task.Run(Open)` + `WaitAsync` shape (see
`docs/windows-patching-lane.md` ▸ Kerberos-broken hosts). The one-line `SetMinThreads` is the minimal,
low-risk lever that stays out of that choke point. **Reserve the async-open rewrite for later, only if
it ever proves worth the churn.**

## One honest open thread

We proved the freeze **duration** equals the serial-injection window, and overlapping the opens
collapses that window — but we never fully pinned *why the UI thread was hard-blocked (one long
synchronous-looking op) rather than merely idle-waiting* for the delayed results. Overlapping the
opens fixed it either way, and the acceptance run confirmed the freeze is gone. A single ~**1.2 s**
dispatcher blip remains at the very first burst of connect-completions (one-time, at startup, not a
freeze) — **not chased**. If it ever needs chasing, the instrument that would name it is a UI-thread
managed-stack capture (needs ClrMD; a real build dependency — don't add it for a throwaway probe
unless the simpler counters come back ambiguous, as they did not here).

---

## How to reproduce / verify (cold-start acceptance)

Cold start → open a large list (the ~319-box "All Servers"). Pass criteria:
1. In the pool-state samples, the in-progress-open count **jumps to ~24–28 within ~1s** (not a slow
   climb over ~38s).
2. **No multi-second** `DISPATCHER OP` / `RENDER-FRAME GAP` in the post-paint window (a few-hundred-ms
   gap for real render work is fine).
3. The UI stays live — the 1s narration/heartbeat keeps ticking through the connect window.
4. The slowest single connect still takes however long that one box needs — **expected and fine**; the
   point is the UI is responsive during it and rows fill in as each box reports.

---

## Cardinal / safety note

None of this work touches a reboot path — it is startup-timing + thread-pool configuration only.
`Win32Shutdown` stays only in `DcomRebootTrigger.cs`, behind the operator-confirmed gate. Re-grep on
any merge as usual.
