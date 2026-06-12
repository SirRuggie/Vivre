# Vivre — Kerberos / SMB-DCOM work: overnight status

**TL;DR:** The entire *detect → route → score* foundation is built, code-reviewed (adversarially), and **verified green** — 243 Core tests pass, Vivre.Desktop builds 0 warnings / 0 errors, and the update agent is Authenticode-signed. The one part that physically needs your fleet to validate — the **SMB execution lane** (drop+launch+read-back of the patch agent) — is fully designed and ready to build the moment you run the 5-minute **Phase 0 pilot** below. I did **not** build that lane "blind" overnight, because (a) it can only be proven against a real failing box, and (b) it edits the most reliability-critical code in the app — the responsible move was to verify everything that doesn't need the fleet and gate the rest on your pilot. Nothing is committed; it's all staged on disk for your review.

---

## What's done & verified (no fleet needed)

| # | What | State |
|---|------|-------|
| 1 | **Honest Kerberos error.** `0x80090322` (SEC_E_WRONG_PRINCIPAL) is now classified as a distinct `KerberosWrongPrincipalException` *before* the generic transport branch — so it stops mislabeling as *"the remote session ended (the target may have rebooted)"* and no longer wrongly trips the reboot/self-heal logic. | ✅ tested |
| 2 | **Automatic transport routing + per-host cache.** A `RoutingPowerShellHost` decorator wraps the WinRM host behind the existing seam (zero call-site changes — every feature inherits it). On `0x80090322` it records the host as **SMB/DCOM** and never re-pays the ~20s doomed WinRM connect again that session. Healthy boxes stay on the fast WinRM primary. Thread-safe under the 32-way sweep (concurrency races fixed in review). | ✅ tested |
| 3 | **Vitals flags the Kerberos problem + scores it.** A Kerberos-broken box now surfaces in **Vitals** as *"WinRM/Kerberos auth failing (0x80090322) — fix the host's AD-side Kerberos (SPN / encryption-type / re-sync if snapshot-reverted)"*, docks the health score, and sets a `NeedsAttention` flag — **without** ever putting "fell back"/"degraded" wording on an operation result (your hard rule). Wired end-to-end: `VitalsProbe` catches the rejection and returns a blank-but-flagged snapshot, so the scorer actually fires. | ✅ tested |
| 4 | **Update agent is code-signed.** Self-signed cert created (`CN=Vivre Update Agent (self-signed)`, thumbprint `1A5CE867A4660C271C9C7AA0DD2F923A1FE05953`, valid to 2031); the agent build signs it automatically (best-effort, no network/timestamp). Verified signed + bundled beside `Vivre.exe`. | ✅ verified |

**Verification:** `dotnet test Vivre/source/Vivre.slnx` → **243 passed, 0 failed**. `dotnet build Vivre.Desktop.csproj` → **0 / 0**. Per your standing rule, I verified by build + tests + code review only — **no screenshots / no foregrounding the window**. Your visual check is the one thing left for these UI-adjacent bits (see "Decisions").

**How a Kerberos-broken box behaves *today* with this code** (even before the SMB lane): it stops showing the misleading "session ended", it's only probed over WinRM **once** (then cached), and in Vitals it shows **"needs attention — fix Kerberos"** with the recommended fix and a docked score, instead of a bare "vitals unavailable". Scan/Install on such a box still *fails* honestly (the SMB execution lane below is what makes them *succeed*).

---

## What's deferred — and exactly why

These are the parts that **need the Phase 0 pilot** (they execute against a real failing box) and/or touch the load-bearing patch lane. All are fully designed; the design doc lives in the workflow output. I deliberately did not build them blind.

