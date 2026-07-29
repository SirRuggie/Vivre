# Reboot paths & guardrails — case file

> **Point-in-time record, never edited after the fact.** Written 2026-07-29 against `master` @ `99995b6`
> (clean tree, release **1.17.1**, suite 1200 green). The thirteen findings below were surfaced in the
> **2026-07-28** session and are reproduced here in full, in their original severity order and wording.
> Later work does not get retro-fitted into this file — if the code changes, this file stays as written
> and the new truth goes in `CHANGELOG.md` / `docs/vivre-backlog.md` / a new case file.
>
> **Status at freeze: NONE FIXED.** Every finding below is open. The backlog carries a ≤4-line pointer
> per finding (`docs/vivre-backlog.md` ▸ "REBOOT-PATH & GUARDRAIL FINDINGS") — that is where live status
> is tracked. This file is the detail; the backlog is the tracker.
>
> **Citations were re-verified on 2026-07-29** against `99995b6`, one file:line at a time. Each finding
> carries a **Citation check** block recording what resolved and what had drifted. Where a line number
> moved, BOTH the original and the corrected citation are recorded — nothing was silently overwritten.
> Finding text itself is verbatim from the backlog and was not reworded.

---

## Why this file exists

The 2026-07-28 session produced thirteen findings while investigating the SentinelOne detection, the
1191 arc and the silent grid freeze. They lived only as long-form backlog entries, which is the wrong
home for them: the backlog is a do-next tracker whose entries are meant to be short, and thirteen
multi-paragraph entries buried the list they were supposed to sit in. Worse, several of them contradict
what other project docs assert — `CLAUDE.md`'s reboot cardinal presents the `Win32Shutdown` gate grep as
if it mechanically covers every reboot primitive (finding 5 shows it covers one of at least four), and
`CLAUDE.md`'s friction rule reads as a description of shipping behaviour when findings 2, 3 and 4 are
reboot paths shipping today with no confirm.

This file is the full text, frozen. The backlog keeps the pointers.

---

## Framing note (carried verbatim from the backlog section)

> Surfaced while investigating the SentinelOne detection, the 1191 arc and the silent grid freeze. Listed
> most severe first. **Nothing here is a live incident** — they are gaps in confirm coverage, guardrails and
> failure visibility. The reboot cardinal (nothing auto-reboots) still holds on every path below: each one
> is operator-initiated. What varies is how much the operator is told before it fires.

---

## A note on paths

Two findings cite `source/Vivre.Desktop/Views/WorkspaceView.xaml.cs`. **There is no `Views/` folder.**
The real path is `source/Vivre.Desktop/WorkspaceView.xaml.cs` (the view lives at the project root;
`WorkspaceViewModel.cs` *is* under `ViewModels/` as cited). This path drift is recorded once here rather
than repeated in every affected finding.

---

## 1. APPROVE-VS-EXECUTE GAP — the set approved is not the set rebooted

**Finding (verbatim):**

> **APPROVE-VS-EXECUTE GAP — the set approved is not the set rebooted.** The confirm dialog names the
> selection read at `WorkspaceView.xaml.cs:1187`, then `Execute(null)` at `:1217` makes the command RE-READ
> `SelectedComputers` (`WorkspaceViewModel.cs:3781`). Two independent reads, so a selection change between
> them reboots a set the operator never approved. Force reboot has the same shape; **the install nudge
> already passes its rows explicitly and is the pattern to copy.**

**Citation check (2026-07-29 @ `99995b6`) — SUBSTANCE CONFIRMED, all three line numbers DRIFTED.**

