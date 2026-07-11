# Changelog

Notable changes to Vivre, newest first. Loosely follows
[Keep a Changelog](https://keepachangelog.com/) — work-in-progress sits under **Unreleased** until
it ships, then gets a dated heading.

## Unreleased

## 1.14.6 — 2026-07-11

### Added
- **The software check now works on Kerberos-broken boxes.** On a machine where WinRM refuses the
  fast Kerberos login, "Check software…" no longer just fails — it falls back to the same read-only
  backup channel the health numbers already use and still fills in the Software column, running on your
  ambient Windows login (an alternate credential applies to the WinRM path only). If that backup channel
  also can't read the box, the check reports a real error naming both paths — an unreadable box is never
  faked as "not found".

### Changed
- **The in-app guide (Help ▸ How to use Vivre) was swept against the current app.** Chip lists, the
  bottom-panel behavior, button labels, the vitality rubric and the reboot-wave outcomes now match
  reality, and the newest behaviors (custom-column cancel, automatic reboot-service housekeeping)
  are covered.
- **The software check's backup channel now covers any WinRM failure, not just Kerberos.** If WinRM is
  unavailable for any reason — service stopped, misconfigured, session dropped, or Kerberos-rejected —
  "Check software…" falls back to the same read-only DCOM channel and still fills in the Software column.
  The answer is identical no matter which transport responds; you never see which one was used.
- **When an action can't run because WinRM is broken on a Kerberos-rejected box, the error message now
  points out that the software check still works there.** Health checks, custom columns, SCCM client
  actions and scripts add a short note that "Check software…" reads over the DCOM backup channel on
  these boxes — so you're pointed at what still works instead of a plain dead-end.
- **Grid text now has a little breathing room from the cell edges in the Health and Patching grids.**
  Adjacent columns (Software, Command result, Last error, …) no longer read as one run-on line, and
  cell text lines up with the column headers.

### Fixed
- **Removing a custom column while it's still filling now stops that fill.** Deleting a custom column
  cancels the running fill for it; a fill that still covers other columns keeps going and finishes
  those. Pressing **Stop** during a custom-column fill now freezes the progress counter where it was
  instead of letting it race to the end, and cells that were still filling when you stopped show
  "cancelled" rather than being left mid-fill.
- **A software check against a box that is fully offline now shows "Offline" instead of "WinRM
  unavailable".** The box is down, not broken — the check no longer burns a connection timeout to
  report a misleading remoting error, and the box gets a real answer the next time it's checked.

## 1.14.5 — 2026-07-10

### Added
- **You can now see where machines stand before setting WhatsUp Gold maintenance.** A new right-click
  **Check WhatsUp Gold state** action — on both the Health and Patching grids — writes each machine's
  current state to its row's Command result column (in maintenance, not in maintenance, not found in
  WUG, or unknown), plus a summary in the activity log, without changing anything. "Unknown" means Vivre
  couldn't read the state, not that the machine is out of maintenance. The **Reason** field now also
  only appears when you pick Enter (it's the note WUG records for entering maintenance; Exit doesn't
  need one).

### Changed
- **The Patching grid's command column header is now "Command result", matching the Health grid.**

## 1.14.4 — 2026-07-09

### Added
- **Leftover one-shot reboot services are now cleaned up automatically.** On the machines that need
  the SMB reboot fallback, the tiny helper service Vivre creates to fire the reboot could survive if
  its cleanup lost the race with the reboot itself. When a list loads, Vivre now quietly removes any
  of these leftovers on the loaded machines (only exact `Vivre_Reboot_*` names, only when fully
  stopped — never anything running), notes each removal in the activity dock, and honors the
  "auto-check on load" setting. The leftover was inert on its own, but removing it closes the door on
  anything else ever starting it.

### Changed
- **"Clean up" on the Server 2016 bar now works on any 2016 box, not just ones marked for staged patching.**
  It follows the selection like the rest of the toolbar: with nothing selected it cleans every 2016 box in the
  tab; with some selected it cleans exactly those (non-2016 selections are ignored). Component cleanup shrinks
  the Windows Update component store, which speeds up normal Windows Update on any 2016 box and frees space
  before staging — so it's no longer scoped to the staged subset. Stage and Verify are unchanged (still act on
  boxes you've marked for staged patching); Clean up never reboots, same as before.

### Fixed
- **The Users Online dot no longer shows a green "No" when the check couldn't actually read the machine.**
  A failed read now shows a grey "?" (unknown), while a machine that genuinely has nobody logged on
  still shows the green "No". The wrong green was on exactly the signal an operator checks before
  rebooting a box — it read "safe, nobody's on" when the real answer was "couldn't tell".
- **A rare timing gap could make a retried install misreport "up to date" and drop the installed count.**
  When an install hit a transient Windows Update hiccup, was retried, actually began installing on the
  retry, and then hit a second hiccup, Vivre could in a narrow window forget the install had begun and
  re-run it — the re-check would then find nothing left to do and report a false "up to date", losing the
  real installed count and the pending-reboot flag. The install-began signal is now recorded the instant
  the machine reports it, closing the gap. (Never observed in the field; found by code audit.)
- **Saving a machine list is now crash-safe.** Like settings, a named list was written by overwriting its
  file in place, so a crash or power loss mid-save could corrupt the list. It now uses the same atomic
  temp-file swap, so the previous good list always survives a failed save.
- **SCCM client actions no longer hang the whole batch on one stuck machine — and Stop now works on them.**
  Client actions (Machine Policy, Hardware Inventory, Update Scan, …) used to run one machine at a time
  with no time limit: one hung box stalled every machine after it, some failures aborted the rest of the
  selection outright, and the Stop button couldn't cancel any of it. They now run on all selected machines
  at once (on the same shared connection budget as the other checks), each machine gets 60 seconds before
  its row honestly reads "Timed out", one broken box can never abort the others (its row reads "WinRM busy"
  or "WinRM unavailable" instead), and Stop cancels the batch.
- **A crash can no longer corrupt Vivre's saved settings.** Settings — including which 2016 boxes are
  marked for staged patching — were written by overwriting settings.json in place, so a crash, power loss,
  or full disk mid-write left a truncated file and silently dropped those flags on the next launch. The
  file is now written to a temp file first and atomically swapped in, so a crash mid-write always leaves
  the previous good settings intact.
- **The health check's "last boot time" read is now documented as deliberately best-effort** (an isolated
  failure shows a blank cell — never a wrong value, and it doesn't affect the health verdict), and its
  parsing gained the test coverage it was missing.
- **The message after "Reboot & verify" is now honest in all three ways it could quietly lie.**
  (1) If the post-reboot "is a reboot still pending?" check couldn't answer (broken WinRM/Kerberos,
  or a hung SCCM client), the row used to read a green "Back online · installed N · up to date" —
  indistinguishable from a genuinely clean box. It now reads "Back online · couldn't confirm reboot
  state — re-check" with a grey "?" in the Pending Reboot column, and is never shown as up to date.
  (2) That check also had no time limit — a hung SCCM client could pin the row for up to ~4¾ hours;
  it now gives up after 2 minutes into the same honest "couldn't confirm" outcome. (3) The
  "installed N" number used to come from whatever install ran last on that machine this session —
  a standalone reboot claimed "installed 0", a failed later attempt erased the real count, and an
  old failure could resurface as this reboot's outcome. The count now appears only when this
  session's install actually installed or failed something, is reported once, and is dropped from
  the message entirely otherwise.
- **Cancelling a scheduled task now tells the truth.** If the machine failed to remove the task
  (access denied, Task Scheduler not answering), the row used to drop its Scheduled marker anyway —
  so a scheduled reboot the operator believed cancelled could still fire. The cancel now verifies the
  tasks are actually gone before clearing anything; on failure the Scheduled marker stays, the row
  reads "Cancel failed — task may still fire" with the surviving task named in the activity log, and
  the cancel can simply be run again.
- **A machine with a broken SCCM client no longer shows a green "no updates missing" check.** When the
  client's ClientSDK data couldn't be read at all, the health check used to render the empty answer as
  fully compliant ("Healthy", green checks) — hiding exactly the damaged machines it exists to catch.
  Those cells now show a grey "?" with "SCCM ClientSDK unavailable — updates state unknown" and the
  row surfaces under the Errors filter. Healthy machines, including ones with genuinely zero missing
  updates, look exactly the same as before.
