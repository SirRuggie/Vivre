# Vivre — 2016 LCU lane red-team review (the de-risking rationale)

> **Project knowledge note:** The adversarial review of the 2016 LCU design. Captures the traps that bite on box #7 of 30. Reference before changing the install/verify/reboot logic.

## Verdict

The mitigation is sound even though part of the diagnosis was overstated. Going full-package + DISM is robust *because it sidesteps the Express delta-assembly machinery entirely* — so it works regardless of which sub-cause is actually biting. You don't have to be 100% right about *why* Express fails for the full-package fix to be correct.

**Sequencing correction:** the LCU lane builds **on top of** the SMB/DCOM Kerberos lane, NOT alongside it. The new lane *consumes* the SMB delivery + run-as-SYSTEM mechanism and the DCOM reboot/UBR-read channel. Pilot SMB/DCOM first, then build LCU on the proven foundation.

## Root-cause cross-check

- **Holds up:** the CBS DPX failure (mqsec.dll not in the .psf → 0x80070002) is the *true* failure — a staging/assembly failure, not a network failure. `0x80240034` is just the top-level symptom WUA reports.
- **DO absence is a red herring** for this failure: BITS moves 1.7 GB fine; size isn't the problem. The .NET CU succeeded because its deltas were all present in its PSF. The OS CU failed at pre-stage extraction because one delta was missing. The full-package lane bypasses WU + DO + Express entirely, so it's immune either way. **Do not spend effort restoring DO.**

## The traps (folded into the build)

1. **SSU prerequisite — the #1 trap.** Check the catalog: combined SSU+LCU, or LCU-only needing a separate SSU first? If separate and skipped, DISM can silently no-op or half-apply across the fleet. *(This cycle: confirmed COMBINED — risk cleared.)*
2. **DISM exit codes — don't treat nonzero as failure.** `0` = success; **`3010` = success, reboot required** (the normal case); `1641` = success, reboot initiated; `0x800f081e` (CBS_E_NOT_APPLICABLE) = already installed = **green**; `0x800f0922` = real fail. A naive `if exit ≠ 0 { fail }` marks every successful box as failed.
3. **Rollback looks like a successful reboot.** A 2016 CU that fails during the offline commit rolls back and the box returns at the OLD build (9060) — pingable, services up, but **failed**. UBR == target verify is MANDATORY. "Box back ≠ box done."
4. **Progress capture from `dism.exe`.** Original advice was "use `Add-WindowsPackage`, not raw `dism.exe`" — **but the as-built decision went the other way:** the agent runs raw `dism.exe /add-package` and handles the `\r`/backspace progress bar by reading stdout in 256-char chunks and regex-extracting the latest percent. The cmdlet's cleaner stream wasn't worth the extra dependency once the chunk-read parser worked (and the agent already expands `.msu`→`.cab` itself).
5. **Disk pre-check ≥ 8 GB.** 1.7 GB download + ~1.7 GB expanded cab + WinSxS growth on 60-80 GB system drives. Copy local + hash-verify + install from local + delete — never DISM from a UNC path (a network blip mid-extract can corrupt the apply).
6. **Reboot-readiness signals = SAFE not FAST.** TrustedInstaller=Stopped + TiWorker quiet + CBS\RebootPending present makes the reboot safe (no 2-hour Stopping hang), but the offline commit can still be 30-60 min. Slow ≠ hung. Also: RebootPending present doubles as the "stage actually succeeded" signal — absent after a stage = silent no-op, don't mark ready. **As-built note:** the agent gets TI/TiWorker quiescence implicitly (its `dism /add-package` runs synchronously, so the transaction is complete on return) and gates readiness on the single `CBS\RebootPending` check — it does not separately poll the two service signals; the effect is equivalent.
7. **Dead-box detection.** You can't read progress on a box that's down, and "ping responds" ≠ "done." Wave state machine: issue reboot → confirm it goes offline (proves reboot fired) → wait for return → run Verify. Returns + UBR=target → green; returns + old build → rolled back (red); never offline → reboot didn't take (red); offline past ~90 min ceiling → flag for console/iLO. Be honest in the UI about that ceiling.

## Conflicts with existing lanes

- **Reuse the `PatchService` `_inFlight` guard.** CBS serializes TrustedInstaller — can't run a WUA install and a DISM add-package on the same box at once. The LCU lane claims the host through the same per-host guard; don't invent a second registry.
- **The 1.10.0 timeout/watchdog will false-positive — RESOLVED.** Those per-host timeouts assumed minutes; a DISM stage is 30-60 min. Fixed: `PatchOptions.PerHostTimeout` is now 3 h, the 2016 Cleanup passes `InfiniteTimeSpan`, and the agent heartbeat flows during long DISM phases so the watchdog never declares a healthy box dead. (Per-host timeout/watchdog detail now lives in windows-patching-lane.md.)

## Late-return / UBR-decides rule (the most important wave behavior)

The clock NEVER writes a box off — only the UBR check decides pass/fail. Two separate timers: the "go offline" window (reboot escalation only — 8 min for the Default preset; **20 min via `ForSlowCommit` for staged-2016 boxes**, whose CBS commit holds the network longer; the post-force wait is 2× either) and the 90-min "offline commit" ceiling (a flag, not a stop). A box offline > 90 min that then returns is re-detected and Verified; it's never stuck "timed-out" while actually up. On return, Verify retries through "can't read registry yet" (box mid-boot) — never a false red. Verify is also a standalone button, so no box is ever stranded by a timer.