| Original citation | Current line | What is actually there |
|---|---|---|
| `WorkspaceView.xaml.cs:1187` | `WorkspaceView.xaml.cs:1194` | `:1187` is the handler signature `private async void OnRebootAndVerify(object sender, RoutedEventArgs e)`. The confirm dialog's selection read is `var selected = vm.SelectedComputers.ToList();` at `:1194`. |
| `WorkspaceView.xaml.cs:1217` | `WorkspaceView.xaml.cs:1230` | `:1217` is a string-literal fragment inside the dialog's `Content`. The call is `vm.RebootAndVerifyCommand.Execute(null);` at `:1230`, guarded at `:1227-1228`. |
| `WorkspaceViewModel.cs:3781` | `WorkspaceViewModel.cs:3840` | `:3781` is a doc-comment line for the unrelated `CheckLcuStageReadiness`. The command's re-read is `var selected = SelectedComputers.ToList();` at `:3840`, inside `RebootAndVerifyAsync()` at `:3838`. |

The two-independent-reads claim holds structurally: `RebootAndVerifyAsync()` takes **no parameter**, so
the command cannot receive the confirmed set even in principle. Nothing between `:1194` and `:3840` pins
the selection. Corroborating detail found during verification: the view model's re-read carries its own
empty-selection guard at `:3841-3845` (`_activity.Warn(null, "Reboot & verify: select the machine(s) to
reboot first.")`), which only makes sense if the set can differ by the time the command runs.

**Force reboot — same shape CONFIRMED, with one structural difference.** `OnRebootForce`
(`WorkspaceView.xaml.cs:744`) reads the selection for the dialog, then calls
`await vm.RebootForceSelectedAsync();` at `:774` with **no argument**; the view model at `:6192` does
`List<Computer> targets = [.. rows ?? SelectedComputers.ToList()];`. So the gap is the same — but unlike
Reboot & verify, `RebootForceSelectedAsync` **already accepts an explicit row list**
(`WorkspaceViewModel.cs:6190`); the view simply doesn't pass one.

**Install nudge is the pattern to copy — CONFIRMED.** `MainWindow.xaml.cs:1135` calls
`await vm.RebootForceSelectedAsync(pending);` — `pending` is the exact set the nudge named, passed
straight through. One read, no re-read.

---

## 2. Schedule ▸ Reboot arms a forced SYSTEM reboot on the Enter key with NO confirm at all

**Finding (verbatim):**

> **Schedule ▸ Reboot arms a forced SYSTEM reboot on the Enter key with NO confirm at all.** Every field is
> pre-filled and Schedule is `IsDefault=True` (`ScheduleWindow.xaml:47`), so Enter registers a scheduled
> task running `/r /f /t 0`. The only reboot path in the app with no confirmation step whatsoever.

**Citation check — CONFIRMED (`ScheduleWindow.xaml:47` resolves exactly), with two precisions.**

`ScheduleWindow.xaml:47` is exactly the cited construct:

```xml
<ui:Button Appearance="Primary" Content="Schedule" Click="OnSchedule" IsDefault="True" />
```

Fields are pre-filled in the constructor (`ScheduleWindow.xaml.cs:47-50`: `DateTime def =
DateTime.Now.AddHours(1);` then date / hour / minute), so `OnSchedule` clears both its guards — the
null-check and the past-time check — and Enter always returns `DialogResult = true`. The registered task
is built at `WorkspaceViewModel.cs:1987`:

```powershell
$action = New-ScheduledTaskAction -Execute 'shutdown.exe' -Argument '/r /f /t 0 /c "Vivre scheduled reboot"'
```

registered as `Vivre_Reboot` under `-UserId 'S-1-5-18' -RunLevel Highest` — SYSTEM, forced, zero grace.
**This is the only `/t 0` in the repo**; every other reboot path uses `/t 5` (`DcomRebootTrigger.cs:362`,
`ForceRebootRunner.cs:51`) or `/t 300` (finding 12). No confirm exists anywhere on the path:
`WorkspaceView.xaml.cs:1062-1082` (`OnScheduleReboot`) goes menu click → `new ScheduleWindow(...)` →
`vm.ScheduleRebootSelectedAsync`, and the code says so in a comment at `:1075` — *"This path has no
MessageBox confirm — the ScheduleWindow is the whole gate."*