- **A corrupt settings file no longer breaks the whole session (or the next launch).** A damaged
  settings.json used to make Vivre fail at startup, and any later settings read could error too. It
  now falls back to defaults once, with a red activity-log entry warning that staged-patching flags
  may be missing until the file is fixed. A merely-locked (not corrupt) file is retried instead, so a
  passing antivirus scan can never cause your saved settings to be overwritten with defaults.
- **Enable WinRM no longer reports success when the machine never answered properly.** A response with
  no result code used to read as "started" — it now reports an honest failure asking you to retry.
- **One hung machine no longer freezes the fleet monitor for everyone.** The monitor's reboot-pending
  check had no time limit, so a single box with a wedged SCCM client stalled the whole 20-second refresh
  pass indefinitely — every machine's online dot froze until Vivre was restarted. The check now gives up
  after 120 seconds, quietly backs that one box off (retrying every 5 minutes), and the rest of the fleet
  keeps refreshing. The box's reboot-pending flag keeps its last-known value; nothing is painted as failed
  just for being slow.
- **Pressing Stop during a package copy no longer launches the update agent anyway.** A Stop pressed
  while the (up to ~90-second) file copy to a 2016 box was in flight used to be ignored until after the
  agent had already started as SYSTEM — only to be torn down seconds later, mid-work. The cancel is now
  honoured the moment the copy finishes: the agent is never launched and the copied files are cleaned up.
- **A failed settings save is now reported instead of silently losing the change on restart.** If writing
  settings.json failed (locked file, disk error), the running session behaved as saved but the change
  vanished on the next launch — worst case, a 2016 box silently lost its staged-patching flag and was
  routed down the wrong update lane. A failed save now writes a red activity-log entry naming the file and
  the reason.
- **Enable WinRM no longer gets stuck behind one dead machine, and Stop now works on it.** It used to run
  one machine at a time with no time limit — the exact machines it targets are the sick ones, so one hung
  box stranded the whole selection forever. It now runs a few machines in parallel, each attempt bounded
  (~25s worst case), a Stop cancels the rest cleanly (no false "failed" spam for cancelled boxes), and the
  status bar narrates progress like other operations.
- **A dead update agent no longer leaves the row spinning "Installing…" forever.** If the agent process
  died without writing a final result (a crash, a security-software kill, or the scheduled task's time
  limit), the remote watcher kept heartbeating on its behalf, so the machine looked busy indefinitely and
  held an install slot until Stop. The watcher now checks the scheduled task's state during quiet stretches
  and, when the agent is gone with no result written, the row turns red with "The update agent stopped
  without reporting a result… Re-scan to confirm what was installed" within about 16 seconds. A stream that
  ends without a final result is likewise reported honestly instead of freezing on the last mid-run status.
  The task-level time limit is now 12 hours for watched (run-now) installs and stays 6 hours for scheduled
  ones, which run unwatched.
