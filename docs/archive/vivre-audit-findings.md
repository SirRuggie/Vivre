# Vivre — five-lens audit findings (2026-07-01)

> Point-in-time diagnostic audit run on master @ 1.14.3 (+ the status-bar dedup commit).
> Read-only: 5 lens workers + 1 adversarial verifier per finding (26 agents), then PM re-read every
> cited file/line. 21 raw findings → 20 confirmed, 1 refuted. NOTHING here is fixed — this is a
> triage backlog. Pull items into scoped fix handoffs over time; research-first on anything in the
> live-filtered grid / remoting-cache zones. Suite was 666 green at audit time.
>
> **Headline: NO CRITICALs.** No cardinal-rule reboot violation, and no live-filtered off-thread
> write — the reboot lens traced every reboot-capable path to an explicit operator gate, and the
> concurrency lens traced every write to all 12 live-filtered properties to the UI thread (the crash
> class is not reintroduced). The two HIGHs are both "operator sees a calm/green picture while
> something is wrong" — Vivre's core ethos.

## Recommended fix order (advisor triage)
1. HIGH-1 — dead install worker undetectable (WinRM lane) — highest value, SMB lane has the template.
2. HIGH-2 — one hung box freezes the whole monitor loop — well-scoped, vitals probe is the template.
3. MED — cancel-failed-but-chip-cleared — reboot-adjacent, relevant to just-shipped cancel work.
4. MED — post-reboot probe failure renders as green "up to date" — fold into post-reboot work.
5. The cheap silent-failure batch (SCCM false-green, settings-save, SMB-stop-launches-agent).
6. MEASURE the Details-window CollectionView leak before deciding (perf — do NOT fix on theory).
7. DECIDE (not reflex-fix): the install-nudge gate + the orphan Vivre_Reboot service LOW.
Everything else: recorded, act only if it recurs.

---

## HIGH

### HIGH-1 — Dead install worker is undetectable on the WinRM lane; the row says "still running" forever
- **Where:** WuaUpdateLane.cs:1004 (server tail loop), :242 (client watchdog), ProgressWriter.cs:100
- **What:** Once the agent writes any progress, the bootstrap tail loop has no worker-liveness check —
  it exits only on a terminal JSONL line or "file never appeared in 2 min". If the agent dies without
  a terminal line (EDR kill — the exact failure the Deploy docs cite — a crash, the 6h task-limit
  kill, or ProgressWriter silently dropping the final Error line, which its catch at :100-104 permits),
  the loop emits a synthetic "Worker still running…" heartbeat every 15s forever. Client-side, :242
  resets the NoResponseTimeout watchdog on EVERY line before the heartbeat filter at :248, so the
  watchdog can never trip. A dead/half-applied install is indistinguishable from a slow one,
  indefinitely, with no failure signal.
- **Trigger:** WinRM-lane install where the agent process dies without writing a terminal result.
- **Fix direction:** the SMB lane already guards this (SmbAgentLane.cs:338 "stopped without reporting a
  result") — port an equivalent liveness/terminal-line guard to the WinRM lane. Research-first.
- **Severity:** HIGH (false-green in the core install function).

### HIGH-2 — One hung box silently freezes the entire monitor loop, fleet-wide, toggle still "on"
- **Where:** WorkspaceViewModel.cs:4601 (reboot-pending probe in the monitor)
- **What:** This is the only remote probe in the VM with NO per-call timeout — it awaits with just the
  monitor-lifetime token (contrast vitals at :4883, wrapped in a 120s linked CTS). PSRunspaceHost
  bounds only the connect (20s); the invoke is unbounded, and the probe calls DetermineIfRebootPending
  on the SCCM client WMI provider — the component that hangs on broken clients. MonitorLoopAsync:4374
  → Task.WhenAll:4398 means one never-returning probe stops all future monitor passes: online/offline
  dots freeze for all ~319 boxes, nothing throws, IsMonitoring stays true. Also pins a cross-tab probe
  slot + a per-host WinRM gate slot.
- **Trigger:** Patching mode + monitoring on + one online box with a hung CCM provider.
- **Fix direction:** wrap the invoke in a per-call linked-timeout CTS like vitals (:4883). Research-first.
- **Severity:** HIGH. **Live-check caveat:** static-analysis — the missing timeout is fact; "hung
  provider blocks without throwing" is the standard failure mode but was not live-reproduced.

---

## MEDIUM

### MED — "Cancel scheduled task" clears the Scheduled chip even when the unregister FAILED; the task still fires
- **Where:** WorkspaceViewModel.cs:1879-1885
- **What:** A non-terminating Unregister-ScheduledTask error (access denied, Task Scheduler RPC error)
  sets HadErrors without throwing; the code clears ScheduledAction/ScheduledNextRun/tracking
  unconditionally and logs "Cancelled pending scheduled task(s)." regardless. Only a small "Cancel had
  errors" breadcrumb survives; result.Errors is never surfaced (the sibling schedule path at :1828
  does surface it). A Vivre_Reboot task that wasn't actually removed fires at its trigger on a box the
  grid shows as clean.