**Precision 1 — "no confirmation step whatsoever" is slightly stronger than the code.** There is no
*second* confirm, but the ScheduleWindow is itself a modal gate, and its reboot branch discloses the
consequence in its intro text (`ScheduleWindow.xaml.cs:23-27`: *"A one-time task runs as SYSTEM at that
time and force-restarts the box — any unsaved work on it is lost."*), plus a self-target disclosure at
`:31-34`. The accurate statement: the modal **is** the only gate, and its default button arms the action
on Enter with no yes/no restatement of count or target names — unlike Force reboot, which restates count
+ names and requires a `Reboot {count}` primary button.

**Precision 2** — Enter arms a *deferred* task (default ~1 hour out), not an immediate reboot. Still
operator-initiated: the reboot cardinal is not breached.

---

## 3. The install nudge's primary button force-reboots every reboot-pending box in the tab

**Finding (verbatim):**

> **The install nudge's primary button force-reboots every reboot-pending box in the tab.** Needs no
> selection, names no machines, shows no command, and is reachable by Ctrl+Enter (`MainWindow.xaml:240`).
> It is the highest-exposure reboot surface: broadest scope, least information, fastest to trigger.

**Citation check — CONFIRMED, `MainWindow.xaml:240` resolves exactly.**

```xml
<KeyBinding Modifiers="Control" Key="Return" Command="{x:Static local:MainWindow.InstallKey}" />
```

The chain, all in `MainWindow.xaml.cs`: `OnInstallKey` (`:1218`, gated on `CanShowInstallToolbar`) →
`OnInstallClick` (`:1022`) → `RunInstallFlowAsync(..., selectionOnly: false)` → the nudge at `:1120-1129`
→ `await vm.RebootForceSelectedAsync(pending);` at `:1135`. The dialog's `Content` states a **count**
(`$"{pending.Count} of {count} target machine(s) have a reboot pending."`) and never the names; no
command line is shown; the primary button reads `$"Reboot the {pending.Count} first"`.

**Precision:** the Ctrl+Enter binding targets `InstallKey` (install), not a reboot action directly — the
nudge is downstream on that path. The finding's claim is about reachability, and reachability holds.

**Corroboration found in the code itself.** `MainWindow.xaml.cs:1116-1117` already carries a comment
saying the same thing: *"This is the highest-exposure path: it needs no selection, is reachable by
Ctrl+Enter, and scopes to every reboot-pending box in the tab."*

---

## 4. Run script ▸ Reboot is a FIFTH reboot path with no confirm of any kind

**Finding (verbatim):**

> **Run script ▸ Reboot is a FIFTH reboot path with no confirm of any kind.** `ScriptRunnerWindow.xaml.cs:60-66`
> executes with no gate, the "All machines…" menu item needs no selection (`WorkspaceView.xaml.cs:530`), and
> `scripts\Reboot\"Restart - force now.ps1"` ships `shutdown.exe /r /t 5 /f`. Not covered by any reboot-path
> inventory to date because it arrives through the script library rather than a reboot command.

**Citation check — CONFIRMED; both line citations resolve exactly (path drift on the second only).**

`ScriptRunnerWindow.xaml.cs:60-66` is precisely the ungated run handler:

```csharp
60    private void OnRun(object sender, RoutedEventArgs e)
61    {
62        if (_viewModel.RunCommand.CanExecute(Editor.Text))
63        {
64            _viewModel.RunCommand.Execute(Editor.Text);
65        }
66    }
```

`WorkspaceView.xaml.cs:530` is exactly the no-selection menu item:

```csharp
runAll.Click += (_, _) => OpenScriptRunner([.. vm.Computers]);
```

The shipped script is `scripts/Reboot/Restart - force now.ps1:3` —
`shutdown.exe /r /t 5 /f /c "Vivre: restarting now."` (the finding quoted the switch prefix; the `/c`
message suffix is the only text it omitted).

**The full shipped reboot script inventory** (all four, established during this verification):

| Script | Line 3 (or as noted) |
|---|---|
| `Restart - force now.ps1` | `shutdown.exe /r /t 5 /f /c "Vivre: restarting now."` |
| `Restart - warn users (5 min).ps1` | `shutdown.exe /r /t 300 /c "This computer will restart in 5 minutes for maintenance. Please save your work."` |
| `Restart - if reboot pending.ps1` | `shutdown.exe /r /t 5 /f /c "Vivre: restarting now."` (line 13, conditional) |
| `Restart - cancel pending.ps1` | `shutdown.exe /a` (line 2 — cancels, does not reboot) |

**Cohort note:** this finding is the same class as findings 2 and 3 — a reboot path shipping with zero
confirm. The three should be scoped together, not piecemeal.

---

## 5. The cardinal gate grep guards ONE of at least FOUR reboot primitives

**Finding (verbatim):**

> **The cardinal gate grep guards ONE of at least FOUR reboot primitives.** It keys on the WMI token
> `Win32Shutdown` and so misses the literal `shutdown.exe` command lines in `ForceRebootRunner.cs:47`,
> `WorkspaceViewModel.cs:1930`, and `scripts\Reboot\*.ps1`. It is the mechanical guard on the project's one
> non-negotiable rule, and it currently proves less than it appears to.

**Citation check — SUBSTANCE CONFIRMED; BOTH cited line numbers DRIFTED.**

| Original citation | Current line | What is actually there |
|---|---|---|
| `ForceRebootRunner.cs:47` | `ForceRebootRunner.cs:51` | `:47` is the class declaration `public sealed class ForceRebootRunner`. The command line is `internal const string Script = "shutdown.exe /r /f /t 5 /c \"Vivre forced reboot\"";` at `:51`. |
| `WorkspaceViewModel.cs:1930` | `WorkspaceViewModel.cs:1987` | `:1930` is unrelated. The scheduled-task command line is at `:1987` (same line as finding 2). |

`scripts/Reboot/*.ps1` — confirmed, four scripts, tabulated under finding 4.

**Primitive inventory established by exhaustive sweep on 2026-07-29** (repo-wide grep of `source/` and
`scripts/` for `shutdown.exe`, `Restart-Computer`, `Stop-Computer`, `InitiateSystemShutdown`, and the WMI
token). "At least four" is accurate — the count is **five distinct issuing sites**:

1. **DCOM WMI shutdown method** — `DcomRebootTrigger.cs:339`. *This is the one the gate grep covers.*
2. **SMB/SCM service image** — `DcomRebootTrigger.cs:363`, `binPath = "cmd /c shutdown " + switches + ...`
   (started at `:373`). Same file as (1), so the grep incidentally lands on the file — but not on this
   primitive.
3. **WinRM `shutdown.exe`** — `ForceRebootRunner.cs:51`. A different file; the grep does not reach it.
4. **Scheduled task `shutdown.exe`** — `WorkspaceViewModel.cs:1987`. A different project
   (`Vivre.Desktop`); the grep does not reach it.
5. **The shipped script library** — `scripts/Reboot/*.ps1`. Not `.cs` at all, so the grep's
   `--include=*.cs` structurally excludes it.

**PREREQUISITE — finding 12 blocks this one.** The gate grep cannot be re-scoped against an unclosed
primitive inventory. Finding 12 is now resolved (see below) and its answer is folded into the five above,
but the dependency is recorded because it is the reason this finding could not be actioned when it was
raised.

**Note on the gate grep itself:** it was re-run during this verification and is **unchanged and passing**
— `grep -rl --include=*.cs "Win32Shutdown" source/` returns exactly `DcomRebootTrigger.cs`. The finding
is not that the grep is broken; it is that the grep proves less than its framing implies.

---

## 6. The SMB/SCM fallback cannot report a failed start — in Release it reports to nobody

**Finding (verbatim):**

> **The SMB/SCM fallback cannot report a failed start — in Release it reports to nobody.** It never reads
> `shutdown.exe`'s exit code, and surfaces the failure via `Debug.WriteLine`, which is
> `[Conditional("DEBUG")]` and compiles to nothing in Release; the caller returns `Issued` regardless. This
> is why a 3-of-6 field failure rate was invisible. Silent-failure class, cardinal-adjacent.

**Citation check — CONFIRMED. The fallback still exists on master; 1.17.0 did NOT remove it.**

`DcomRebootTrigger.RebootViaSmbScm` at `:358`. It never reads an exit code because it structurally
cannot: the service image is `cmd /c shutdown ...` (`:363`), started fire-and-forget at `:373`, so
`shutdown.exe` runs inside the service image rather than synchronously. The two failure surfaces are both
`System.Diagnostics.Debug.WriteLine` — `:381` (start failure) and `:391` (delete failure). The caller at
`:91` calls `RebootViaSmbScm(host, smbForced)` and then returns `smbDispatch` at `:100` regardless of
whether the start threw.

**One precision on the wording.** Since 1.17.0 the caller can return either `RebootDispatch.Issued` **or**
`RebootDispatch.EscalatedToForced` (`FallbackDispatch`, `DcomRebootTrigger.cs:143`) — both are
success-shaped, so the substance ("returns success regardless") holds, but "returns `Issued`" is now one
of two success values rather than the only one.

**What 1.17.0 changed and what it did not.** 1.17.0 demoted this path: a 1191 refusal on a graceful DCOM
call now escalates to forced on the **same** DCOM session instead of switching to SMB/SCM, which is what
removed the routine SentinelOne "Lateral Movement" scoring. The SMB/SCM fallback is still reached when
DCOM *throws* (connect/auth/timeout — the Kerberos-broken cohort), when the escalated send itself fails,
or on any other non-zero/missing code. Its silence is unchanged.

---

## 7. Force reboot silently drops the operator's alternate credential on the Kerberos fallback

**Finding (verbatim):**

> **Force reboot silently drops the operator's alternate credential on the Kerberos fallback.**
> `ForceRebootRunner.cs:81` — a different principal reaches the box than the one the operator selected, with
> no indication in the UI. Wrong-identity-without-telling class.

**Citation check — SUBSTANCE CONFIRMED; line number DRIFTED (`:81` → `:87`).**

`:81` is a comment line inside the `catch (KerberosWrongPrincipalException)` block. The call is at `:87`:

```csharp
RebootDispatch dispatch = await _dcomFallback.RebootAsync(host, forced: true, cancellationToken)
    .ConfigureAwait(false);
```

The method's own `PSCredential? credential` parameter (`:67`) is passed to the WinRM leg at `:76` and is
**not** passed to `_dcomFallback.RebootAsync` — the interface takes no credential, so the DCOM/SMB legs
run on the process's ambient Windows identity. The operator is told the channel switched
(`status?.Report("WinRM auth rejected (Kerberos) — trying the DCOM channel…")` at `:86`) but not that the
identity changed with it.

---

## 8. Custom columns auto-run arbitrary operator PowerShell on EVERY host on EVERY list load

**Finding (verbatim):**

> **Custom columns auto-run arbitrary operator PowerShell on EVERY host on EVERY list load.** No click, no
> confirm: `CustomColumnProbe.cs:73-75` base64-wraps into `ScriptBlock::Create` and `WorkspaceViewModel.cs:1287`
> runs it. Invisible to every gate grep. The operator's current column ("Logged-on user") was inspected
> 2026-07-28 and is a pure read — **a capability risk, not a live one.**

**Citation check — first citation CONFIRMED exactly; second DRIFTED, with a material caveat.**

`CustomColumnProbe.cs:73-75` straddles the two constructs precisely (`:74` is the intervening `try {`):

```csharp
73    string scriptBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(column.Script));
74    sb.AppendLine("try {");
75    sb.AppendLine($"  $sb = [ScriptBlock]::Create([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{scriptBase64}')))");
```

| Original citation | Current line | What is actually there |
|---|---|---|
| `WorkspaceViewModel.cs:1287` | `WorkspaceViewModel.cs:1344` | `:1287` is `Computers.Clear();` inside `SetComputers`. The auto-run is `_ = RunCustomColumnsSelectedAsync(toSweep);` at `:1344`, inside the local function `KickAutoCheck()`. |

**Material caveat found during verification, recorded because it narrows the finding.** The auto-run is
**gated on `AutoCheckOnLoadEnabled()`** (the enclosing `if` block), i.e. Settings ▸ *Auto-check on load*.
With that setting off, custom columns do **not** run on list load. The finding's "EVERY list load" is
therefore true of the default configuration, not unconditionally true. "No click, no confirm" is
unconditionally true on the path that does run.

---

## 9. `Vivre_Reboot_*` fallback services get no SDDL and their delete is raceable

**Finding (verbatim):**

> **`Vivre_Reboot_*` fallback services get no SDDL and their delete is raceable.** Nothing in Vivre constrains
> who could start a leftover service; `OrphanRebootServiceReaper` exists precisely because the delete loses
> races. Tightening the ACL at creation would make the reaper a backstop rather than the control.

**Citation check — CONFIRMED (all three sub-claims).**

Creation is `RemoteServiceController.Create(host, serviceName, "Vivre Reboot " + runId, binPath)` at
`DcomRebootTrigger.cs:370` — four arguments, **no security descriptor**. A repo-wide grep of `source/`
for `SDDL` / `sddl` / `SetServiceObjectSecurity` returns **zero hits**, so no ACL is applied anywhere
after creation either.

The delete is best-effort inside a `finally` at `:390-391`, wrapped in its own `try`/`catch` whose only
surface is `Debug.WriteLine` — and the code comment at `:385-389` states the race outright: *"the box is
going down, so this SCM delete may race the reboot and throw."* `OrphanRebootServiceReaper` exists at
`source/Vivre.Core/Remoting/OrphanRebootServiceReaper.cs` as the list-load cleanup for exactly that.

The service-creation code still exists on master; 1.17.0 demoted it to fallback-only (see finding 6) but
did not remove it.

---

## 10. `WinRmEnabler` is a second Lateral-Movement detection surface

**Finding (verbatim):**

> **`WinRmEnabler` is a second Lateral-Movement detection surface.** `WinRmEnabler.cs:55` launches
> `powershell.exe` on remote hosts via `Win32_Process.Create` — unrelated to the 1191 arc, same EDR
> signature, and it will score whenever Enable-WinRM is run.

**Citation check — CONFIRMED, `WinRmEnabler.cs:55` resolves exactly.**

```csharp
54    using CimMethodResult result =
55        session.InvokeMethod(@"root\cimv2", "Win32_Process", "Create", arguments, operationOptions);
```

The statement begins at `:54`; `:55` carries the `Win32_Process` / `Create` invocation itself. The command
line is supplied at `:46` as `CimMethodParameter.Create("CommandLine", EnableCommand, CimFlags.In)`.

---

## 11. `HostName.IsLocal`'s alias branch is case-sensitive

**Finding (verbatim):**

> **`HostName.IsLocal`'s alias branch is case-sensitive.** A row named as an FQDN (`APVHOP.contoso.com`) or
> `LOCALHOST` gets NO self-target warning while the reboot still lands on the Vivre host. **Latent** — the
> operator's lists are short-name today.

**Citation check — CONFIRMED. The whole implementation, `HostName.cs:12-15`:**

```csharp
12    public static bool IsLocal(string? host) =>
13        string.IsNullOrWhiteSpace(host)
14        || host is "localhost" or "127.0.0.1" or "::1" or "."
15        || string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
```

Both slip-throughs are real, by two **different** mechanisms — worth separating because a single fix
addresses only one of them:

- **`LOCALHOST`** — `:14` is a C# constant pattern match, which is ordinal and **case-sensitive**. `"LOCALHOST"`
  does not match `"localhost"`. This is the case-sensitivity the finding names.
- **`APVHOP.contoso.com`** — `:15` **is** case-insensitive (`OrdinalIgnoreCase`), but it is an **exact**
  comparison against `Environment.MachineName` (the short name). An FQDN is not equal to the short name
  regardless of case, so it slips through on *exactness*, not case.

The consumer is `LocalHostRebootWarning`, which is a **disclosure**, not a guard — so a miss costs the
operator a warning, never a blocked or redirected reboot. That is what makes this latent rather than live.

---

## 12. A "/t 300" warned-reboot path was referenced but never inventoried

**Finding (verbatim):**

> **A "/t 300" warned-reboot path was referenced in an earlier PM report but never appeared in that report's
> own reboot-primitive inventory.** Either it exists and the inventory is incomplete, or the reference was
> wrong. Confirm which; do not carry an unverified fifth primitive in the record.

**RESOLVED 2026-07-29 — IT EXISTS. The earlier reference was right and the inventory was incomplete.**

Exhaustive repo-wide search for `/t 300` returns exactly one code hit, in the shipped script library — not
in C# at all:

```
scripts/Reboot/Restart - warn users (5 min).ps1:3
shutdown.exe /r /t 300 /c "This computer will restart in 5 minutes for maintenance. Please save your work."
```

Note the switches: `/r /t 300` with **no `/f`** — a genuinely warned, non-forced, five-minute countdown
reboot, and the only reboot path in the product that gives a logged-on user any chance to save work. It
reaches a machine through **Run script ▸ Reboot** (finding 4), which is why no reboot-command inventory
found it: it is not a reboot command, it is a script.

**Consequence — this is why finding 12 is a prerequisite for finding 5.** The primitive inventory could
not be closed while this was open, and the gate grep cannot be re-scoped against an open inventory. With
this resolved, finding 5's inventory stands at five sites (tabulated there).

---

## 13. HelpContent's Install topic never got the self-target line

**Finding (verbatim):**

> **HelpContent's Install topic never got the self-target line.** The other three how-to topics received it
> in `01ef85d`; the "reboot these first" nudge — the broadest-scope reboot surface (see #3) — did not.

**Citation check — CONFIRMED, exactly as stated.**

`01ef85d` (*"feat: warn when a reboot target is the machine vivre runs on"*, 2026-07-27) exists and added
the self-target line to **three** help topics. `HelpContent.cs` today carries exactly three occurrences of
*"If your OWN machine is in the selection…"*:

| Line | Topic |
|---|---|
| `:358` | *How do I reboot machines now?* (Force reboot) |
| `:521` | the Schedule ▸ Reboot topic |
| `:551` | the Reboot & verify wave topic |

The **code** has **four** `LocalHostRebootWarning` call sites — `WorkspaceView.xaml.cs:760` (Force
reboot), `:1210` (Reboot & verify), `ScheduleWindow.xaml.cs:31` (Schedule), and
`MainWindow.xaml.cs:1118` (the install nudge). The fourth is the gap: the nudge got its warning in a
**later** commit, `506abdf` (*"feat: warn on self-target in the reboot-pending install nudge"*), which did
not touch `HelpContent.cs`. So the help describes three of the four surfaces that warn.

**Scope note:** `HelpContent.cs` is source code, not documentation, despite being documentation-shaped.
Closing this finding is a code change and was explicitly out of scope for the session that wrote this file.

---

## Relationships recorded at freeze

- **12 → 5 (prerequisite).** The cardinal gate grep (5) cannot be re-scoped against an unclosed primitive
  inventory; 12 was the open question in that inventory. 12 is now resolved and its answer is folded into
  finding 5's five-site table, but the dependency is why 5 could not be actioned when raised.
- **2 · 3 · 4 (one cohort).** All three are reboot paths shipping today with **zero confirm** — Schedule ▸
  Reboot, the install nudge's primary button, and Run script ▸ Reboot. They should be scoped as one piece
  of work so the confirm story is decided once, not three times in three shapes.

## What was NOT found

No finding in this set was refuted. Every one resolved as either CONFIRMED at its cited line, or
CONFIRMED-with-drifted-line-number, or (finding 12) resolved in the affirmative. Three findings gained a
precision that narrows or qualifies the original wording without overturning it — 2 (the ScheduleWindow
modal *is* a gate, just not a yes/no confirm), 6 (the caller now returns one of two success values, not
only `Issued`), and 8 (the auto-run is gated on Settings ▸ Auto-check on load). Those precisions are
recorded under their findings and are not corrections to the findings' substance.