- **The SMB execution lane (the BatchPatch-equivalent: Scan + Install over SMB).** Drop the signed agent over `\\host\C$` → launch it as SYSTEM via the SMB Service-Control-Manager pipe → agent does the real Windows Update locally → read progress back by tailing its JSONL over SMB. The adversarial review found three things the naive plan gets wrong that I've folded into the spec: the install selector must live in `WuaUpdateLane` (not the decorator — an install is multi-call); the agent must emit a **periodic heartbeat** or a long quiet install false-times-out; and teardown needs a real **stop → wait → DeleteService → delete dir** sequence (Windows services don't auto-delete on stop).
- **DCOM vitals reader.** So a Kerberos-broken box shows *full* vitals (not just the flag) gathered over DCOM/WMI. Gated on the Phase 0 DCOM check (informational).
- **ACL'd agent drop dir.** Move the agent drop off world-writable `C:\Windows\Temp` (a TOCTOU privesc) to an Administrators/SYSTEM-only `C:\ProgramData\Vivre\agent`. Bundles with the SMB lane (both need it).
- **net462 agent retarget.** Tried it; reverted. net462's BCL lacks `ValueTuple` (the agent uses tuples) — would need an extra DLL (breaks the single-EXE drop) or a tuple refactor of reliability-critical code I can't behaviorally test overnight. **net48 retained** (proven in your fleet; 4.8 is WU-deployed). net462 is a clean follow-up if you ever need guaranteed Server-2016-inbox.
- **`NeedsAttention` UI badge.** The flag is computed + the fix is already in the Vitals "why this score" list; a dedicated badge is a small UI add I left for your visual design (can't verify it without a screenshot).

---

## ▶ Phase 0 pilot — run this FIRST (5 min, the GO/NO-GO for the SMB lane)

Run on **one** Kerberos-broken box (e.g. `APVVISIONF5`) **as your current login — no `-Credential`**. `GATE A` passing (probe file appears **and is owned by SYSTEM**) is the green light to build the whole SMB lane. `INFO B` is informational (decides whether vitals read over DCOM or over the agent).

```powershell
$h = 'APVVISIONF5'   # a box that fails WinRM with 0x80090322 and where  Test-Path \\$h\c$  is True

# --- GATE A: SMB Service-Control-Manager create/start/delete as SYSTEM (BatchPatch's exact channel) ---
$leaf  = "vivre_pilot_$(Get-Random).txt"
$local = "C:\ProgramData\$leaf"
$unc   = "\\$h\C$\ProgramData\$leaf"
sc.exe \\$h create VivrePilot binPath= "cmd /c echo ran-as %USERNAME% > `"$local`"" type= own start= demand obj= LocalSystem
sc.exe \\$h start  VivrePilot
Start-Sleep 3
if (Test-Path $unc) {
    "GATE A PASS — service ran. Probe says: $((Get-Content $unc).Trim())  | file owner: $((Get-Acl $unc).Owner)"
    # owner MUST be NT AUTHORITY\SYSTEM — that proves it ran as SYSTEM (WUA install needs that).
    Remove-Item $unc -Force
} else { "GATE A FAIL — probe not found; the SMB-SCM launch did not run." }
sc.exe \\$h delete VivrePilot

# --- INFO B: DCOM/WMI on the current login (decides vitals-over-DCOM vs vitals-over-the-agent) ---
try {
    $s = New-CimSession -ComputerName $h -SessionOption (New-CimSessionOption -Protocol Dcom) -ErrorAction Stop
    "INFO B PASS — DCOM/WMI works on your login: $((Get-CimInstance Win32_OperatingSystem -CimSession $s).Caption)"
    Remove-CimSession $s
} catch { "INFO B — DCOM on current login did NOT work; vitals will read via the agent over SMB. ($($_.Exception.Message))" }
```

- **GATE A PASS** (file appears, owner = `NT AUTHORITY\SYSTEM`) → the architecture is sound; I build the SMB lane next.
- **GATE A FAIL** → tells me svcctl is restricted; the fallback is Task-Scheduler-over-`atsvc` (same SMB channel) — a localized change.

---

## The path forward (once GATE A passes)

1. **ACL-harden the drop dir** (`C:\ProgramData\Vivre\agent`) on the existing WinRM path too. *(verifiable)*
2. **Build `SmbAgentLane`** with the four verbs (SMB copy / SCM launch-as-SYSTEM / client-side UNC progress tail / SCM stop-wait-delete cleanup), the agent **heartbeat**, and injectable seams so it's unit-tested. *(unit-verifiable; live-verified by your pilot box)*
3. **Wire the lane selector** into `WuaUpdateLane`/`PatchService` (install re-dispatch lives here, not the decorator).
4. **DCOM vitals reader** (if INFO B passed) so Kerberos-broken boxes show full vitals.
5. Docs + a single clean operation result on the pilot box, end to end.

---

## Decisions to weigh in the morning

- **Trust the signed agent fleet-wide?** Optional: push the self-signed cert to Trusted Publishers via GPO so the signature reads "Valid" rather than "untrusted publisher". Not required for it to function.
- **Vitals penalty weight = 12.** Docks a pristine Kerberos-broken box to 88 (still green) but tips it to Warning the moment anything else co-occurs; the fix text always shows in the reasons. One constant to raise if you want these boxes amber on sight. *(Want a dedicated red "needs attention" badge regardless of band? That's the `NeedsAttention` UI wire-up.)*
- **net462 agent** for guaranteed Server-2016-inbox — worth it, or is 4.8-via-WU fine? (Needs the tuple refactor.)

---

## Honest boundary

I will not claim the SMB lane "works on your boxes" — it isn't built, precisely because it can't be validated without your fleet. What I *can* stand behind: the detect/route/score foundation **compiles, is tested (243), is adversarially reviewed, and is logically correct**, and the SMB lane is fully specified and de-risked. Run GATE A and I'll have the execution lane built and unit-tested next, ready for you to prove on the pilot box.