- **Severity:** MED, but REBOOT-ADJACENT (a reboot fires on a box the operator believes is cancelled).
  Relevant to the just-shipped cancel-breadcrumb work.

### MED — Post-reboot outcome renders a FAILED reboot-pending probe as a definite green "no reboot pending"
- **Where:** WorkspaceViewModel.cs:3951-3967
- **What:** The catch says "treat as don't know" but sets rebootStillPending = false, and
  RebootOutcomeSelector.Select has no probe-failed input — so a probe failure is indistinguishable
  from confirmed-clean. Systematic variant: on a Kerberos-cached host the install/wave/rescan succeed
  via SMB fallback, but the probe (WinRM-only, no fallback) deterministically throws → the row reads
  "Back online · … up to date" while a chained CU/SSU second reboot is genuinely pending. Contradicts
  the method's own doc at :3868 ("a rescan or probe failure is surfaced honestly").
- **Fix direction:** give RebootOutcomeSelector a probe-failed/unknown state; never render probe
  failure as green. Fold into the parked post-reboot-count work. HANDLE WITH CARE zone.
- **Severity:** MED (systematic false-green on Kerberos-cached hosts).

### MED — A Kerberos rejection on the AMBIENT identity blocks CREDENTIALED WinRM for the whole session
- **Where:** RoutingPowerShellHost.cs:59
- **What:** The fast-fail fires before the credential parameter is consulted, and the cache has no
  eviction or credential dimension. Run Script, ConfigMgr actions, software/custom-column probes have
  no fallback — so the operator's natural workaround (enter working local-admin creds) is silently
  defeated until app relaunch. Caveat (agreed): whether credentialed NTLM would actually succeed
  depends on TrustedHosts/HTTPS config — but refusing to even ATTEMPT it while saying "WinRM is
  disabled for this host this session" is the defect.
- **Severity:** MED. Remoting-cache zone — research-first. (Same family as the ambient/credential
  asymmetry that bit this session.)

### MED — Stop during the SMB copy still launches the SYSTEM agent
- **Where:** SmbAgentLane.cs:257
- **What:** The token passed to Task.Run only prevents STARTING the copy delegate; no cancellation
  check between the copy completing and service.Start(). First check is inside TailAsync (:304) — so a
  cancel during the ~90s CU copy starts DISM-as-SYSTEM post-cancel and SCM-stops it almost immediately
  (agent OnStop grace is only 15s).
- **Fix direction:** ThrowIfCancellationRequested() before Create. One-line candidate.
- **Severity:** MED.

### MED — Enable WinRM has no timeout at all and runs the selection sequentially
- **Where:** WinRmEnabler.cs:27 (caller loop :5145)
- **What:** The only DCOM site of seven with no Timeout on session or operation and no CancellationToken
  on the invoke. One hung box (exactly the sick boxes this action targets) sticks its row on "Enabling
  WinRM…" and the rest of the selection is never attempted.
- **Severity:** MED.

