# DCOM 1191 → SMB/SCM fallback — case file

> **Point-in-time record, never edited after the fact.** Written 2026-07-27 against `master` @ `f693310`
> (clean tree, release 1.16.4). It captures what was proven, what was analysed, and what was still
> unknown *on that date*. Later work does not get retro-fitted into it — if the code changes, this file
> stays as written and the new truth goes in `CHANGELOG.md` / `docs/vivre-backlog.md` / a new case file.
>
> **Status at freeze: INVESTIGATION ONLY. No code was changed.** The fix direction below is
> operator-approved but unbuilt; every line-number citation is against `f693310`.

---

## 1. The symptom

SentinelOne raised 100+ "Lateral Movement" detections across the fleet (APVVISIONF4, APVSP19APP,
APVSCERISEML1, AZRVISIONST-SQ1, APVMANHOURDEV, NYC-FP1, APVVISIONW1) whenever the Reboot Wave ran.
The S1 process trees showed REMOTE creation of `cmd.exe /C shutdown /r /t 5 /c "Vivre Reboot Wave"`
→ `shutdown.exe`, plus a Service Create indicator naming `Vivre_Reboot_<32 hex>`.

Detections fired only on the operator's explicit reboot click — the reboot cardinal was never in
question. The question was *why Vivre was using that channel at all*.

---

## 2. PROVEN EVIDENCE (established fact — not re-derived in this pass)

### 2.1 Live tests (operator, NYC-FP1, Windows Server, `admin_sbridges` session)

1. Active RDP session → `Win32Shutdown(2)` returned **1191**
2. Active RDP session → `Win32ShutdownTracker(300,"...",0,2)` returned **1191**
3. Active RDP session → `Win32Shutdown(6)` returned **0**, box rebooted
4. DISCONNECTED session → `Invoke-CimMethod Win32Shutdown Flags=2` over DCOM returned **1191**
5. `Get-CimInstance` over DCOM **succeeded** → DCOM transport is healthy; this is not hardening/firewall

**1191 = `ERROR_SHUTDOWN_USERS_LOGGED_ON`. The force flag (4) bypasses it. The Tracker does NOT bypass it.**

### 2.2 Vivre logs (6 files, 2026-07-22 → 07-24)

6. **61 "DCOM accepted", 38 "DCOM failed" — ALL 38 are code 1191. Zero other error codes exist.**
7. **100% of `forced=True` dispatches were accepted over DCOM. Zero forced failures.**
8. **The SMB/SCM fallback FAILED TO REBOOT on 3 of 6 sampled boxes** (APVMANHOURDEV, APVEQUISWEBDEV1,
   APVSNMIDDEV1): `WaitForOffline(graceful) result=window-expired` at 8 min, uptime proof shows
   `LastBoot` unchanged. **Only the forced DCOM escalation actually rebooted them.**

### 2.3 SentinelOne control experiment (APVMANHOURDEV, 2026-07-22)

9. 08:59:31 SMB/SCM fallback → **2 threats raised** (Identified 08:59:30–31).
10. 09:07:32 forced DCOM reboot → **ZERO threats. Forced DCOM is invisible to S1.**

---

## 3. Root-cause chain (settled)

1. The wave issues its first reboot **graceful** — `RebootWave.cs:173`:
   ```
           RebootDispatch graceful = await IssueRebootAsync(forced: false).ConfigureAwait(false);
   ```
   This is the **only** `forced: false` call site in the repo. `RebootWave.cs:228` and
   `ForceRebootRunner.cs:81` both pass `forced: true`.

2. Graceful maps to `Win32Shutdown` flags **2** — `DcomRebootTrigger.cs:123`:
   ```
           int flags = forced ? EwxReboot | EwxForce : EwxReboot;
   ```
   with `:27-28`:
   ```
       private const int EwxReboot = 2;
       private const int EwxForce = 4;
   ```

3. On a box with any logged-on session, Windows refuses flag 2 with **1191**. Vivre reads the code at
   `DcomRebootTrigger.cs:144-155`:
   ```
                       using CimMethodResult result = session.InvokeMethod(@"root\cimv2", os, "Win32Shutdown", inParams, cimOptions);
                       object? rv = result.ReturnValue?.Value;
                       uint code = rv is null ? 0 : Convert.ToUInt32(rv);
                       if (code == 0)
                       {
                           return (true, false, string.Empty);
                       }

                       // 1115 = a shutdown is already in progress → the box IS going offline; not a failure.
                       return code == ErrorShutdownInProgress
                           ? (false, true, "Win32Shutdown: a shutdown is already in progress (1115)")
                           : (false, false, $"Win32Shutdown returned {code}");
   ```
   **1115 is the only named code. Everything else non-zero collapses into `(false, false, <string>)`.**

4. `RebootSync` then treats that as a dead channel — `DcomRebootTrigger.cs:68-74`:
   ```
           // 2) DCOM didn't take it (e.g. 1191 / access denied on a Kerberos-broken box). Fall back to the
           //    SMB/SCM channel — the same transport that delivers the agent, which authenticates over NTLM.
           cancellationToken.ThrowIfCancellationRequested();
           _trace?.Trace(host, $"reboot channel: DCOM failed ({dcomFailure}) — falling back to SMB/SCM");
           try
           {
               RebootViaSmbScm(host, forced);
   ```

