# Vivre — Server 2016 Patch Pipeline ("2016 LCU Lane"): canonical spec

> **Project knowledge note:** The canonical spec for the Server 2016 patching lane — root cause, scope, and build requirements. Reference this for any change to the LCU lane.

## Background — the problem

~30 Server 2016 boxes (incl. APVVISIONB-F3, APVVISIONB-SQL2) chronically fail to install monthly/quarterly OS Cumulative Updates through normal Windows Update, and have for multiple quarters.

This cycle's update: **KB5094122** (June 2026 CU, target build **14393.9234**). Boxes are at 14393.9060 (April 2026). DISM CheckHealth/ScanHealth clean, disk/network/time healthy, no WSUS, SentinelOne ruled out (tested disabled).

**Root cause chain (confirmed via WindowsUpdate.log + CBS.log):**
1. WU delivers the CU as an **Express** package (deltas, not the full file). WUA fails `0x80240034` (WU_E_DOWNLOAD_FAILED).
2. CBS.log shows the real failure: during staging, DPX extraction of `mqsec.dll` fails — "not present in the container (Windows10.0-KB5094122-x64_4.psf)" → `0x80070002 ERROR_FILE_NOT_FOUND` → "Failed to pre-stage package." The Express assembly pipeline drops pieces. (`mqsec.dll` = MSMQ security DLL; its delta is absent because the box's current version has no Express delta path — full package ships the whole file, so it doesn't care.)
3. Delivery Optimization is physically absent (DoSvc/dosvc.dll stripped from the golden image) — RED HERRING for this failure (BITS moves big files fine; the failure is delta *assembly*, not download).
4. Server 2016's servicing engine is slow. Installs have online (OS running) + offline (commit during reboot) phases. The 2-hour "TrustedInstaller stuck in Stopping" hangs happen when reboots are triggered while the online phase is still finalizing.

**Fix = full-package + DISM, bypassing Express/DO/WU entirely** — robust regardless of the exact sub-cause.

**Connectivity constraint:** On the Vision servers the `HTTP/<hostname>` SPN is owned by the Deltek service account (Deltek7xBSQL) for SSRS, so WinRM Kerberos to the hostname fails with SEC_E_WRONG_PRINCIPAL. Agent comms use Negotiate/NTLM, SMB/scheduled-task delivery, or a management alias — never assume Kerberos WinRM works on these boxes. (The LCU lane rides the SMB/DCOM transport for this reason.)

## Scope — HARD-GATED to Server 2016 (CurrentBuild == 14393) ONLY

All other OS versions keep existing Vivre behavior unchanged.

1. **Full-package primary lane** (`FullPackageLcuLane`, alongside `WuaUpdateLane`): for 14393 boxes, do NOT install the OS LCU via WUA/Express. Instead: identify the month's CU KB from WUA scan metadata → fetch/use the full package from the package folder → **expand the `.msu` to its `.cab`(s) with `expand.exe`** (online DISM on Server 2016 rejects `.msu` payloads with `0x80070032`) → install each cab **SSU-first via `dism.exe /add-package`**. **As-built note:** the earlier "use `Add-WindowsPackage`, not raw dism.exe" plan was dropped — the agent runs `dism.exe` and reads its `\r`-drawn progress bar in 256-char chunks (see the red-team review, trap 4). .NET and small updates stay on the existing WUA lane.
2. **Everything operator-initiated. NOTHING runs automatically.** Four manual actions with confirmation: Component Cleanup, Stage, Reboot Wave, Verify.
3. **Component Cleanup:** `DISM /Online /Cleanup-Image /StartComponentCleanup`. Refuses if a reboot is pending or servicing is in progress. Tracks last cleanup date per box. **Access-denied is success-with-caveat, not failure:** if the cleanup clears the backlog but can't delete a locked remainder (AV/EDR holding WinSxS handles → DISM exits 5 / `0x80070005` in CBS), the agent runs a read-only `AnalyzeComponentStore`, parses the reclaimable-package count, and emits those raw facts; the pure `ComponentCleanupClassifier` (Vivre.Core) reclassifies it to **CleanedFilesLocked** → the neutral *"Cleaned · locked files (see log)"* row (green, never red) with the explanation in the activity log. The agent builds no user-facing strings (facts only) and adds no reboot path. Any other non-zero exit is still a genuine failure, surfaced unchanged.
4. **Stage (daytime step):** copies/downloads the package, runs the DISM install with no restart while the server keeps serving, then polls until genuinely reboot-ready (TrustedInstaller=Stopped, TiWorker quiet, `CBS\RebootPending` present). Marks "Staged — run Reboot Wave." NEVER reboots.
5. **Reboot Wave (night step):** operator selects boxes, reboots them, tracks the offline commit by elapsed time vs learned per-box average.
6. **Progress UI:** real progress, not spinners. Phases: Downloading → Hash-verified → Staging (x%) → Staged/Reboot-ready → Rebooting (elapsed) → Verifying → Done/Failed. Show elapsed alongside percent (DISM percent stalls on 2016).
7. **Post-reboot verify:** confirm UBR equals the target (9234 this cycle), key services running, optional role checks → green/red per box.

## Package handling (settled)

- Package folder: `C:\Vivre\VivrePackages` (configurable in Settings).
- **Verify-and-prompt, auto-fetch OFF.** Vivre checks the folder for the correct + newest package (right KB/arch/size/hash). If missing or wrong, it prompts with the catalog link and does NOT continue until the correct package is present. (Auto-fetch from the catalog is a later convenience layer with three guards: strict title/arch/KB filter, hash+size verify, manual-drop fallback.)
- Target UBR: operator confirms per cycle (9234 this month).
- This cycle's catalog fact: KB5094122 is a **COMBINED SSU+LCU** (no separate SSU — half-apply risk cleared). Package: "2026-06 Cumulative Update for Windows Server 2016 for x64-based Systems (KB5094122)", ~1744 MB.

## Mixed-fleet "Install all" routing (settled)

One button, auto-routes per machine. **As-built (the staged-patching toggle made the 2016 lane opt-in):** a 2016 box goes to the LCU lane (stage-and-stop) **only when the operator has flagged it** (`RequiresStagedPatching`); an unflagged 2016 box falls through to the WUA lane exactly like a 2019+ box. 2019+ → existing WUA lane (one-step). Unknown/unscanned OS → skipped with "Check Vitals first" (fail-safe — never mis-routed). When a flagged-but-not-staged 2016 box is in the target set, Install shows the **`StagedInstallDecisionDialog`** (Stage CU first / Install minor updates only / Cancel; Cancel skips only the flagged boxes); a box already at the target UBR this session (`LcuVerifiedThisSession`) skips the dialog, and the Stage step is gated on a scan-this-session. Result is a two-bucket summary ("N installed · N staged, awaiting Reboot Wave · N failed"); staged 2016 rows read as action-needed, visually distinct from green-done and red-failed.

## Build sequencing (the proven order) — ALL SHIPPED

All four steps are complete and on `master`:

1. Pilot SMB/DCOM lane on one Vision box (DONE — Gate 1 passed).
2. `FullPackageLcuLane` on the proven transport (DONE).
3. Mixed-fleet "Install all" routing (DONE — now flag-aware per the opt-in toggle, see above).
4. Beta-validated end to end (APVVISIONB-F3 / -SQL2 / APATRC-WS1 reached 14393.9234).