### MED — SCCM client actions / schedule-reboot / cancel-task batches: sequential, no per-host timeout
- **Where:** WorkspaceViewModel.cs:5111 (also :1818, :1865)
- **What:** Connect is bounded (20s) but the invoke isn't, unlike every sweep (60-300s per-host linked
  CTSs). One hung CCM provider stalls the remaining selection. Mitigation: these are cancellable
  IAsyncRelayCommands, so Stop recovers them.
- **Severity:** MED.

### MED — Settings save failure is invisible in Release
- **Where:** AppSettingsStore.cs:165
- **What:** Fire-and-forget write; catch is Debug.WriteLine only (compiled out of Release). Cache is
  re-seated first, so the session behaves as saved — StagedHosts (the 2016 staging safety flags),
  MonthlyCu, and columns silently revert on restart. A restart then routes a flagged 2016 box down the
  normal WUA lane.
- **Fix direction:** surface the write failure via IActivityLog (same fix pattern as the SMB/AdaptiveLayout logging fixes). 
- **Severity:** MED (silent loss of the 2016 staging safety flag across restart).

### MED — SCCM health check false-greens on a broken ClientSDK namespace
- **Where:** ConfigMgrClient.cs:115 (and the bare catch at :113)
- **What:** Only SMS_Client gates with -ErrorAction Stop; the three ClientSDK compliance queries use
  SilentlyContinue and DetermineIfRebootPending sits in a bare catch {}. A corrupt/denied
  ROOT\ccm\ClientSDK — the textbook damaged-client state this check exists to find — yields empty
  arrays → confident MissingUpdates=false, IsHealthy=true. Failure and genuine compliance are
  indistinguishable.
- **Severity:** MED (false-green on exactly the broken-client state the check targets).

### MED — Every Details window permanently leaks a live filtered CollectionView over the app-lifetime activity log
- **Where:** ComputerDetailViewModel.cs:76 (teardown at ComputerDetailWindow.xaml.cs:21)
- **What:** New CollectionViewSource over the shared ActivityLog.Entries per open; the window's only
  teardown stops the requery timer; DetachFromSourceCollection appears nowhere. Each leaked view
  re-filters on every log line (log caps at 2000 but inserts constantly during sweeps).
- **Severity:** MED — **but PERF-TAGGED: DO NOT FIX ON THEORY. MEASURE FIRST.** The strong-subscription
  premise is WPF framework knowledge, not read from this repo. Measurement plan: open/close Details
  50×, run a vitals sweep on ~300 rows, Stopwatch the ActivityLog.Add insert/trim block at 0 vs 50
  leaked views; confirm the leak with a memory snapshot (rooted ListCollectionViews after close +
  forced GC). Flat cost + collected views = REFUTED.

### MED — Install re-entry guard's `installBegan` flag can be read before its dispatcher-posted write lands
- **Where:** WorkspaceViewModel.cs:3145 (retry continuation TransientRetryRunner.cs:48 ConfigureAwait(false))
- **What:** Written only inside a Progress<T> callback (posted async to the UI queue); on retry the
  read runs on a thread-pool continuation. Needs a double-transient install with a busy UI thread —
  narrow — but when it fires it produces exactly the false "up to date"/dropped-count retry the guard
  exists to prevent. The code's own invariant comment at :3155-3158 documents this hazard for
  onRetrying but not for this read.
- **Severity:** MED (narrow trigger, but defeats a guard that exists to prevent a false-green).
  HANDLE WITH CARE — concurrency.

---

## LOW (recorded; act only if it recurs — EXCEPT the reboot one, flagged up)

### LOW (flagged UP — reboot-adjacent) — Orphanable Vivre_Reboot_<guid> service is a latent loaded gun
- **Where:** DcomRebootTrigger.cs:199
- **What:** Best-effort delete can lose the race with the reboot; the leftover LocalSystem service's
  binPath IS `shutdown /r`. Demand-start = never fires on its own, BUT any later "start service" (an
  admin, a service-healing GPO, or Vivre's own Vitals-triage Start-service action matching its display
  name) reboots the box with NO confirm. No reaper exists.