5. The fallback creates the remote service S1 scores — `DcomRebootTrigger.cs:185-195`:
   ```
           string runId = Guid.NewGuid().ToString("N");
           string serviceName = "Vivre_Reboot_" + runId; // unique per call → concurrent waves never collide
           string switches = forced ? "/r /f /t 5" : "/r /t 5";
           string binPath = "cmd /c shutdown " + switches + " /c \"Vivre Reboot Wave\"";
   ```
   ```
           RemoteServiceController service = RemoteServiceController.Create(host, serviceName, "Vivre Reboot " + runId, binPath);
   ```
   Because the 1191 cohort reaches this from the wave's *graceful* dispatch, `switches` is the
   **`"/r /t 5"`** arm — no `/f`. **Vivre re-sends, over a different pipe, the exact graceful semantics
   Windows just refused.**

**The misclassification, stated once: 1191 is not a channel failure. The channel worked (evidence 5).
Windows refused a graceful shutdown because users are logged on. Vivre reads "the transport is broken"
and switches transports, which neither fixes the refusal nor is invisible to EDR.**

The code's own prose encodes the wrong belief. `DcomRebootTrigger.cs:15`:
```
/// rejected (it returns 1191, or throws an access/Kerberos error) for the same reason WinRM is — the
```
1191 has nothing to do with the SPN/Kerberos cause. That line, and the same conflation at `:68`, are
the highest-value single-line corrections in the repo on this subject.

---

## 4. Agreed fix direction (operator-approved 2026-07-27, unbuilt)

**On 1191 specifically, treat it as "graceful refused" and escalate to forced DCOM
(`Win32Shutdown` flags 6) rather than switching transports. SMB/SCM must remain for boxes DCOM
genuinely cannot reach.**

Operator's stated basis: users expect these reboots, and he reboots regardless of sessions, so
immediate force is acceptable on this fleet.

### Why `Win32ShutdownTracker` was ruled out

It was the obvious "graceful but with a countdown and a reason string" candidate. **Evidence 2 kills
it: `Win32ShutdownTracker(300,"...",0,2)` returned 1191 on the same box.** The Tracker does not bypass
`ERROR_SHUTDOWN_USERS_LOGGED_ON` — the force flag is the only lever that does (evidence 3). A Tracker
call would have to pass flags 6 anyway, at which point it buys only the countdown and the message, at
the cost of a second WMI method in the one file the cardinal grep pins.

---

## 5. A — FEASIBILITY

### A1/A2 — the branch point, and whether the code survives to it

**The numeric code is available at `DcomRebootTrigger.cs:152`, and NOWHERE after it.**

`code` is a `uint` local declared at `:146` and out of scope at `:156`. **Line 155 is the last place in
the entire repository where 1191 exists as a number** — after that it lives only inside the
interpolated English string `"Win32Shutdown returned 1191"`.

`TryDcomShutdown`'s contract, `DcomRebootTrigger.cs:121`:
```
    private static (bool Ok, bool AlreadyInProgress, string Failure) TryDcomShutdown(string host, bool forced, CancellationToken cancellationToken)
```
destructured at `:53`:
```
        (bool ok, bool alreadyInProgress, string dcomFailure) = TryDcomShutdown(host, forced, cancellationToken);
```

So there are exactly two insertion points:

| Option | Where | Consequence |
|---|---|---|
| **A** | `RebootSync`, at `:67` (before the fallback) | Must string-match `dcomFailure`. That string is **also** operator-facing at `:90-91`, and a `Contains("1191")` test would match the existing test fixture shape at `ForceRebootRunnerTests.cs:71`. **Brittle.** |
| **B** | `TryDcomShutdown`, at `:152` (where `code` is live) | Numeric test alongside the existing `ErrorShutdownInProgress` check. **The correct seam.** |

**Invasiveness: category (i) — confined to private members of `DcomRebootTrigger.cs` — for *compilation*
only.** `TryDcomShutdown` is `private static`; `RebootViaSmbScm` is `private static void`; the constants
are `private const`. Nothing public changes. A new `private const int ErrorShutdownUsersLoggedOn = 1191;`
is a new addition — **1191 appears nowhere in the repo as a constant.** Repo-wide, the token appears
exactly four times, all prose or test fixture:

```
docs/archive/vivre-backlog-done-archive.md:420
source/Vivre.Core/Updates/DcomRebootTrigger.cs:15
source/Vivre.Core/Updates/DcomRebootTrigger.cs:68
source/Vivre.Core.Tests/Updates/ForceRebootRunnerTests.cs:71
```

**The catch path.** `DcomRebootTrigger.cs:165-172`:
```
        catch (Exception ex)
        {
            // A shutdown already in progress can surface as a typed/HRESULT error too — treat it as
            // going-offline, not a fall-back-worthy failure. Otherwise (Kerberos / access / timeout) let
            // the SMB/SCM fallback try.
            return IsShutdownInProgress(ex)
                ? (false, true, $"A shutdown is already in progress: {ex.Message}")
                : (false, false, $"{ex.GetType().Name}: {ex.Message}");
        }
```
A 1191 arriving as a *thrown* exception carries no recoverable numeric on this path — neither `HResult`
nor `NativeErrorCode` is read. `IsShutdownInProgress` (`:99-115`) is the pattern an analogous 1191
helper would follow, but **nothing in this repo reads a numeric off a `CimException`**, so whether one
is even available is unconfirmed. Evidence 1/2/4 all say 1191 was *returned*, never thrown; the doc's
"returns 1191, **or throws**" at `:15` is untested prose.