- **Long installs are no longer cut off by a 3-hour ceiling, and a cut-off install can no longer mislabel
  itself "Worker did not start writing progress within 2 minutes."** Two boxes mid-install (80% and 32%)
  hit the old wall-clock: Vivre tore the watch down, its cleanup deleted the progress log under the still-live
  remote watcher, and the watcher's unlatched startup check then painted the startup-failure message over the
  honest "Timed out" — while the install actually kept running on the box. Install/Uninstall now run with no
  wall-clock in Vivre (like 2016 Clean up); the 90-second silence watchdog remains the safety net, so a box
  writing progress is never cut off by Vivre and a dead session still fails fast. (An agent that dies without
  reporting a result is caught by the watcher's task-state probe — see the entry above.) The watcher now only
  reports "did
  not start" when nothing was ever written, and a line arriving after an operation has finished can no longer
  overwrite the row's final state.
- **The bottom status bar no longer double-prints the sweep progress.** During a fleet sweep it briefly
  showed the machine count and timer twice — e.g. "Checking vitals — 65/319 · 00:36" immediately followed
  by "65/319 machines  00:36". The second copy was a leftover from the removed fleet band; the count and
  elapsed now show once, in the single narration line. Cosmetic only — no change to the sweep or its
  progress values.

## 1.14.3 — 2026-07-01

### Changed
- **The machine grid now keeps a "Scheduled task cancelled" note after you cancel a scheduled task,
  instead of blanking the message.** Previously, cancelling a scheduled install or reboot cleared the
  Windows-update-message cell on the Patching grid, so with the Activity panel closed there was no
  on-grid sign anything happened. It now shows the same "Scheduled task cancelled" text the Fleet
  Health grid already displayed, and that note stays until the row's next action replaces it.

### Fixed
- **Offline machines on the Health grid now read "Offline" instantly, instead of sitting on
  "Reading vitals…" for ~20 seconds and leaving stray "timed out" / "WinRM n/a" cells behind.** When a box
  is unreachable by both ping and DCOM, Vivre now skips the doomed health, vitals, and custom-column probes
  (each of which was waiting out a remoting timeout on a machine that's simply off) and marks it Offline
  directly. A Kerberos-broken box that's still reachable over DCOM is unaffected — it still gets its vitals
  over the backup channel. Re-checked every sweep, so a box that comes back online is picked up normally.
- **A powered-off, Kerberos-broken app server no longer flips to a false "online" or a
  "Offline since … — waiting for it to come back…".** When such a box (one that Vivre reads over the
  DCOM/SMB backup channel) is actually off, its vitals read comes back empty — and Vivre was treating
  that empty read as a successful reach, marking the row online and "managed", which re-triggered the
  reboot-wave "waiting to come back" message on a machine that was simply powered off. An empty vitals
  read is now treated as the failed read it is: the row reads a calm "Offline" and still keeps its
  degraded-connection note in Machine Details. A box that actually answers (even partially) over DCOM is
  unaffected — it stays marked as reached so its reboot tracking still works.
- **Triage now shows stopped auto-services and the logged-on user on Kerberos-broken / WinRM-down
  machines.** These boxes are read over the DCOM/SMB backup channel, which previously left the
  "stopped auto-start services" list and the "Users online" indicator blank (the WinRM path filled them,
  the DCOM path didn't). The DCOM vitals read now populates both — using the same `Win32_Service` and
  explorer.exe-owner queries the WinRM path uses — so a machine on the backup channel shows the same
  triage detail as one read over WinRM. Read-only; the 0–100 vitality score is unchanged (stopped
  services were never scored).
- **Powered-off machines now read a calm "Offline" instead of a misleading "Offline since [launch time]"
  or a red WinRM/SMB error.** A server that was off the whole time (its management controller or a reused
  IP can still answer a ping) was mistaken for one that had been up and then "went offline", and scanning
  it produced a scary "Can't reach over WinRM or SMB — not manageable right now" error. Vivre now shows a
  plain "Offline" for a box it never actually reached over remoting this session, and a scan against an
  offline box reports "Offline" without attempting the doomed remoting. Preserved: a machine that WAS being
  managed and then drops (e.g. one you rebooted for patching) still shows "Offline since HH:mm — waiting
  for it to come back…", and a box that answers ping but whose remoting is genuinely broken still shows the
  real "Can't reach over WinRM or SMB" error.
- **A failed cleanup of the temporary SMB helper service is now logged instead of vanishing.** On a
  Kerberos-broken box, Vivre creates a per-run helper service (Vivre_WUA_*) and removes it when done; a
  failed removal previously left only a debug line that release builds strip. It now records a warning to
  the activity log and rolling log file (the leftover is harmless — the next run reaps it).
- **Scheduled installs and reboots now fire at the time you actually picked, on every machine.** The
  one-time task is registered on the remote box, and the trigger time was sent with no time zone — so each
  target read your chosen wall-clock as *its own* local time. A box in a different zone (e.g. a UTC Azure VM)
  could fire hours off: a "2 PM" install or reboot ran 4–5 hours early on a UTC box, into business hours. The
  chosen time is now anchored to **your** local time (this PC's) and converted to an absolute instant, so every
  selected machine runs at the same moment regardless of its own time zone. The "scheduled for …" message now
  reads "(your time)" to make that explicit.

## 1.14.2 — 2026-06-29

### Changed
- **Smoother grid during large patch sweeps.** A per-row update-progress tick (which fires many times a
  second per machine while a download/install runs) now refreshes only the overall progress bar instead of
  recomputing every fleet tally and re-walking the whole machine list — so the per-tick UI work drops sharply
  at fleet scale. Phase changes (scanning → installing → done / reboot / failed) still refresh the full
  tallies, so the bottom-bar counts stay exact. Responsiveness only; no change to what's shown.

### Fixed
- **A multi-second UI freeze when opening a large machine list on a cold start is gone.** The fleet vitals sweep
  opens a WinRM connection per host on a thread-pool thread; with the pool's default minimum worker count (= CPU
  count, e.g. 2), those connections were injected only about one every half-second and serialised behind the
  slowest connect — freezing the UI for as long as the slowest box took to answer. Raising the minimum
  worker-thread floor at startup lets the already-bounded set of connections run in parallel, so the UI stays
  responsive and rows fill in as each box reports.
- **Cold start no longer leaves the shell blank for ~10s when you open a big list.** With "Check vitals on
  load" on, opening a few-hundred-machine list as the first action after launch fired the vitals sweep inline,
  on the UI thread, before the just-loaded grid could paint — so the whole content area sat blank for ~10s.
  The auto-check kickoff (the vitals sweep + the custom-column fill) is now deferred to a low (Background)
  dispatcher priority, so the loaded rows lay out and paint first (~1s) and vitals begin a render cycle later.
  Auto-check still runs on every load — only its start moves slightly later; the sweep's heavy remote work was
  already off the UI thread.
- **The machine grid no longer comes up blank until you resize the window.** On a freshly shown tab — or
  after reloading a list into an already-open one — the grid could render empty (no rows) until you nudged
  the window size, sometimes for many seconds. The cause was a layout race during the window-open animation:
  the virtualizing grid was first laid out before it had a real height, realized zero rows, arranged itself to
  zero, and then nothing told it to lay out again once the size settled. A single debounced re-layout now fires
  ~150 ms after the size (or the bound list) stops changing and re-measures + re-arranges the grid, so it fills
  its space and shows its rows on its own — exactly what a manual resize was doing. (Replaces three earlier,
  narrower re-measure attempts.)
- **Loading a big machine list with auto-check-on-load no longer freezes the app.** With "Check vitals on
  load" enabled, adding a few hundred machines (e.g. ~319) froze the UI for 20-30 seconds: each health-check
  result that came back recomputed the whole-fleet summaries by re-walking every row, so N results × an N-row
  walk was O(N²). The fleet-summary recompute is now coalesced — a result marks the tallies dirty and a
  UI-thread timer recomputes them at most ~5×/second — turning the sweep from O(N²) into O(N). The summaries
  still climb smoothly during the sweep and are exactly correct when it finishes, and each machine's own row
  (dot, score, status) still updates the instant its result lands.
- **A "Reboot complete" / "Back online" notice no longer lingers in the Reboot column after the row moves on.**
  These reboot notices had no clearer, so one could sit on a row that was now scanning or installing — looking
  like a fresh reboot on an unrelated operation. Starting a new scan / install / uninstall on a row now clears
  a stale reboot notice. The live "Offline since…" and "WinRM temporarily unavailable…" messages are unaffected
  (they keep their own clearers), and a genuine reboot-and-verify still shows its completion message at the
  moment it completes.
- **A failed update no longer shows a green "Up to date" pill.** If an install (or uninstall) finishes with
  any update failed, the row now shows the red **Error** status — the failure wins over both "up to date" and
  reboot-pending, and the reboot dot still lights separately when a reboot is also pending. Previously a box
  that installed 0 and failed 1 could read green (or hide behind an amber reboot pill). Fixed at both layers:
  the on-target agent now classifies an all-failed install as Error (matching the uninstall path), and the
  controller forces Error on any install/uninstall completion that reports a failure. The "Installed N, M
  failed" detail text is unchanged.
- **A machine rebooted outside Vivre no longer keeps showing "… reboot required" on its update message.**
  When a box you'd just patched was rebooted by someone else, Vivre correctly cleared the reboot-pending pill
  and flag but left the update-message text still reading "… · reboot required". The text now clears too. (The
  cleanup is separator-agnostic, so it can't silently drift out of sync with the agent's wording again.)
- **A patch action on a box that's already reboot-pending no longer leaves the row stuck "working."** When the
  on-target agent defers a mutating action (install / clean / stage) because a reboot is already pending, the
  row now finishes cleanly — previously the background session stayed open (and left temp files on the target)
  until you hit Stop.
- **Cancelling a scheduled task clears its "… scheduled for …" message.** The update-message column no longer
  keeps showing the old schedule after a cancel (it cleared the chip but left the text).
- **Reloading a computer list no longer carries over stale per-host monitor state.** A returning host could be
  silently skipped for reboot-pending checks; it now starts fresh.
- **"Check Vitals" now refreshes the "Users Online" column** (previously only a full Check All updated it).
- **Machine Details no longer shows two different "Reboot pending" values** — the Readings card now tracks the
  same live value as the header, so they can't disagree after a reboot.
- **A confirmed-online ping clears a stale "Offline since …" reboot message** left over when monitoring was
  stopped while the box was down.
- **Starting a stopped service reports its real post-start status** (it used to log "now Stopped" right after a
  successful start; an already-running service now says so instead of "Started").

- **Fixed a native-handle leak on machines reached over DCOM** (Kerberos-broken / WinRM-down boxes). The vitals
  and reboot-pending probes created a small CIM parameters object on each call without releasing it, so its
  Windows handle lingered until garbage collection; they are now disposed immediately, matching the rest of the
  codebase. Prevents handle build-up over long sessions against such fleets.

### Internal
- Made the SMB-agent heartbeat-line filter consistent across its read paths (no behaviour change). Remaining
  items from the 2026-06-23 drift/stale hunt (post-reboot install-count accuracy; DCOM-path vitals fields) are
  parked in the backlog.
- Skipped a wasted per-minute relative-time change-notification for grid rows that have no boot time (no
  behaviour change).

## 1.14.1 — 2026-06-23

### Changed
- **The bottom dock is now a mode-labeled toggle that no longer auto-opens.** The single dock button reads
  **Updates & Activity** in Patching mode and **Activity log** in Health mode; selecting a machine row no
  longer pops the dock open — the toggle is the only thing that opens it, and it reopens at the height you
  last dragged it to (floored so the machine grid can't vanish).
- **Run Script now uses the app's shared connection path.** Script runs go through the same WinRM→SMB
  transport cache as every other action, so on a host that already rejected Kerberos this session a script
  run fast-fails instead of re-paying the ~20-second connect timeout (and a healthy WinRM connection is now
  recorded for the rest of the app to reuse).

### Fixed
- **Machine Details now refreshes live when you click Check Vitals.** With the Details window open, re-running
  Check Vitals updated the score but left the readings (system-drive free, memory, CPU, last boot, reboot
  pending, the drives list, and the "why" reasons) showing the old values until you closed and reopened the
  window. They now update in place.
- **Reboot & verify now shows it's rechecking, and stops printing "Up to date · up to date."** After a box
  reboots and comes back online, Vivre automatically rechecks it for updates — the row now reads
  "… · rechecking for updates…" during that check, so you know not to rescan it by hand. And a Server 2016
  box with nothing left to install now reads a single **Up to date** instead of the doubled
  "Up to date · up to date."
- **Reboot & verify no longer false-fails a slow-to-restart Server 2016 box as "rolled back."** If a box
  reported its restart was already underway but was slow to actually drop off the network, Vivre could read
  its old build number and wrongly mark it failed. It now waits for the box to genuinely go offline before
  verifying, so a box that's merely slow to come down is verified correctly when it returns.
- **Cancelling WhatsUp Gold maintenance no longer reports it as "failed."** Pressing Cancel now leaves a
  neutral "cancelled" note on each row instead of "WhatsUp Gold: failed — The operation was canceled."
- **Continuous monitoring no longer dies silently.** If the background monitor hits an unexpected error it
  now logs it and switches the toggle off (so you can restart it), instead of leaving the toggle showing
  "on" while nothing is actually running.
- **Software check finds products whose name contains brackets.** A search like `Cisco [VPN]` is now matched
  literally instead of being treated as a wildcard pattern (which silently returned "not found").
- **A re-added machine's "Pending Reboot" column populates promptly again.** Removing and re-adding a host
  no longer leaves stale per-host timing that could blank the column for up to five minutes.

### Internal
- Added failure logging on several swallowed-exception paths (settings reads/writes, the pre-dialog currency
  check, layout-state persistence), removed dead fields/code, and refreshed the docs (README, windows-patching-lane,
  backlog, key-file-path-map, the 2016-LCU specs, the RDP findings) to match the as-built code. No behaviour
  change.

## 1.14.0 — 2026-06-19

### Added
- **Install concurrency default raised to 50, and now operator-tunable** — the cap on how many machines
  install at once defaults to **50** (was 10) and is editable in **Settings ▸ Max simultaneous installs**.
  Changing it takes effect on the next install you start; an in-flight install is never disrupted. The scan
  cap and the reboot-issue burst cap are unchanged.
- **Server 2016 "Clean up" now shows it's alive on a backlogged box and reads at a glance when it's
  done.** A component-store cleanup can legitimately run for hours while DISM's percent sits frozen, so
  the row now shows a live host-side **"Cleaning — 12m"** readout (with the percent when Windows reports
  one) that keeps ticking independent of DISM — it never looks stuck. It only *flags* a long run
  (*"looks stalled (may still be working)"*, and past 8 hours *"still going, check the box"*) as a
  heads-up; it never gives up on a working box. The old 3-hour hard timeout that could tear down a
  still-working cleanup mid-run is gone for cleanup (a genuinely dead agent is still caught by the
  silence-watchdog and a real DISM error). When it finishes the row reads one of three distinct
  states — **"Cleaned — ready to Stage"** (green), **"Cleaned — reboot-pending (reboot before Stage)"**
  (amber), or, if a reboot was already pending so it didn't run, **"Couldn't clean up — reboot to clear
  the pending state first."**
- **A servicing-busy refusal no longer masquerades as a successful stage.** When the 2016 Stage or
  Clean up agent declines to run because a reboot is *already* pending (to avoid colliding with in-flight
  Windows servicing), the row now lands on a distinct **Deferred** state — amber, reboot-pending, with a
  *"reboot to clear the pending state first"* message — instead of reading as the amber **"Staged — run
  Reboot Wave"** state. The box wasn't touched, so it must not look staged; the label prescribes a reboot
  the operator runs through the existing Reboot Wave (no new reboot path was added).
- **Patching now rides out transient Windows Update network blips instead of failing — and never
  fake-greens a box it couldn't actually scan** — a scan or install that momentarily can't reach Windows
  Update now **silently retries the whole operation up to 3 times** (~60s apart, with a little random
  jitter so a fleet-wide blip doesn't retry in lockstep) before giving up. While it retries, the row shows
  a calm *"Couldn't reach Windows Update — retrying (n/3)…"*, not an error. If every attempt fails it lands
  on a distinct, honest **"Can't reach WU"** state (red, with the error code and a "try again" hint) —
  **never** a false *"Up to date."* This closes a trap (the tool we're replacing falls into it) where a
  search that came back *with errors* — zero updates plus a non-success result code — read as a clean,
  patched box. The root cause was pinned from a real box's `WindowsUpdate.log`: the very first call Windows
  Update makes (a service-locator lookup, **before** it even searches) timed out (`0x80072EE2`) during a
  brief network blip and worked perfectly an hour later. Covers every path — normal (WinRM) scan and
  install, and the SMB-agent scan and install used for Kerberos-broken boxes. A genuine install failure
  still surfaces immediately (no pointless retry), and a box that has already started installing is never
  re-run (so an installed count is never silently dropped). Nothing reboots as part of this.
- **Server 2016 patching is now opt-in per box — flag only the ones that need staged (DISM) patching** —
  by default every Server 2016 box now patches through **normal Windows Update**, the same as a 2019/2022
  box. The full-package DISM staging lane is reserved for the boxes that actually need it (the ones whose
  monthly cumulative update chronically fails through Windows Update). **Right-click a 2016 row ▸ Mark as
  Staged patching** to flag it (and **Remove Staged flag** to undo); flagged boxes show a small **Staged**
  pill in the grid and are listed under **Settings ▸ Staged patching machines**, where you can remove one or
  clear the list. When you **Install** (or **Install all**) and the run includes a flagged box whose CU
  hasn't been staged yet, Vivre asks what to do: **Stage CU first** (recommended — stage the big cumulative
  update now, commit it later with the Reboot Wave), **Install minor updates only** (everything except the
  cumulative update, which is staged separately), or **Cancel** (skip just those boxes — the rest of the run
  still installs). Boxes already at this month's build are detected up front and skipped automatically
  ("Already current — skipped"), going straight to their minor updates. The flag is remembered between
  sessions; non-flagged 2016 boxes are never touched by the staging lane.
- **Boxes that reject WinRM (Kerberos) are detected and flagged instead of silently failing** — a growing
  set of servers refuse the WinRM login with a Kerberos error (`0x80090322` — their Active Directory
  identity is out of sync, classic after a VM snapshot revert; BatchPatch reaches them, Vivre couldn't).
  Vivre now recognises that specific rejection, switches the host to the SMB/DCOM path on your current
  Windows login (**no credential prompt, ever**), and caches the decision so it never re-waits on the
  doomed ~20s connect. **Vitals** names the problem and the fix ("WinRM/Kerberos auth failing — verify the
  host's SPN / encryption-type / re-sync the machine account") and docks the score, so the AD issue stays
  visible — while operation results stay clean (no "fell back" wording). The on-target update agent is now
  Authenticode-signed. The SMB/DCOM scan + install execution path for these boxes is now live (drop a
  signed agent over the admin share → run it as a one-shot SYSTEM service → stream progress back over
  SMB), so a Kerberos-broken box can be scanned, patched, and rebooted like a WinRM box.
- **Run operations on different machines at the same time** — scanning or installing on one set of
  machines no longer locks the whole tab: kick off Install on server A, then immediately Scan server B,
  from the toolbar or the per-machine panel. Rows already busy with an operation are skipped with a
  per-row "Skipped — busy (Install running)" note that stays until the next action touches them; the
  status bar narrates multiple operations ("2 operations · Install 3/12 · Scan 7/40"), the fleet band
  sums their progress, completion banners queue one at a time, and Stop cancels everything in the tab
  ("Stop all — N operations running"). Total remote load stays bounded by the same shared budget as
  before — concurrency never adds connections.
- **Check Vitals from the machine details window** — the Vitals tab has its own Check Vitals button that
  reads health + vitals for just that one machine (busy spinner while it runs; disabled while a fleet
  sweep already holds the machine). No more sweeping the whole tab to populate one box.
- **Installed updates are marked in the panel** — after a zero-failure install, that install's updates show
  greyed with an **"Installed — reboot pending"** chip (or just "Installed" when no reboot is needed), their
  checkboxes untick so Install checked can't re-target them, and the summary line adds "· N installed this
  session". A partial failure shows an honest banner ("Install completed with N failure(s) — rescan after
  reboot for exact state") instead of guessing per-row. The panel's "All" button skips marked updates too.
  Display-only; a fresh scan clears it.
- **Vitals on the Patching grid** — the same 0-100 vitality pill the Health grid has, so a sick box stands
  out while patching. Display-only (Patching already runs the vitals sweep); one shared template drives both
  grids so they can't drift apart.
- **"What does the Vitality score mean?"** — a new in-app help topic: the scoring rubric (start at 100; the
  disk / memory / CPU / reboot-pending / uptime penalties; the 80/50 band cut-offs), that the Unhealthy
  filter includes Offline boxes, that CPU/memory are point-in-time samples a re-check clears, and which
  signals are gathered but deliberately not scored.
- **Server 2016 cumulative updates via a full-package DISM lane** — the Server 2016 boxes that
  chronically fail monthly CUs through Windows Update (Express-delta assembly breaks at pre-stage) now
  get a dedicated lane that sidesteps Windows Update / Delivery Optimization / Express entirely: drop
  the full CU package and install it with `Add-WindowsPackage`. A self-populating **Server 2016**
  filter chip appears once a vitals check finds a 14393 box, with a four-step action bar — **Clean up →
  Stage → Reboot Wave → Verify** — that the operator drives by hand (nothing reboots on its own). Stage
  installs with the server still serving and stops when the box is genuinely reboot-ready; **Reboot
  Wave** reboots only the boxes you select and confirm, escalating to a forced reboot after 8 minutes
  if one won't go down; **Verify** confirms the post-reboot build, so a rolled-back box that returns at
  the old build is caught as failed rather than a false green. A guided prompt walks you to the right
  package (with the catalog link) when it's missing. Mixed-fleet **Install all** auto-routes per
  machine: 2016 → this lane (stage-and-stop), 2019+ → the existing one-step WUA lane, unknown/unscanned
  → skipped.
- **Fleet-wide reboot-and-verify** — right-click ▸ **Reboot & verify…** on any selection reboots
  each machine (gracefully; forced after 8 minutes if it won't go down), then watches until it's
  genuinely back and auto-rescans. Server 2016 boxes verify by build/UBR and catch a rolled-back
  update as failed; all other boxes re-scan Windows Update and show what's still applicable.
  Outcomes read "Back online · installed N · up to date / N remaining / couldn't rescan" directly
  in the row. The unbounded offline watch (`_waveThrottle` = 256) means a slow 45-minute Server
  2016 commit never blocks a fast box from completing; simultaneous reboot issuance is capped
  (`_rebootTriggerThrottle` = 12) to protect DCs/DNS/auth from a simultaneous drop. Nothing
  reboots without your explicit confirm — the post-reboot rescan is read-only (scan only, never
  install or further reboot).
- **WhatsUp Gold maintenance pre-flight** — *WhatsUp Gold maintenance…* now runs a connection +
  module/credential check before it touches anything: the dialog stays open until the WUG server is
  reachable, the `WhatsUpGoldPS` module is present, and your login is accepted, then closes and fires
  the per-device set. **Test connection** and a hidden-until-needed **Install module** button let you
  confirm or repair the setup up front instead of discovering a problem mid-run. Failures now name the
  real cause (unreachable server / rejected credentials / module missing) instead of a misleading
  "module not installed".
- **Smarter Server 2016 staging** — Stage now confirms each box's current state before touching it.
  A 2016 box must be scanned this session before it can be staged (Stage lists any boxes that still
  need a scan and stops); a box that's already staged and waiting to reboot is skipped ("Already
  staged — run Reboot Wave"); and a box already at the target build is skipped after a quick build
  check ("Already current — skipped"). If that build check can't reach the box it stages anyway
  (fail-safe). The display-only "Approx. package size (MB)" Settings field was removed (the package
  is matched by KB + architecture, never size).

### Fixed
- **Install/scan no longer crash with a cross-thread error when a machine needs a Windows Update retry** —
  when reaching Windows Update hit a transient network blip and the operation retried (common when a big
  Stage batch was saturating the network at the same time), the row's "retrying…" status update ran on a
  background thread and threw *"The calling thread cannot access this object because a different thread owns
  it,"* failing the whole install/scan on every affected machine. The status update is now marshalled to the
  UI thread. Added a debug-only safety check that makes any future off-thread write to a grid status property
  fail loudly during development instead of crashing in the field.
- **Server 2016 "Clean up" no longer reports a false failure when security software holds files open** — a
  component-store cleanup that cleared the update backlog but couldn't delete a locked remainder (commonly
  AV/EDR holding WinSxS handles, so DISM exits access-denied) used to read as a hard *"Component cleanup
  failed"* in red. It's now correctly shown as **"Cleaned · locked files (see log)"** in the neutral done
  (green) state, with a plain-English activity-log note explaining that staging isn't blocked and how to
  reclaim the rest (temporarily disable AV and re-run, or reboot to release the lock). The agent backs this
  with evidence — on the access-denied exit it runs a read-only `AnalyzeComponentStore` and reports the
  reclaimable-package count; the reclassification is decided from those raw facts, not assumed. Genuine
  cleanup failures (any other exit code) still surface as errors unchanged. No reboot path was added.
- **Flagged Server 2016 boxes with no pending OS cumulative update now install their minor updates instead of
  being dead-ended by the staging prompt** — the staging gate exists only to keep the broken Express-delta OS
  cumulative update off Windows Update on a flagged box, but it was blocking *any* flagged box that wasn't
  verified-current — including one whose scan holds only minor updates (Office, Defender) and no OS CU at all.
  The bottom **Install checked** button just showed *"Needs CU staging"* and installed nothing. The gate now
  fires only when there's genuinely something to stage: a scan that actually contains a Server 2016 OS CU, or an
  unscanned box that can't be cleared yet (SQL Server / .NET cumulative updates never count). A freshly-scanned
  flagged box with no OS CU falls through to a normal Windows Update install of its ticked minor updates. The
  "already current" routing note moved off the prominent grid column into the activity log, so the column shows
  the real install outcome. Install-path only — nothing reboots.
- **Cross-Domain RDP sessions now fill the pane *and* keep Failover Cluster Manager's right-click menus
  working** — the embedded remote session now pins its display scale to **100%** instead of matching this
  PC's display scale (~150% on a high-DPI jump box). At the host scale, FCM's context menus collapsed the
  instant they opened (a documented Windows bug at any scaling above 100%); pinned to 100% they work, and the
  session still fills the pane natively because the remote is rendered at the pane's own pixel resolution.
  Rendering config only — nothing reboots.
- **The update Size column shows a real size for every update — and quietly fixes the one case Windows Update
  inflates** — the column now shows Windows Update's reported download size for all update types (Defender,
  drivers, .NET, SQL, normal cumulative updates), matching BatchPatch, with no extra lookup. The one exception
  is an express/checkpoint OS cumulative update (Server 2025 / Windows 11 24H2), where Windows Update reports a
  worst-case *aggregate* that's wildly too big (e.g. **21,926 MB** for a package that's really **2,435 MB**).
  Only for those — detected by an implausibly large value (>10 GB) — does Vivre substitute the exact published
  size from the **Microsoft Update Catalog** (a single read-only HTTPS lookup, cached per KB). It shows a dash
  (**—**) only when *both* are unavailable: an inflated express update whose catalog size couldn't be fetched
  (e.g. the machine running Vivre is offline / locked-down). Normal updates never trigger a catalog lookup, so
  there's no network cost for the common case. (Display only — nothing that reads or acts on the size changed.)
- **Reboot & verify now auto-selects the remaining updates it surfaces** — after a box comes back from
  reboot-and-verify still needing updates, those updates are now ticked, so the top **Install** can target
  them in one click — the same readiness a fresh scan gives. Previously the post-reboot rescan surfaced the
  count but left the update unchecked (it inherited the just-completed install's untick), so Install reported
  "No updates selected." The rescan stays read-only — it only selects; it never installs or reboots.
- **The Server 2016 panel buttons explain themselves instead of silently doing nothing** — Clean up, Stage,
  and Verify only act on Server 2016 boxes you've marked for staged patching. When none are marked (the
  common "the 2016 buttons don't do anything" case), clicking one now opens a short dialog telling you to
  right-click a Server 2016 row ▸ **Mark as Staged patching** and try again — instead of quietly no-opping.
  The check runs before any box is touched. Reboot Wave is unchanged (it reboots the boxes you select and
  stays disabled until you select some).
- **Far fewer spurious WinRM errors, false "went offline" blips, and no more "reboot the target" on a
  healthy box** — four changes cut the background WinRM noise on a Patching tab:
  - The "reboot pending" check no longer opens a fresh WinRM shell on every box on every 20-second pass; it
    runs on a single **~5-minute cadence for all boxes** (the cheap online/offline ping stays at 20s, and a
    just-rebooted box still re-checks promptly during its post-boot window). A box you patch in Vivre still
    reflects its pending state immediately.
  - A new **per-host shell cap** keeps several operations on the *same* box from stacking WinRM shells and
    tripping its `MaxShellsPerUser` limit — at most 4 at once, with slots reserved for what *you* click, so a
    scan / install / run-script never waits behind a background probe.
  - The monitor now **confirms a box is really gone before announcing it**: a single dropped ping under load
    reads as a transient "probe timed out (busy)", and only two failures in a row flip a box to "offline" —
    killing the false "Went offline → Back online" 20-50s flicker. (The Reboot & verify wave's own reboot
    detection is separate and stays prompt.)
  - The "WinRM shell couldn't start" message no longer guesses "reboot-pending" or tells you to reboot the
    box — it says plainly it's a temporary hiccup that's been backed off and will retry. Vivre only mentions a
    reboot for a box that genuinely *is* reboot-pending (which already shows the pill). Nothing here reboots
    anything.
- **"Reboot & verify…" now appears for any reboot-pending box, not just one you patched this session** — the
  right-click item is offered whenever a selected box shows the "Reboot pending" pill (it's keyed off the
  same signal now), so a box pending from a prior session, an app reopen, a re-scan, BatchPatch, or a manual
  patch all get it. It stays Patching-mode only, and clicking still routes through the same confirmed
  graceful-reboot-then-verify flow.
- **Reboot & verify no longer calls a slow reboot a failure, and only declares success on a REAL reboot** — a
  box that commits updates slowly on the way down (Server 2016 especially) can stay reachable for 15–20+
  minutes, which the wave used to mistake for "the reboot isn't taking". It now waits longer for
  staged/2016 boxes (and longer still after a forced reboot), treats "a shutdown is already in progress" as
  "it's going down — keep watching" rather than a failure, and confirms the reboot only when the box returns
  with a NEWER last-boot time — so a brief network flicker during reboot-prep is no longer mistaken for a
  completed reboot (which previously declared "committed in ~0 min" and then re-scanned a box that hadn't
  actually rebooted, leaving it stuck red). A box that's momentarily unreachable as it settles is retried
  rather than left on a stale error. Nothing auto-reboots — this only interprets a reboot you confirmed.
- **A box that finished installing is available again immediately** — it no longer stays marked "busy" (so
  Reboot & verify / a new scan no longer skip it) until the slowest box in the same batch finishes, and the
  fleet's Scan/Install buttons re-enable as boxes free up instead of staying disabled behind the whole batch.
- **A box rebooted outside Vivre clears its "Reboot pending" on its own** — if a box's pending reboot is
  cleared out-of-band (e.g. by BatchPatch) the pill self-clears on a later monitor poll (within a few
  minutes) instead of sticking until you manually re-scan.
- **Switching the Applicable/Installed view never blanks a row's terminal status** — toggling the scope
  radio no longer wipes the "Windows update message" for a box showing any terminal result (a failure, a
  successful "Installed N", a "Cleaned"/"Deferred" outcome) or one mid-operation; only a new scan/operation
  the operator starts replaces it. (It previously preserved error detail only; it now preserves successful
  results too.)
- **A Kerberos auth rejection no longer masquerades as "the remote session ended"** — when a target refuses
  the WinRM login with `0x80090322` (SEC_E_WRONG_PRINCIPAL), Vivre was reporting "Lost connection — the
  remote session ended (the target may have rebooted)", which is wrong (nothing dropped or rebooted — the
  login was refused) and could wrongly trip the reboot/self-heal path. It's now classified honestly as a
  Kerberos rejection so the diagnosis is accurate and the host is routed appropriately. Covered by tests.
- **The empty-state cards are truly centered** — the "Get started" card and the "No machines match
  this filter" state now sit dead-centre of the visible content area at every window width, in both
  Health and Patching. The long-standing top-left placement was a workaround for a "DataGrid width
  leaks into the layout" drift that runtime measurement disproved: a new env-gated layout probe
  (`VIVRE_LAYOUT_PROBE=1`, kept as a permanent diagnostic) showed centre-vs-viewport deltas of 0.0
  at four widths in both overlay states — and even at the old commit where the drift was originally
  "verified". That report was an artifact of the since-banned screenshot verification pipeline, not
  a real layout bug.
- **A dead host can no longer crash the app at fleet scale** — abandoning a connection attempt to an
  unreachable machine raced the PowerShell SDK's connection-retry, which then fired into torn-down
  transport state: a NullReferenceException on a raw background thread, which terminates the process
  (confirmed via the Windows Event Log at 318/319 of a 319-machine sweep). Two-part fix: WSMan
  connection retries are disabled (`MaxConnectionRetryCount = 0` — dead hosts now fail fast in ~20s,
  and the retry that raced disposal never exists), and abandoned connections/pipelines are no longer
  disposed while live — cleanup defers until the abandoned task settles. Covered by a regression test.
- **A hung host can no longer stall a sweep indefinitely** — the SCCM-health half of Check Vitals had
  no per-host timeout (now 60s), and the timeouts that existed only *requested* an abort while the call
  could wait minutes more for a zombie connection to acknowledge; timeouts now unblock the sweep
  immediately while teardown completes in the background. Worst case per dead host is ~3 minutes and
  the sweep always completes — no more sitting at 318/319.
- **Custom columns start filling immediately on big fleets** — the column fill shared one
  first-come-first-served budget with the vitals sweep and queued behind all of its rows (~2 minutes of
  blank columns on 319 machines). The budget now guarantees the column fill a small reserved slice
  (4 of the same 32 — the total cap is unchanged), so columns populate from the first seconds.
- **Patching no longer runs a phantom "Custom columns" pass** — every tab loaded the saved
  custom-column definitions, so Patching tabs ran real remote probes for columns their grid can't even
  display. The fill now runs only on Health tabs that actually have columns configured.
- **The amber sweep banner names the right actions per section** — Health reads "Ping & Check Vitals
  resume when finished" instead of borrowing Patching's "Scan & Install".
- **Install no longer defers on phantom "file-rename" reboots** — the update agent's pre-install servicing
  guard was the one remaining place counting `PendingFileRenameOperations`, so clicking Install flipped
  whole fleets to "Reboot pending (file-rename operations queued)" and quietly never started those installs.
  The guard now uses the five real servicing signals only (CBS in-progress / staged pending.xml / packages
  pending / CBS reboot / Windows Update reboot).
- **Download progress counts again** — the MB counter tracked a WUA byte counter that often sits at 0 for
  the whole download; it now derives from the per-update percent when that happens, and totals display as
  estimates ("3/~12 MB") since WUA revises them mid-download.
- **Deselecting a filter chip falls back to All** — unchecking the lit chip used to leave the old filter
  silently applied with no chip lit (an empty-looking grid when nothing matched).
- **Right cluster no longer clips at maximized** — the command-bar width was pinned from mistimed
  measurements (a window's resize event fires before its content re-arranges, and a one-shot startup
  correction never re-fired). The pin now follows the content area's own size change, which is always
  post-layout — correct on launch, maximize/restore, resize, and pane animations.
- **The bottom dock opens at a sane height** — it reopened at whatever height it last had that session with
  no cap, and a splitter drag pinned the machine grid to a fixed height so the dock could visually swallow
  it. It now opens clamped to 40% of the section, remembers your dragged height across sessions, and the
  grid row is re-asserted as flexible on every open/close.
- **The narrow toolbar stays icon-only when you select rows** — the selection swap's re-measure ignored the
  re-expand hysteresis and visibly expanded the collapsed bar; it now applies the same rules as a resize.
- **Scan all / Install all disable on an empty tab** — nothing to act on, so they no longer look clickable.
- **"Scan this machine" regained its Fluent styling** — its inline style lacked a `BasedOn` and wiped the
  button template.
- **The command bar no longer reads as a lighter band** — the workspace section now paints the same surface
  as the tab strip instead of letting the Mica backdrop show through.
- **Tab headers were dead to the mouse** — the ✕ close button, the right-click menu (close / close others /
  close all / rename), double-click rename, and middle-click close all did nothing in both Fleet strips: the
  tab header's keyboard-focus ring disabled hit-testing for everything inside it. Removed the offending
  attribute; all four interactions work again.
- **Reboot Pending column over-reported on every machine** — healthy boxes showed a pending reboot when
  none was due. The detection OR'd in `PendingFileRenameOperations`, which is populated by benign file
  operations (AV definition swaps, installer temp cleanup) and accumulates on long-uptime servers, so it
  fired almost everywhere. Reboot-pending detection now uses the reliable signals only — the CBS and
  Windows Update reboot keys plus SCCM's own `DetermineIfRebootPending` — so the column agrees with the
  ConfigMgr console. Fixed across all three probes (Check Vitals, SCCM health, and the
  monitor/force-reboot recheck).
- **Update-scan concurrency knob now actually works** — `MaxConcurrentScans` was defined but never
  wired to anything; the shared remote-read budget is now sized from it (default 32, unchanged in
  practice).
- **Scheduling an install no longer self-cancels on Stop** — hitting Stop right after a scheduled task
  was registered on a target used to silently unregister it; the schedule is now left in place and the
  outcome is reported.
- **Uninstalls show "Uninstalling"** in the status chip instead of the misleading "Installing".
- **CSV export is formula-injection safe** — values that start with `=` `+` `-` `@` (e.g. a software
  name read back from a target) are neutralised so a spreadsheet can't execute them on open.
- **Cross-Domain RDP tells you when a host has no saved credentials** — connecting now shows a dialog
  pointing at the right-click ▸ Edit… login fields, instead of silently doing nothing.
- **WhatsUp Gold no longer falsely reports its module as "not installed"** — two independent traps in
  the Windows PowerShell 5.1 shell-out were breaking the WUG check. (1) Vivre's in-process PowerShell 7
  runspace rewrites the process `PSModulePath` to PS7 folders, which a shelled-out 5.1 child inherited,
  so it couldn't see 5.1 modules; the child now starts with that variable cleared. (2) The temp script
  was written as BOM-less UTF-8, which PS 5.1 reads as ANSI — one non-ASCII character (an em-dash) made
  the whole script fail to parse and emit nothing; temp scripts are now written UTF-8 **with** a BOM
  (locked by a regression test). Both fixes are required; one alone doesn't fix it.

### Changed
- **Selection actions live in the command bar** — selecting rows transforms the bar in place (the
  Gmail / Explorer pattern): an accent **"N selected"** chip plus **Scan (N)** / **Install (N)** /
  **Clear selection** appear exactly where Scan all / Install all sat (those yield while a selection
  exists); Health mode shows the chip + Clear. The old floating selection bar — and the layout jump it
  caused — is gone entirely, so nothing moves or covers the grid when you select, and a double-click always
  lands on the row you clicked. The compact icon-only mode keeps the count visible; the status bar's
  "N selected" stays as ambient feedback.
- **Double-click opens Details** — double-clicking a machine row (Health and Patching) opens that machine's
  Details window; running scripts is deliberate and stays in the right-click menu only.
- **Saved lists carry the tab name** — Save tab as list… prefills the tab's current title, and opening a
  list names the tab after it. List files on disk are unchanged.
- **Settings reorganised for clarity** — "Integrations" (WhatsUp Gold server + Package library folder, each
  with plain-language helper text and a Browse… picker for the folder), "Help & about" (Info icon, inline
  version line), and a flat **Grid columns ▸ Manage columns…** row alongside the other top-level settings;
  the in-app guide follows the new paths.
- **Exclude updates is a proper dialog** — wrapped helper text, a live "currently excluded" list as you
  type, and leave-blank-to-clear (stated in the dialog).
- **Clicking empty grid space clears the selection** — Explorer-style; clicks on rows, headers, scrollbars,
  or buttons are unaffected.
- **A running vitals check announces itself** — an amber banner above the grid shows the live progress
  ("Checking vitals — 12/48 · 00:32 — Scan & Install resume when finished") for both the auto-check on
  load and manual Check Vitals, auto-dismissing when done; the grid stays fully usable underneath. The
  disabled Scan/Install buttons carry the same live narration as a themed tooltip on hover, so the held
  state is legible at a glance and in detail.
- **The shell now adapts to window width** — below ~1200 px the nav pane auto-collapses to the compact icon
  rail (Health / Patching stay one click away); widen again and it restores your last open/closed choice.
  The toolbar measures whether its labelled buttons genuinely fit and only then drops them to centred
  icon-only buttons (tighter-spaced, with full-label tooltips); the title bar slimmed to a 36 px band.
- **Frameless command bar** — toolbar buttons sit directly on the surface (icon + label, a subtle highlight
  on hover only), Task-Manager style; the Monitor toggle shows accent text on a faint fill when on instead
  of a solid box. The **…** overflow is gone: **Clear results** is a regular toolbar button and **Export to
  CSV** lives in the grid right-click ▸ Export.
- **Fleet ▸ Health and Fleet ▸ Patching replace the single Computers workspace** — the left nav now has
  a collapsible **Fleet** parent with two independent keep-alive destinations: **Health** (ping, vitals,
  SCCM actions — the former "Machines" mode) and **Patching** (Windows Update scanning and install — the
  former "Windows Update" mode). Each section has its own tab strip; switching between them Visibility-toggles
  the inactive strip without destroying it (the `TabControlEx` keep-alive is preserved). Ctrl+M now toggles
  between Health and Patching; the nav highlight follows. Health is the default on launch.
- **Mode chips removed** — the per-tab "Machines / Windows Update" radio chips are gone; mode is fixed by
  the Fleet section a tab belongs to (Health tabs are always in health mode, Patching tabs always in
  patching mode). The Get-Started card's "Switch modes" row now points at the Health / Patching nav items.
- **Menu bar removed** — File / View / Updates are gone; their items moved to where they're used: the tab
  right-click menu (New tab, Clear this tab, Rename, Close…), a **Lists ▾** toolbar button (open / save /
  delete named machine lists), the grid right-click ▸ Export (Export to CSV), an **Update options ▾** button shown in
  Patching (update source / drivers / exclusions), and an **Activity-log** toggle on the status bar. The
  title bar is now just the app title and the window controls.
- **Filter chips all carry icons** — the All / Updates / Done chips gained icons to match Reboot pending /
  Errors / Offline / Unhealthy, and **Remote credentials** on the Settings page is now a collapsible card
  like the other settings groups.
- **New left navigation** — the app now has a WPF-UI `NavigationView` pane (**Fleet** ▸ **Health** / **Patching** ·
  **Scripts** · **Cross-Domain RDP** · **Settings**), collapsed to icons by default with a hamburger toggle (remembered
  across launches). Theme
  (Light / Dark / System, a Windows-11-style "App theme" dropdown), session credentials, auto-check-on-load,
  WUG server / packages folder, and Help / About moved into a dedicated **Settings page**; the Settings and
  Help menus were retired from the menu bar (File and View stay). Switching sections keeps the workspace —
  and any live Cross-Domain RDP session — alive. The bottom status bar is now a full-width strip.
- **Cross-Domain RDP and Scripts are nav sections** — Cross-Domain RDP moved from the View menu into a left-nav
  destination (its live sessions stay kept-alive across nav switches), and a new **Scripts** section is a
  standalone library manager: browse the categorised PowerShell library, edit in a syntax-highlighted editor,
  and add / save / delete scripts. (Running scripts against machines is unchanged — still the grid's
  right-click ▸ Run Script.)
- **Computers workspace polish** — the command bar is now a single clean row; selecting machines raises a **contextual command bar** (Scan / Install scoped to the
  selection) rather than mutating toolbar labels; the workspace tabs gain modern browser-style headers with a
  right-click **Close other / Close all** menu; operation progress is reported in **one** place (the bottom
  status bar — a slim strip plus the live narration and counts) instead of three; and the **Settings** page
  groups set-once options into collapsible sections. The redundant View-menu Machines / Windows Update items
  were retired (the on-canvas chips and Ctrl+M remain).
- **Micro-interaction polish** — tab ✕ buttons gain a hover circle and tabs a keyboard focus ring (and middle-click closes a
  tab); right-click menus throughout pick up Fluent icons; scrollbars everywhere become thin Fluent overlay
  bars; the completion banner fades in, the fleet progress bar eases smoothly to each value, and the
  activity panel fades open; the Run Script category headers animate their chevron; and tooltips now name
  their keyboard shortcuts. Verified in light and dark.
- **Refreshed grid + dialog styling** — the machine and update grids now have structured, theme-aware
  column headers (a fill band, a separator under the headers, and sort arrows) with taller 36px rows and
  horizontal row dividers; the filter chips are taller with semantic icons; dialogs share consistent
  section headers, padding, and a three-level type ramp; and all three tab strips use one Fluent style.
  Works in both light and dark mode.
- **Status chips now adapt to the theme** — the machine status pills, the Vitals chips, the status dots,
  and the activity-log severity colours were fixed RGB (identical in both themes, with weak contrast in
  light mode); they now use theme-aware Fluent colours that stay legible in light and dark, and the
  actionable "Updates available" state stands out in the app accent. The activity log and the per-machine
  detail grids also pick up the Fluent control styling.
- **Sweeps narrate their progress** — instead of a silent spinner, a running sweep now shows the operation,
  count, and elapsed ("Checking vitals — 12/48 · 00:32") beside the progress ring and in the status bar.
  During an update run the fleet band adds elapsed + an N/M counter and holds open briefly after finishing
  so it no longer races the completion banner. The completion banner's colour now reflects the real outcome
  (green all-succeeded, amber partial, red all-failed) rather than guessing from the message text, and
  failing rows get a red error icon moved into view. The bottom status bar is reorganised into
  left / centre / right zones (context · active operation · summary).
- **New-tab "+" sits next to the last tab** — browser-style, instead of pinned to the window's far-right
  edge. With many tabs it scrolls with the strip (scroll right to reach it).
- **Multi-tab sweeps stay responsive and never freeze** — Check Vitals, update scans, software and
  custom-column reads across *all* open tabs now share one app-wide concurrency budget (≈32 hosts at a
  time) instead of each tab flooding WinRM on its own, so tabs fill in together (in waves) rather than one
  tab finishing entirely before the next starts. Each row's combined health+vitals pass also holds a
  single slot end-to-end — fixing a stall where a second tab would show "health unavailable" and then sit
  idle until the first tab's vitals had *all* completed. Activity-log and completion-toast updates are
  marshalled to the UI without blocking, so a heavy multi-tab sweep no longer stutters or freezes the
  window.
- **The two "Scheduled task" columns were retired** — what's queued and when now reads inline in the
  update message instead of two extra columns.
- **Consistent dialog sizing across every popup** — modals centre on their owner, fixed forms size to
  their content with sensible min/max, and content-heavy/list dialogs are resizable with their action
  buttons always visible (outside the scroll region), so nothing clips on smaller screens.
- **Repo and publish output moved out of OneDrive to `C:\src\Vivre`** *(dev/build infrastructure)* —
  closes a class of stale-binary bugs where OneDrive's cloud-placeholder copies shipped old code while
  it looked fresh, plus a `.git` worktree lock and CRLF/LF churn. A `.gitattributes` (`* text=auto`)
  keeps line endings stable.

### Added
- **Empty-state guidance + mode chips** — a fresh tab now shows a "Get started" onboarding card (paste
  names → ping / check vitals → switch modes, with an Open-help link) instead of a blank grid; the
  activity log shows "No activity yet" until operations run; the Cross-Domain RDP pane prompts you to add
  or connect to a host; and filtering to no matches shows "No machines match this filter" with a Clear
  button. New **Machines / Windows Update mode chips** in the filter bar give the current view an on-canvas
  switch (the View menu and Ctrl+M still work). Verified in light and dark.
- **Grid right-click menu, regrouped** — per-machine actions are now clustered (Run script ▸ · Client
  actions ▸ · Software ▸ · Export ▸ · Schedule ▸) for easier scanning, and **Export ▸ Shown rows + columns
  (CSV)…** puts the full grid export — filter-aware and including any custom columns — on the right-click,
  not just the software report. Same export as File ▸ Export to CSV.
- **Cross-Domain RDP — embedded remote-desktop manager** — a tab (opened from View ▸ Cross-Domain RDP,
  beside your machine tabs) with a foldered tree of hosts and live, tabbed RDP sessions, full-screen, and
  saved per-host/per-folder credentials (Windows DPAPI, per user). Built on the Microsoft RDP ActiveX
  control, so it reaches hosts on other domains — turn NLA off per host if a login is rejected.
  Right-click or drag in the tree to organize; sessions stay connected when you switch tabs.
- **Windows Update lane** — scan, install, and uninstall updates per machine with live progress,
  driven by a compiled SYSTEM agent. Update Source toggle (Windows Update / Microsoft Update /
  Managed-WSUS) + an exclude-by-name list. (See `docs/windows-patching-lane.md`.)
- **Grid filter + state chips** — a filter bar on both views: search machines by name and one-click
  quick filters (Updates available · Reboot pending · Errors · Offline · Done), with a live
  "Showing N of M" count. Filters the whole tab and updates mid-sweep (a row that errors appears
  under the Errors chip automatically).
- **Select shown** — one click selects every row the filter currently shows, so you can act on just
  that subset (e.g. filter to Errors → Select shown → Install) without hand-picking rows.
- **Export to CSV** (File ▸ Export to CSV…) — writes the rows currently shown (respecting the filter)
  to a CSV report (machine · online · state · updates · reboot · error · OS · schedule) for a
  maintenance-window write-up / ticket.
- **Pre-install reboot-pending check** — Install first checks the targets: if any already have a
  reboot pending (which can jam WinRM and fail the install), it offers to **reboot those first**,
  **install anyway**, or **cancel** — heading off the WinRM-unhealthy failure instead of reacting to it.
- **Browser-style tab menu** — right-click a tab for **Close other tabs** and **Close tabs to the
  right** (alongside Rename / Close tab); a single confirm covers any tabs that still have work.
- **WhatsUp Gold maintenance mode** — right-click ▸ *WhatsUp Gold maintenance…* puts the selected
  machines (or all in the tab) into/out of WUG maintenance via the `WhatsUpGoldPS` module: pick
  Enter/Exit, enter the WUG server + login, and it maps the names to WUG devices and sets it. The
  WUG credential is prompted each time and never stored (only the server is remembered); it runs
  locally against the WUG server (not on the targets), auto-installs the module for the current user
  if it's missing, and surfaces a clear reason at every step if something fails.
- **How to use Vivre** guide (Help ▸ How to use Vivre, or F1) — a searchable, collapsible in-app
  manual covering both Machines and Windows Update modes, grouped into Getting started / Machines /
  Windows Update / Tips & shortcuts / Troubleshooting; the search filters and auto-expands matches.
- **Schedule ▸ menu** (right-click) — schedule a one-time **install** or **reboot** at a chosen
  time, or **Cancel** a pending scheduled task. Works in either view; the "Scheduled task" columns
  show what's queued.
- **Reboot (force now)** — right-click action on the selected machines, with confirmation.
- **Per-machine detail window** (right-click → Details…) — OS (caption + build), full update state,
  and that machine's activity-log messages; **Show messages** filters the activity log to one machine.
- **Keyboard accelerators** — Ctrl+T/W/L, F2, F5, Ctrl+M, Ctrl+Enter, and Shift+F10 for the
  right-click menu. **Theme** (Light/Dark/System) is now persisted across launches.

### Changed
- Machines ↔ Windows Update mode is selected from the **View** menu (direct items).
- Status is shown by colour **and** shape (glyphs), never colour alone; the activity log gained a
  severity glyph.
- Run Script now opens the grouped Run Script window instead of a deep cascading menu.
- Confirmations added for the irreversible/production actions (fleet install, uninstall, reboot,
  large delete, closing a tab with work, replacing a loaded list); routine actions stay one-click.
- Toolbar reordered so the machine buttons don't shift when switching modes; the tab strip scrolls
  when there are more tabs than fit.

### Fixed
- **From the code review (`REVIEW_FINDINGS.md`):**
  - Uninstall could remove the **wrong** update: the DISM fallback matched a KB against installed
    packages by bare substring (`KB5000` matched `KB5000802`). It now matches the KB as a whole token.
  - DISM package enumeration could **deadlock** the SYSTEM worker if DISM wrote a lot to stderr; both
    DISM calls now drain stderr concurrently.
  - A failure writing the activity-log file could **throw out of a caller's catch block** and bury the
    original error; the file write is now isolated (the in-memory entry is the source of truth).
  - The update agent could exit with **no error line** if a transient file lock hit its progress write;
    the write now retries briefly and never throws.
  - The streaming PowerShell output collection is now disposed (was leaking a wait handle per remote call).
  - The update agent now fails with a clear "config was empty or malformed" message instead of a bare
    NullReferenceException when handed an unreadable config.
  - Regression coverage added for the load-bearing WUA paths: the install/uninstall streaming
    controller (heartbeat filtering, watchdog, typed-exception handling, user-cancel), per-host
    serialization release on fault, the cross-framework agent-config JSON contract, and DISM
    exit-code translation. No behaviour change — these lock the existing behaviour in.
  - The monitor's reboot probe no longer swallows a persistent failure silently — a lost session or
    sustained error now backs off and is logged once (matching the WinRM-unhealthy path), instead of
    leaving the row dark with no trace.
  - Removing machines (Remove Offline / Delete) now prunes their per-host monitor state, so stale
    name-keyed entries don't linger or affect a later re-add.
  - The fatal-error handler writes straight to the log file instead of through the shutting-down UI
    dispatcher, so the one line naming a fatal crash isn't lost during teardown.
- The Windows Update agent is now **hash-verified on the target** (SHA-256) before it runs as SYSTEM,
  so a tampered/replaced binary in the shared temp dir is caught instead of executed.
- Monitor/reboot-probe updates now consistently marshal to the UI thread, and the local-vs-remote
  host check is centralised in one helper (was copy-pasted across five files).
- Remoting failures no longer leak raw SDK strings — they're translated to clear, host-named
  messages ("Lost connection to …", "WinRM unhealthy — reboot the target", "No response from …").
- The monitor no longer hammers reboot-pending / degraded hosts (the cause of WinRM/PSRP poisoning);
  a degraded host self-heals once WinRM responds again (re-tested every few minutes).
- Hung or dead install sessions are caught via heartbeat silence (~90s) instead of freezing on stale
  progress — and a genuinely slow update is never falsely flagged.
- Uninstall surfaces the real per-KB reason (e.g. `0x800F0825` permanent package) and reports an
  all-failed run as an error rather than a green "Done"; cumulative updates are correctly reported
  as non-removable.
- Copy ▸ \<field\> copies the right-clicked row, not a stale multi-selection.
- The Update grid's "Reboot message" and "Windows update message" cells no longer butt together
  (they had no gutter, so trimmed text read as one run-on sentence) — added a right-hand gutter.
- The scan-complete summary counts machines that actually have updates, not every scanned machine.
- Settings no longer silently clears a stored credential when the username is left blank.