- **Why flagged despite LOW probability:** it's a REBOOT path — cardinal-rule sensitivity outweighs
  raw probability. Candidate fixes: a startup reaper for orphaned Vivre_Reboot_* services, or a name
  that can't match a triage Start-service, or a non-shutdown binPath. Worth a conscious decision.

### LOW — Post-reboot rescan discards the real exception
- WorkspaceViewModel.cs:3908 — bare catch → placeholder "rescan threw"; cause never reaches UpdateError
  or the log (sibling scan path at :2882 records it).

### LOW — LcuPackageNeededDialog buttons fail silently in Release
- LcuPackageNeededDialog.xaml.cs:51, :69 — Debug-only catches; narrower than first claimed (:45
  recreates a missing folder) but policy-blocked explorer / no browser association still dead-clicks.

### LOW — Local Runspace leaks if Open() throws
- PSRunspaceHost.cs:31 — created outside try; remote paths handle this at :141-152, local ones don't.
  Per-failure, not per-sweep.

### LOW — RDP view re-load leaves the 450ms resize timer ticking with no handler
- RdpSessionView.xaml.cs:473 — DisposeSession detaches Tick, the supported re-load path never
  re-attaches; the debounced re-fit silently never runs. Narrow under TabControlEx keep-alive.

### LOW — Stale agent doc comments promise a reboot the code excised
- Program.cs:58, :826, :911 — "handles the optional post-op reboot" / "(the caller reboots)" contradict
  the body's "The agent NEVER reboots." Invites a future re-wire in the cardinal-rule file. Fix = delete
  the stale comments so the agent's source matches its behavior.

### LOW — Custom-column one-liners auto-execute on every list load
- WorkspaceViewModel.cs:1268 (default-on Auto-check) — operator-authored PS runs against the whole
  loaded list with no per-run action, no sandbox — the one AUTOMATED arbitrary-PS channel (Run script
  is invoke-by-name; this re-fires silently). Design-inherent; flagged for conscious acceptance.

---

## DECISIONS (not bugs — conscious accept-or-tighten)

### The install-flow "Reboot the N first" nudge is the weakest reboot gate in the app
- **Where:** MainWindow.xaml.cs:1122 (runs shutdown at :5175)
- **What:** Toolbar Install with nothing selected → targets = every machine in the tab; "pending" =
  whoever has a cached, possibly-stale RebootRequired flag. The confirm shows only a COUNT ("N of M…
  have a reboot pending"), never names — unlike the other two reboot entry points, which enumerate
  names and require explicit selection. It IS an explicit, clearly-labelled confirm (not a cardinal
  violation), but a count-only confirm over a derived whole-tab subset is a gate to consciously accept
  or tighten (e.g. enumerate names, or require selection).

---

## REFUTED (recorded for transparency — do NOT chase)

### Monitor double-increment of _consecutiveProbeFailures — REFUTED
- The mechanical pattern is real (no per-host in-flight gate) but both triggers are dead: IsUpdateMode
  is written exactly once at tab creation on an empty workspace (ShellViewModel.cs:125, grep-confirmed
  sole writer, so the mode-flip kick can't fire with rows loaded), and the add-rows kick only monitors
  rows absent from the in-flight snapshot (which also have previous == null, no 2-probe window).
  **Latent:** a future caller passing existing rows to MonitorRowsAsync concurrently with the loop
  would resurrect it. Not a bug today.

---

## Coverage honesty (declared blind spots)
- Intra-lane concurrency inside WuaUpdateLane/SmbAgentLane/RebootWave not line-by-line audited (they
  surface to the VM via IProgress only); ShellViewModel/RDP spot-checked.
- Agent's WUA COM lifetimes not audited (short-lived separate process — leaks die with it).
- SCCM-policy-driven reboots (the shipped SCCM install script / client actions) are governed by site
  deployment settings — environmental, not a Vivre code path.
- All static analysis. The two live-check-caveat items (monitor-freeze hang behavior, CollectionView
  leak magnitude) want runtime confirmation before fixes are built.