### A3 — can the wave represent "graceful refused"? **NO. Not at any layer.**

`RebootDispatch` has exactly two members — `IRebootWave.cs:18-27`:
```
public enum RebootDispatch
{
    /// <summary>The OS accepted the reboot (over DCOM, or via the SMB/SCM fallback).</summary>
    Issued,

    /// <summary>A shutdown was ALREADY in progress on the box (Win32 1115 / ERROR_SHUTDOWN_IN_PROGRESS) —
    /// the box is going offline on its own, so the wave should drop into the commit-watch loop rather than
    /// escalating to a forced reboot or declaring a false "reboot isn't taking" failure.</summary>
    AlreadyInProgress,
}
```

Both the DCOM-accepted path (`:57`) and the SMB/SCM path (`:76`) return `Issued` — **even the channel is
unrepresented.** The wave then collapses the value to one bool two lines after receiving it —
`RebootWave.cs:185`:
```
        bool alreadyGoingOffline = graceful == RebootDispatch.AlreadyInProgress;
```
and never reads `graceful` again.

**Consumers of `RebootDispatch` in production code — complete:** `RebootWave.cs:185`, `:231`;
`WorkspaceViewModel.cs:5901`; `ForceRebootRunner.cs:21`, `:81`, `:94`.
**There is no `switch` on `RebootDispatch` anywhere.** That cuts both ways:

- **Good:** a new member cannot break an exhaustive switch — nothing to break.
- **Bad and load-bearing:** every consumer is `== AlreadyInProgress` with an implicit else, so **a new
  member compiles clean with zero warnings and is silently treated as `Issued` at every site.** A new
  enum member is a *silent* change, not a compiler-checked one.

**Trap if the escalation is instead surfaced as a new `PatchPhase`** (to make it visible in the grid):
`Computer.cs:389` ends the state switch with a catch-all —
```
            _ => pending ? PatchState.RebootPending : PatchState.Idle,
```
and `:350` maps any unknown phase *string* to `Idle`:
```
        PatchPhase parsed = Enum.TryParse(phase, ignoreCase: true, out PatchPhase p) ? p : PatchPhase.Idle;
```
A new `PatchPhase` member renders as **grey Idle** with a clean build. **Do not make the escalation
visible via `PatchPhase`.**

### A4 — which `WaitForOffline` window applies? **DEFINITIVE.**

The two call sites are the only thing that distinguishes the windows — `WaitForOfflineAsync` takes a
plain `TimeSpan` and knows nothing about graceful vs forced.

- `RebootWave.cs:204` — `options.GoOfflineWindow` (graceful)
- `RebootWave.cs:241` — `options.ForcedGoOfflineWindow` (forced)

Concrete values, from `IRebootWave.cs:137-141`, `:147`, `:131-135`:

| | graceful (`GoOfflineWindow`) | forced (`ForcedGoOfflineWindow`) |
|---|---|---|
| `Default` | **8 min** | **16 min** |
| `ForSlowCommit` | **20 min** | **40 min** |

`ForSlowCommit` reaches 40 because `_forcedGoOfflineWindow` is never set, so the record `with` copy
carries `null` and the getter recomputes 2× the *new* 20-minute value.

Lane selection — `LcuRouting.cs:39-40`:
```
    public static RebootVerifyLane RebootVerifyLaneFor(int? osBuild, bool requiresStaging) =>
        Is2016(osBuild) && requiresStaging ? RebootVerifyLane.Lcu2016 : RebootVerifyLane.Wua;
```
**`ForSlowCommit` applies ONLY to a box with `OsBuild == 14393` AND `RequiresStagedPatching == true`.
Every other box in the fleet — including an unflagged 2016 box and any box whose build is unread — runs
`Default` (8/16).**

**The answer, by placement:**

| placement | `Default` | `ForSlowCommit` |
|---|---|---|
| **(a)** escalation hidden inside `DcomRebootTrigger` (returns `Issued`) | **8 min** — graceful window, **WRONG** |**20 min** — graceful window, **WRONG** |
| **(b1)** wave re-issues but falls through the existing structure | **8 min** — **WRONG** | **20 min** — **WRONG** |
| **(b2)** wave re-issues AND routes its wait to `:241` | **16 min** — correct | **40 min** — correct |

**Placement (a) cannot be fixed to give 16/40** — the trigger returns `Issued` and the wave has no other
signal. Only (b2) applies the budget the design intends, and (b2) requires the new `RebootDispatch`
member with the silent-`Issued` hazard above.

**The consequence is NOT a false-fail — it is an unnecessary second reboot.** The failure terminal at
`:250-251` still requires both windows to expire, so total budget is unchanged (24 min on `Default`,
60 min on `ForSlowCommit`). What changes is that a **second forced shutdown is dispatched at T+8 / T+20
to a box already executing a forced reboot** — `RebootWave.cs:228`:
```
                RebootDispatch forced = await IssueRebootAsync(forced: true).ConfigureAwait(false);
```
Nothing anywhere in `RebootWave.cs` records that a force has already been issued.

**The one guard that could suppress it structurally cannot.** `ProvenRebootedAsync` returns `false` on
an unreadable read — `RebootWave.cs:393-396`:
```
        if (current is null)
        {
            return false;
        }
```
A box mid-shutdown does not answer DCOM, so the uptime proof can only suppress an escalation against a
box that has already come *back up*, never one that is currently going down.

**Total shutdown commands one click can send to one 1191 box:** (a)/(b1) → **2** (plus a possible
`Vivre_Reboot_<guid>` service if the second DCOM leg throws). (b2) → **1**.

### A5 — what else keys off graceful/forced

| Thing | Affected? | Anchor |
|---|---|---|
| `sawOffline` gating | **No** — set only by observed offline / uptime proof | `RebootWave.cs:200`, `:209`, `:219`, `:254`, `:298`, `:324` |
| commit-watch (`reachableSince`, `PostReturnConfirmWindow`, `OfflineCeiling`, `HardCap`) | **No** — all measured from `sinceOrdered`, started at the graceful dispatch | `RebootWave.cs:180`, `:276`, `:283`, `:308` |
| uptime proof / `DcomBootTimeReader` | **No** — clock-immune, channel-agnostic | `RebootWave.cs:380-402` |
| `RebootDispatch.AlreadyInProgress` (1115) | **No** — produced at `DcomRebootTrigger.cs:65`, `:85`; consumed at `RebootWave.cs:185`, `:231`, `WorkspaceViewModel.cs:5901`. A 1191 divert does not touch it | as cited |
| post-reboot confirmation (`IPostRebootConfirmation`, `UbrConfirmation`, `ReadyConfirmation`) | **No** — runs after the offline observation, channel-agnostic | `RebootWave.cs:155`, `:341` |
| Unverified derivation | **No** | `RebootWave.cs:310-311`; `Computer.cs:381` |
| **Grid / help / dialog strings** | **YES — several become false.** See §7 | below |
| **Trace breadcrumbs** | **YES — they go false.** See §5-C6 | below |

### A6 — can flags 6 return 1191? Can the escalation loop?

**From code: nothing prevents it.** `:123` computes flags 6 and the return value passes through the
same `:147/:153/:155` ladder as flag 2. A non-zero forced return falls to `(false, false, …)` →
SMB/SCM at `:74`, exactly as today.

**Evidence 7 (100% of forced dispatches accepted) is a base rate over one fleet, not an invariant.
The termination guard must not assume flag 6 always succeeds.** Windows behaviour here is INFERENCE.

**Loop analysis.** `DcomRebootTrigger.cs` contains no `for`, `while`, `goto`, or recursive self-call;
`RebootSync` is straight-line and every branch ends in `return` or `throw`. `RunPatchSweepAsync` has no
retry loop (`WorkspaceViewModel.cs:2841` is a single `Task.WhenAll`). So **no existing loop can re-enter
the escalation.**

But the claim "immediate force cannot loop" is **conditional on an implementation that does not exist
yet, and should not be recorded as verified.** `forced` is a by-value parameter never reassigned
(`:50`, `:121`, `:123`), so a guard keyed on `forced == false` is monotone and terminating; a guard
keyed on `code == 1191` alone, written as a self-call, is **not** — the condition is unchanged by the
recursion.

**Double-fire, by contrast, is not hypothetical — it is code-traced and real under (a)/(b1).** See A4.

---

## 6. B — BLAST RADIUS

### B7 — touch list

**Code (minimum honest footprint is 4 files, not 1):**
- `source/Vivre.Core/Updates/DcomRebootTrigger.cs` — the branch, the constant, and the two false prose
  lines `:15` and `:68`.
- `source/Vivre.Core/Updates/IRebootWave.cs` + `RebootWave.cs` — **only under placement (b2)** (new
  `RebootDispatch` member + routing the wait to `:241`).
- `source/Vivre.Desktop/HelpContent.cs:548` — promises the 8/20-minute grace.
- `source/Vivre.Desktop/WorkspaceView.xaml.cs:1205-1206` — the wave confirm dialog, the operator's only
  pre-click risk statement.
- `CHANGELOG.md` — **Unreleased**, mandated by `CLAUDE.md:153`.

**Docs:** `docs/windows-patching-lane.md:54-56`, `:121`, `:389-394`, `:409-417`, `:437-439`;
`docs/key-file-path-map.md:51`, `:52`, `:54`; `CLAUDE.md:126-131` (the cardinal + gate grep).

**Settings keys:** none. No knob exists or is needed. `ServicingWaitMinutes` is the only reboot-adjacent
setting and it bounds the *pre*-reboot settle wait only.

**Comments that rot silently:** `DcomRebootTrigger.cs:211-214` ("a blocked graceful + its 8-min-later
forced attempt can each leave one"), `OrphanRebootServiceReaper.cs:10`, `RebootServiceReapPolicy.cs:5`
— all three state the SMB fallback as the normal 1191 outcome.

**Also already wrong today, found in passing:** `IRebootWave.cs:5` claims "the only caller is the Reboot
Wave"; `ForceRebootRunner.cs:81` has been a second caller since the Kerberos fallback landed.

### B8 — does the Kerberos-broken SMB/SCM path survive? **YES, if the divert is numeric and 1191-only.**

Connect failure and method refusal **are** distinguishable inside `TryDcomShutdown` — a transport/auth
failure throws and lands at `:165`, returning `(false, false, "<ExceptionType>: <message>")`; a method
refusal returns normally and lands at `:155`, returning `(false, false, "Win32Shutdown returned <code>")`.
**Both collapse to the same tuple, so they are NOT distinguishable at the `:67` branch point** — which is
the second reason Option B (`:152`) is the correct seam.

A numeric `code == 1191` test at `:152` is reachable only from the return-value path. Every thrown
failure — Kerberos rejection, access denied, timeout, `CimException` — still reaches `:170-172` →
`(false, false, …)` → `RebootViaSmbScm` at `:74`, unchanged. **Kerberos-broken boxes keep working.**

Note: no typed Vivre exception (`KerberosWrongPrincipalException`, `RemoteSessionLostException`) can
reach `DcomRebootTrigger` — those come from the WinRM/PSRP stack, not MI. DCOM failures arrive as
`CimException` or a generic `Exception`.

### B9 — `ForceRebootRunner` unaffected? **YES, provably.**

`ForceRebootRunner.cs:81`:
```
            RebootDispatch dispatch = await _dcomFallback.RebootAsync(host, forced: true, cancellationToken).ConfigureAwait(false);
```
It passes `forced: true`, so `:123` already computes flags 6 and a graceful-1191 divert is unreachable
from it. Its double-reboot reasoning (`:30-36`) is about WinRM auth preceding execution and is
untouched.

### B10 — cardinal gate grep

Run verbatim from `C:\src\Vivre`:
```
grep -rl --include=*.cs "Win32Shutdown" source/
```
Literal output — one line:
```
source/Vivre.Core/Updates/DcomRebootTrigger.cs
```
**GATE PASSES on `f693310`.** It survives the fix **provided** the escalation is not written into
`RebootWave.cs` and the primitive's name is not repeated in a new constant name, test identifier, or
comment. A constant named `ErrorShutdownUsersLoggedOn` is safe; one named `Win32ShutdownRefused` is not.

### B11 — test coverage: **the change lands in the one file with no test and no test seam**

| Type | Direct tests |
|---|---|
| `DcomRebootTrigger` | **ZERO** |
| `RemoteServiceController` | **ZERO** |
| `OrphanRebootServiceReaper` | **ZERO** (only the pure `RebootServiceReapPolicy` is covered) |
| `RebootWave` | 27 `[Fact]`, 0 `[Theory]` in `RebootWaveTests.cs` — all through a fake |

`RebootServiceReapPolicyTests.cs:9` states the status plainly:
```
/// boundary over advapi32, the same status as <c>RemoteServiceController</c> (zero tests today) — so
```

**There is no seam.** `TryDcomShutdown` is `private static` and builds its own session inline —
`DcomRebootTrigger.cs:126-127`:
```
            using var options = new DComSessionOptions { Timeout = CimTimeout };
            using CimSession session = CimSession.Create(host, options);
```
The only mockable boundary is `IRebootTrigger`, which sits **above** the decision. The wave's fake
models the trigger as accept-or-1115 with no return code at all — `RebootWaveTests.cs:662-669`:
```
    private sealed class FakeReboot(FakeBox box) : IRebootTrigger
    {
        public bool Graceful { get; private set; }
        public bool Forced { get; private set; }

        public Task<RebootDispatch> RebootAsync(string host, bool forced, CancellationToken cancellationToken)
        {
            if (forced) { Forced = true; } else { Graceful = true; }
```

**A green `dotnet test` proves nothing about this change — same blind-spot class as the RDP scale pin.**
No test anywhere asserts the emitted command line, the binPath, or any `Win32Shutdown` return code.

**What would have to exist before a change here is safe:**
1. A **pure classifier** — extract "what does return code N mean?" into a testable static (e.g.
   `0 → Accepted`, `1115 → AlreadyInProgress`, `1191 → GracefulRefused`, else → `FallBack`) and
   `[Theory]`-cover every code including 0, 1115, 1191, an arbitrary other, and the null case.
2. A **null-`ReturnValue` test** mirroring `WinRmEnablerTests.cs:32-40` (see §8.1).
3. A **wave-level escalation-count test** through `FakeReboot`, asserting that a `GracefulRefused`
   dispatch results in **exactly one** forced send, not two — the (a)/(b1) double-fire.
4. A **cancellation test** proving Stop between the 1191 read and the escalated send prevents the send.

---

## 7. C — RED TEAM (the fix direction itself)

Ordered by how likely each is to actually bite.

### C1 — The wave's own escalation double-fires. **HIGH. The most likely to bite.**

Under (a) or (b1): force at T+0 → wave waits the *graceful* window → box is still holding port 445
mid-commit (`IRebootWave.cs:143-146` puts a staged-2016 box at "15–20+ min" against a 20-minute budget)
→ uptime proof returns `false` because a box mid-shutdown does not answer DCOM →
`RebootWave.cs:228` sends **a second forced shutdown**. If that second DCOM leg *throws* (WMI torn down
mid-shutdown) rather than cleanly returning 1115, `IsShutdownInProgress(ex)` matches none of its
literals and control reaches `RebootViaSmbScm` — **recreating the exact `Vivre_Reboot_<guid>` service
the fix exists to eliminate, with `/f` this time.**

Whether a mid-shutdown box returns 1115 or a hard RPC error is **INFERENCE**, not confirmable here.
**Three cheap checks would settle it before any code is written:**
1. In the six existing logs, for every `reboot dispatched forced=true`, read the next `reboot channel:`
   line. Any `falling back to SMB/SCM` means this path is **already live today**.
2. On one live box: after a forced reboot, poll TCP 445 and a DCOM `Get-CimInstance` every 15 s. Any
   interval where 445 answers and CIM throws proves the precondition.
3. On one staged-2016 box: time from forced reboot to 445 going silent. If it exceeds 20 minutes, the
   second escalation is **guaranteed**, and routing the post-1191 wait to `ForcedGoOfflineWindow` is a
   prerequisite of the fix, not a refinement.

### C2 — Data loss: the delta is two steps, not one. **HIGH for interactive work.**

The comparison is **not** "force with countdown → force without countdown". Today's 1191 box gets
`"/r /t 5"` — **graceful, no `/f`**, with a 5-second timer and a `/c "Vivre Reboot Wave"` message.
After: flags 6, and **`Win32Shutdown` takes no time parameter and no message parameter** — `Flags` is
the only argument (`:139-144`).

By definition 1191 means an interactive session exists, so the affected class is exactly *interactive
unsaved work*: an RDP'd admin or DBA mid-task. **INFERENCE:** services still receive
`SERVICE_CONTROL_SHUTDOWN` under flag 6 and honour `WaitToKillServiceTimeout`, so the SQL *engine*
still checkpoints — the file's own comment at `:11-12` implying otherwise is overstated. The loss is
the human's unsaved work, and the loss of any notice attributable to Vivre.

### C3 — The operator-facing warning becomes wrong, in the wrong direction. **HIGH.**

`WorkspaceView.xaml.cs:1205-1206`:
```
                      + "Each reboots gracefully; if one won't go down within 8 minutes it is "
                      + "forced, to complete the reboot you ordered. Vivre then tracks each box "
```
`HelpContent.cs:548`:
```
                "Each box is rebooted gracefully (lets SQL/services flush). If it doesn't go down within 8 minutes (20 for a staged-2016 box, whose commit is slower) Vivre escalates to a forced reboot to complete the one you ordered.",
```
Neither mentions unsaved work. The **Force reboot** dialog does — `WorkspaceView.xaml.cs:762`:
```
                      + "Runs 'shutdown /r /f /t 5' — any unsaved work on those machines is lost.",
```
**After the fix the wave becomes, for the 1191 cohort, harder than the action that carries the
data-loss warning, while carrying the softer text.** `RebootWave.cs:172`'s `"Rebooting (graceful)…"`
and `:221-222`'s `"Rebooted already — the graceful reboot completed…"` become false labels too.

### C4 — The diagnostic record goes false, destroying the signature this incident was solved with. **HIGH.**

Two sites interpolate the *caller's parameter*, not what was sent to Windows.
`DcomRebootTrigger.cs:56`:
```
            _trace?.Trace(host, $"reboot channel: DCOM accepted (forced={forced})");
```
`RebootWave.cs:181`:
```
        _trace?.Trace(host, $"reboot dispatched forced=false: {graceful}");
```
Under an in-trigger escalation, a flags-6 dispatch logs **`forced=False`**. **Evidence 6 and 7 were
derived by counting exactly these strings.** After the fix, "61 accepted / 38 failed" and "100% of
forced accepted" become uncountable, and the next incident cannot be diagnosed the way this one was.

### C5 — Losing the S1 alert leaves *nothing* in its place, by default. **HIGH.**

Every reboot-channel breadcrumb is `_trace`, and `Trace` is file-only — `ActivityLog.cs:69-73`:
```
    /// Writes a high-volume diagnostic breadcrumb straight to the rolling file ONLY — never to the in-memory
    /// <see cref="Entries"/> (the UI panel), which is for operator-facing history.
```
**No `_activity.Info/Warn/Error` is ever emitted for a channel or force decision in the wave.** The only
UI-visible force indication is the `PatchPhase.Rebooting` progress line, mirrored at
`WorkspaceViewModel.cs:4160-4163` and overwritten by the next 20-second beat.

**Today the S1 alert was Vivre's only durable, push-delivered "this box refused a graceful reboot"
notification. The fix deletes it and replaces it with a line the operator must go and grep for.**

### C6 — 1191 is the only place the reboot path learns anyone is logged on. **MEDIUM.**

Nothing in `RebootWave.cs`, `DcomRebootTrigger.cs`, or `RebootWaveRowAsync` reads session state.
Auto-forcing converts a fact about the box into a no-op — a colleague mid-migration, a vendor session,
or the operator's own session all become indistinguishable from an abandoned locked console.

The irony is recorded in Vivre's own source. `ConfigMgrClient.cs:153-157`:
```
        # Isolated like $lastBoot — but the FAILED-query case must stay distinguishable from the
        # healthy zero-result: -ErrorAction Stop makes a real WMI failure throw (leaving the $null
        # seed = unknown, the grey "?"), while a successful query with no explorer.exe still yields
        # an honest $false ("genuinely nobody logged on"). SilentlyContinue collapsed both into a
        # definite false — a false green on exactly the signal checked before rebooting a box.
```
That signal lands on `Computer.UserLoggedOn` (a `bool?`, `Computer.cs:131`) and **is read by no reboot
path anywhere.** See the PARKED backlog entry — `DcomVitalsProbe.cs:293-303` already reads the logged-on
**names** over DCOM, on the transport evidence 5 proved healthy, at zero extra cost.

### C7 — The status quo is worse than "noisy": the fallback is a write-only channel. **Raises urgency.**

Four code facts:
1. **The SCM start failure reports to nobody.** `DcomRebootTrigger.cs:206`:
   ```
               System.Diagnostics.Debug.WriteLine($"Reboot-service start on {host} (reboot likely issued): {startEx.Message}");
   ```
   `Debug.WriteLine` is `[Conditional("DEBUG")]`; neither `Vivre.Core.csproj` nor `Vivre.Desktop.csproj`
   defines `DEBUG` outside the Debug configuration, so **in the shipped Release build this catch
   compiles to nothing** — a `catch` that reports to nobody, which `CLAUDE.md` forbids.
2. **`shutdown.exe`'s exit code is never read.** The image is `cmd /c shutdown …` inside a LocalSystem
   service; stdout and exit code go nowhere. If `shutdown.exe` itself hits 1191 on the target, Vivre has
   no channel to learn it.
3. **`RebootViaSmbScm` returns `void`** (`:183`) and the caller reports `Issued` unconditionally (`:76`)
   — a value documented as "The OS accepted the reboot". **It is not an acceptance; it is "we created a
   service, called StartService, and did not look."**
4. **It re-sends the semantics that just failed** — `/r /t 5`, graceful, on a box whose OS refused a
   graceful shutdown seconds earlier.

**Evidence 8's 3-of-6 non-reboots are not an anomaly; they are the expected output of this design.**
The current behaviour is a **silent ~50% failure rate on the cohort, reported to the wave as `Issued`**,
masked for 8–20 minutes, then rescued only by the escalation this fix would make redundant.

**Confirm from the existing logs at zero risk:** for each host with
`reboot channel: SMB/SCM issued (forced=False)`, check whether the next `WaitForOffline(graceful)
result=` reads `window-expired`. Every such pair is a silent fallback failure.

### C8 — Cancellation: an escalation at `:67` would fire *after* Stop. **The only cardinal-adjacent finding.**

`RebootSync` (`:50-93`) contains **exactly one** cancellation check, at `:70`:
```
        cancellationToken.ThrowIfCancellationRequested();
```
It sits **after** the `alreadyInProgress` branch and **before** the SMB/SCM fallback. Lines 53–66 have
none, and `Task.Run(…, cancellationToken)` at `:47` only prevents the work item from *starting*.

**An escalation inserted at line 67 (Option A) is upstream of the only cancellation check in the
method** — an operator who hits Stop after the graceful 1191 returns would still get a forced reboot.
Option B (`:152`) is inside `TryDcomShutdown` where `cimOptions` carries the token (`:131`), but there
is still no explicit check between reading `code` at `:146` and any re-invoke. **Either placement needs
its own `ThrowIfCancellationRequested` before the escalated send.**

### C9 — Other worse-outcome scenarios

- **Synchronized fleet drop (MEDIUM).** Today a wave produces two drop cohorts — accepted boxes at T+0,
  1191 boxes at T+8/T+20 — an accidental 61%/38% stagger (evidence 6). After the fix all boxes drop
  within seconds of T+0. `_rebootTriggerThrottle` (12, `WorkspaceViewModel.cs:287`) limits *issuance*,
  not drops. Watch for DC/DNS/auth latency spikes at wave start.
- **Gate throughput halves on a 1191-heavy fleet (LOW).** Under (a), both DCOM round trips happen inside
  one `rebootGate` acquisition (`RebootWave.cs:93-94`), holding each of the 12 slots twice as long.
- **Loss of the Stop escape hatch (LOW-MEDIUM).** Where the SMB graceful silently didn't take, the box
  stayed up until T+8/T+20 and Stop genuinely prevented the reboot. After the fix it is down at T+0.
- **String-matching `dcomFailure` is brittle (MEDIUM).** That string is simultaneously operator-facing
  at `:90-91`, and `ForceRebootRunnerTests.cs:71` already contains a fixture shaped
  `"DCOM: 1191. SMB/SCM fallback: access denied"` that a `Contains("1191")` test would match.
- **Force-reboot concurrency gap (pre-existing, LOW-MEDIUM).** `RebootForceSelectedAsync`
  (`WorkspaceViewModel.cs:5878`) never calls `BeginOperation`, so it neither reads nor writes
  `_heldRows`; the menu item is enabled purely on `hasSelection` (`WorkspaceView.xaml.cs:582`). A hand
  Force reboot can land on a box the wave is already rebooting, in both directions. Unchanged by this
  fix; noted because it adds to the worst-case shutdown count.

### C10 — Where the red team and the building agents disagreed

| Claim | Building agents | Red team | Resolution |
|---|---|---|---|
| "The change is confined to one file" | Category (i), private members only | **Disproved** — `CLAUDE.md:153`/`:156` mandate `CHANGELOG.md` + `HelpContent.cs`, and two operator-facing strings state the old behaviour as a promise | **Red team is right for the commit; building agents are right for compilation.** Minimum honest footprint 4 files. |
| "Immediate force cannot loop" | No loop constructs exist | **Unfalsifiable as stated** — termination depends on a guard that does not exist yet; a `code == 1191` self-call guard is non-monotone | **Do not record as verified.** No *existing* loop can re-enter; the new guard must key on `forced`, not on the code. |
| SMB service creations per wave run | one agent said ≤1, another ≤2 | ≤2 — two independent `RebootAsync` calls per run, each able to reach `:74` | **≤2.** |
| "For the 1191 cohort `RebootViaSmbScm` is only entered with `forced: false`" | stated as EVIDENCE | Falsified by the exception path — a throw from `RebootWave.cs:228` reaches `:172` → `:74` with `forced == true` → `/r /f /t 5` | **Scope the claim to "when the escalated flags-6 call returns cleanly".** |

---

## 8. Findings that are true *today*, independent of the fix

### 8.1 A null `ReturnValue` inverts a refusal into a false success

`DcomRebootTrigger.cs:146`:
```
                    uint code = rv is null ? 0 : Convert.ToUInt32(rv);
```
`Convert.ToUInt32(null)` would coerce to 0 — and here a null `rv` is *explicitly* coerced to 0, which
routes to `:149` `return (true, false, string.Empty);` → `Issued`. **A reboot that was never issued is
reported as accepted, and any 1191 branch built on top of `:152` never runs.**

**The repo already treats this exact coercion as a bug on its sibling DCOM call site**, with a
production guard — `WinRmEnabler.cs:77-81`:
```
        if (rawReturnValue is null)
        {
            throw new WinRmEnableException(
                $"Win32_Process.Create on '{host}' returned no result code — can't confirm Enable-PSRemoting started.");
        }
```
and a regression test — `WinRmEnablerTests.cs:32-38`:
```
    [Fact]
    public void Null_return_value_is_a_failure_not_success()
    {
        // Convert.ToUInt32(null) coerces to 0 — the old inline check read a never-populated
        // result code as a successful enable. Null must fail closed.
```
The reboot path still has the unguarded form.

### 8.2 A conversion throw silently skips the branch

`Convert.ToUInt32` throws on a negative signed value or a non-`IConvertible`. All such throws are inside
the `try` opened at `:124` and are flattened by `:170-172` to `(false, false, …)` → SMB/SCM.
**If MI ever hands back an HRESULT-shaped signed value, the fix does nothing, the box goes to SMB/SCM
exactly as today, and no log line distinguishes it from "not a 1191".** This is the largest residual
risk of the fix appearing not to work with no diagnosable reason.

---

## 9. Behaviour delta

**Only boxes that currently fall back are affected.** A box whose graceful DCOM call returns 0 is
untouched; a box DCOM genuinely cannot reach still goes to SMB/SCM.

| | Today (1191 cohort) | After the agreed fix |
|---|---|---|
| Command | `cmd /c shutdown /r /t 5 /c "Vivre Reboot Wave"` via a `Vivre_Reboot_<guid>` LocalSystem service | `Win32Shutdown` flags 6 over DCOM |
| Countdown | **5 seconds** | **none** — `Win32Shutdown` has no time parameter |
| Force | **no `/f`** (graceful semantics re-sent) | **forced** |
| User-visible message | `"Vivre Reboot Wave"` | **none** — no message parameter |
| Remote process creation | **yes** (`cmd.exe` → `shutdown.exe`) | **no** |
| SentinelOne | **2 threat records per reboot** | **zero** (evidence 10) |
| Actually reboots the box | **~50% on the sampled cohort** (evidence 8) | expected 100% (evidence 3, 7) |
| Orphan `Vivre_Reboot_*` service risk | yes | no |

---

## 10. SentinelOne forensics quirks worth remembering

1. **S1 raises TWO threat records per fallback reboot**, not one. The APVMANHOURDEV control at 08:59:31
   produced two. Do not read the count as two separate reboot attempts.
2. **S1's "Reported Time" lags "Identified Time" by minutes.** Correlate against Vivre's log timestamps
   using **Identified**, never Reported.
3. **The uppercase `/C` in S1's CMD column is an S1 display artifact.** S1 capitalises only the *first*
   token's switch; note that the *second* `/c` in the same string (`/c "Vivre Reboot Wave"`) renders
   lowercase in the same row. Nothing in this repo emits `/C` — in any file, any encoding, any build
   output, any compiled binary, or any git revision. **It does NOT indicate a build/source mismatch.**
4. **Forced DCOM is invisible to S1** (evidence 10). That is the whole basis of the fix — and, per §7-C5,
   also the reason the fix removes the only push notification the operator currently gets.

---

## 11. Open questions at freeze (UNVERIFIED)

- Whether a mid-shutdown box returns a clean 1115 or a hard RPC error to a second DCOM send at
  T+8/T+20. **Decides whether the (a)/(b1) double-fire lands harmlessly or recreates the S1-scored
  service.** INFERENCE only; the three checks in §7-C1 would settle it.
- Whether `Win32Shutdown` flags 6 can ever return 1191. Nothing in the repo asserts or enforces it;
  evidence 7 is a base rate, not an invariant.
- Whether `CimException` exposes a numeric for a method refusal (i.e. whether a *thrown* 1191 is
  recoverable at all). `0x800704A7` appears nowhere in the repo.
- Which build/flag the three fallback-failure boxes (APVMANHOURDEV, APVEQUISWEBDEV1, APVSNMIDDEV1)
  carry — i.e. whether they run `Default` (8/16) or `ForSlowCommit` (20/40). That is runtime state, not
  source.
- Whether `IRebootGate` is meant to rate-limit *sends* or *wave entries*. `RebootTriggerGate.cs:12-14`
  says "reboot issuance", which does not settle it; placement (a) doubles sends inside one acquisition.
