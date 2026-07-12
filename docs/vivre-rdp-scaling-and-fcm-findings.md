# Vivre — embedded RDP scaling & Failover Cluster Manager: full findings

> **Project knowledge note — REWRITTEN 2026-07-11 after the Path 2 spike rounds; updated same day
> when the build shipped.** This version supersedes the parked 2026-06-17 doc, whose central
> mRemoteNG explanation turned out to be **wrong** (see "What mRemoteNG actually does" below).
> Everything here is measured or fetched evidence, labeled. Two problems: (1) FCM context menus
> collapsing — **SOLVED and shipped**; (2) the remote image rendering small/compact — **SHIPPED
> via ZoomLevel + the verified re-fit engine** (`feat/rdp-clientside-zoom`; spike history under
> the `spike/rdp-popout` tag). Nothing here touches the reboot path — RDP rendering only.
>
> **General method:** the reusable instrument + protocol distilled from this hunt (and the cold-start
> freeze) live in [freeze-hunting-playbook.md](freeze-hunting-playbook.md) — this doc is a case file.

---

## TL;DR / status

| Problem | Status |
|---|---|
| **FCM context menus collapse** in embedded RDP | **SOLVED** — shipped (master commit `a7b8833`, pin session scale to 100%). |
| **Magnification** (compact vs mRemoteNG) | **SHIPPED** — client-side zoom + the verified re-fit engine in `RdpSessionView.xaml.cs` (branch `feat/rdp-clientside-zoom`). Step 0b proved it in the embedded tab: exact-fit, fills, 1.5× bigger, clicks land true, probe 96, FCM verified on the live cluster. |

**The shipped configuration:** the EXISTING embedded control (no pop-out, no DPI trickery) +
**framebuffer = LOGICAL pane size** (DIPs; the session renders at 100%) + **SmartSizing OFF** +
**`ZoomLevel`** (the OCX's documented client-side zoom, derived from the display scale and snapped
to the mstsc ladder) applied **post-login** with a **read-back to verify**, re-asserted whenever a
re-fit is confirmed applied. The session's own scale stays pinned at **(100,100)** — the FCM
guarantee, now load-bearing twice over — and size changes go through the **verified re-fit engine**
(below), because `UpdateSessionDisplaySettings` silently drops requests. On 100% displays the zoom
is skipped and SmartSizing stays on — functionally today's behavior.

**Sha correction:** the earlier doc cited the FCM fix as `1ce1abf`. That commit exists only as an
orphaned pre-rebase twin; the commit on master is **`a7b8833`** (identical patch, author identity
rewritten). Read any old `1ce1abf` reference as `a7b8833`.

---

## The setup (reproduction environment — unchanged and still accurate)

**Nested-RDP test environment (this is why DPI gets confusing):**
- 4K workstation at **150%** scaling →
- RDP into jump box **APVHOP** (session runs at **150% / 144 DPI**) →
- Vivre (and mRemoteNG) run *on* the jump box →
- each opens its own embedded RDP into the **target hosts**.
- Target hosts used: **APPMXHV4**, **DCVTRCHOSTS1** (domain `trchosts`, user `admin_sbridges`);
  FCM checks on the **APVVISIONB** cluster members (APVVISIONB-F3 / -SQL2).
- Runtime logs: `%LOCALAPPDATA%\Vivre\logs\vivre-*.log` on the jump box (the PM can't read the
  runtime log — Ruggie pastes it).

**The control stack:**
```
WPF  →  WindowsFormsHost (RdpHostElement)  →  WinForms Panel  →  AxMsRdpClient9NotSafeForScripting (v9 OCX)
```

**Key files:** `source\Vivre.Desktop\RdpSessionView.xaml.cs` (+ `.xaml`) — the embedded session
host: control creation, `LocalScale()` (the pin), framebuffer, SmartSizing, reconnect lifecycle.
Per-host settings resolve via `_creds.Resolve(host, RdpTree.AncestorsOf(_tree, host))` in
`CrossDomainRdpViewModel.ConnectTo`. The throwaway spike (pop-out + Step 0b hacks; never merged)
is preserved under the `spike/rdp-popout` tag; the `feat/rdp-popout-window` branch is deleted
after the shipped build's sign-off.

### The in-session DPI probe (measure, don't guess)

Paste into a remote PowerShell console to read the session's true scale:

```powershell
Add-Type @"
using System;using System.Runtime.InteropServices;
public static class Dpi {
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
[DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
[DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr dc, int i);
public static int Get(){ SetProcessDPIAware(); IntPtr dc=GetDC(IntPtr.Zero); int d=GetDeviceCaps(dc,88); ReleaseDC(IntPtr.Zero,dc); return d; }
}
"@
[Dpi]::Get()
```

**96 = 100% (FCM-safe), 144 = 150% (FCM breaks).** Gotchas that make it lie: run it in a **fresh,
blue Windows PowerShell 5.1 console** — NOT ISE (`SetProcessDPIAware` is a no-op in an initialized
GUI process), NOT PowerShell 7 (DPI-unaware, always reports 96).

---

## Problem 1 — FCM context menus collapse — SOLVED (`a7b8833`, shipped)

FCM's custom controls collapse their context menus at **display scaling > 100%** — a ~10-year-old
Microsoft **won't-fix** bug, reproduces locally and over RDP.
Advisory: `https://techcommunity.microsoft.com/discussions/windowsserverinsiders/issue---wont-fix---failover-cluster-manager-via-rdp/4009683`

Measured root cause (via the probe): Vivre drove the session to 150 (144 DPI); mRemoteNG's session
read 96. Fix: **pin `LocalScale()` → `(100, 100)`** (`DesktopScaleFactor` = `DeviceScaleFactor` =
100) on connect and on every resize re-fit. Fills + FCM-safe + compact.

**The (100,100) pin is now load-bearing TWICE OVER: it is the FCM guarantee, and the ZoomLevel
design assumes the session renders at 100% and is magnified client-side. It never moves.**

---

## Problem 2 — magnification — SOLVED IN PRINCIPLE (ZoomLevel)

### What mRemoteNG actually does — the old doc was WRONG here

The previous version of this doc claimed: "mRemoteNG = pure PerMonitorV2 WinForms; the WinForms
framework DPI-scales the OCX up to the physical 150% size." **That is false.** It was inferred
from mRemoteNG's *development* source (HEAD, which is PMv2). The shipped release is different:

- **mRemoteNG v1.76.20 (the last stable release) ships DPI-UNAWARE.** Fetched from the `v1.76.20`
  tag: `mRemoteV1/Properties/app.manifest` contains **zero** DPI declarations, `app.config` has no
  DPI section, and `ProgramRoot.cs` makes no DPI API call. (v1.77.2 adds only `dpiAware=true`;
  PMv2 appears only in post-2022 dev builds.)
- A DPI-unaware process is **bitmap-stretched wholesale by Windows** (×1.5 on the 150% jump box).
  That OS stretch — not framework scaling — is the entire magnification. Under this model every
  old measurement clicks into place: the "small" framebuffer (1970×1114 = the panel in
  *virtualized* 96-DPI units), the session probe reading 96, and the "unparented Form reports
  96 DPI quirk" (not a quirk — every DC in an unaware process reads 96).

### Closed dead ends — do not re-litigate

1. **SmartSizing as an upscaler — DEAD** (original finding, still true): SmartSizing only shrinks
   a larger framebuffer to fit; it will not magnify a smaller one, under `Dock=Fill` or under the
   undock+Anchor structure.
2. **Path 3: faithful Fit-To-Panel under WindowsFormsHost — CLOSED from source.** The .NET 10
   `WindowsFormsHost` (dotnet/wpf `WindowsFormsIntegration`) converts the WPF DIP constraint to
   physical pixels and resizes the child HWND — nothing more. No AutoScale, no font scaling, no
   content magnification. The "key experiment" never needed running.
3. **Per-window DPI-unaware (`SetThreadDpiAwarenessContext(UNAWARE)` at window creation) —
   CLOSED empirically (spike rounds 2–3).** The context genuinely applies to the window
   (`GetWindowDpiAwarenessContext` = UNAWARE, `GetDpiForWindow` = 96, ClientSize virtualized to
   2316×1212 on a 3474×1818 screen) and the OS stretch genuinely magnifies — **but the image can
   only ever fill 2/3 of the window.** Cause: **mstscax spins its own worker threads**, and new
   threads inherit the **process-default** DPI context — System-Aware in Vivre (WPF calls
   `SetProcessDPIAware` at init). Those threads measure the OCX window in **physical** pixels and
   size the internal render surface physically (3474) while the session and window are virtual
   (2316) → the image occupies exactly the virtualization fraction: **2316/3474 = 0.667**, an
   L-shaped dead zone right and bottom. Structurally unfixable from one window — you cannot set
   the DPI context of threads you don't own. mRemoteNG never hits this because *whole-process*
   unaware makes every thread coherent. Live resize in this mode also tears badly (overlapping
   frames). The only coherent-unaware options — flipping the whole Vivre process (wrecks the WPF
   shell) or a separate unaware helper process (violates the owned-by-Vivre constraint, needs
   cross-process credential handoff) — were rejected.

### Instrumentation traps burned during the spikes — do not re-trip

- **`Control.DeviceDpi` is a liar for per-window DPI contexts.** It initializes from the
  process-cached system DPI (`ScaleHelper.InitialSystemDpi`) and, on a non-PerMonitorV2 thread,
  returns that static value; it never queries the HWND (verified in dotnet/winforms source). In a
  genuinely unaware window it still reports 144. **Use `GetDpiForWindow(hwnd)`** (96 = unaware) and
  `GetWindowDpiAwarenessContext` + `AreDpiAwarenessContextsEqual` for the context. This false
  negative cost a full spike round: the framebuffer math trusted DeviceDpi and double-shrank.
- **`UpdateSessionDisplaySettings` returning cleanly means SUBMITTED, not APPLIED.** Requests that
  arrive while a previous one is in flight are silently dropped/coalesced (observed: refit
  reported OK while the session stayed at a stale size; SmartSizing masked it in the control
  variant, and the unaware variant tore). **Any live-resize implementation must verify-and-retry:
  read the session size back after a beat and re-send on mismatch — never fire-and-forget.**
  Also: only call it when `Connected == 1` — the OCX reports 2 while connecting, and a display
  update sent mid-handshake fails.

### THE ANSWER — ZoomLevel (the OCX's own client-side zoom)

`IMsRdpExtendedSettings` named property **`ZoomLevel`** (VT_UI4, read/write, **changeable after
the connection starts**) — Microsoft-documented: "Implements the Zoom feature by using the RDP
ActiveX control" (mstsc's System-menu Zoom, the feature users are told to use for exactly this
hiDPI problem). Constraints from the doc page: **mutually exclusive with SmartSizing**; **no
effect in RemoteApp and full-screen modes**. Supported since MsRdpClient7 — Vivre uses Client9,
on the same `set_Property` interface the scale pin already uses.
Reference: `https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpextendedsettings-property`
(the same table documents `EnableZoom` — allows scaling above native while SmartSizing is active —
as a possible alternative combo, untested.)

**Proven configuration and measured numbers (round 4, APPMXHV4, maximized, 2026-07-11):**

| Measurement | Value |
|---|---|
| Window | System-Aware (no DPI trickery), `windowDpi=144` |
| ClientSize / panel (physical) | 3474×1818 / 3474×1790 |
| Framebuffer requested = session | **2316×1184 (logical = physical ÷ 1.5)** |
| SmartSizing | OFF (mutually exclusive with zoom) |
| ZoomLevel | 150, set post-login, **read back = 150** |
| Visual | **Fills edge to edge, 1.5× bigger, quality acceptable** |
| In-session probe | **96** (session scale still pinned 100 — FCM-safe) |
| FCM right-click menus | **Verified working on the live cluster** |

The signature of success: **the session stays logical-sized (2316) while the image fills the
physical window (3474)** — the OCX scales client-side. That session/framebuffer-vs-window mismatch
is the mechanism, not a stale session.

**Full-screen ignores ZoomLevel** → full-screen falls back to today's shipping behavior (reflow to
monitor physical resolution: crisp, filled, compact). Accepted by the operator — the windowed view
is where readability matters.

---

## Step 0b + the re-fit engine (2026-07-11) — SHIPPED

**Step 0b (the embedded verification)** hacked the zoom config into the EXISTING
`RdpSessionView` on the spike branch and reached exact-fit in the embedded tab:
`fbConnect == fbNow == session == 1800x1066`, `zoom=150->150` read-back — image fills at 1.5×,
**clicks land true**, FCM right-click verified on the live cluster, in-session probe **96**.
The pop-out was thrown away: ZoomLevel works under `WindowsFormsHost`; input was never broken —
the geometry was (see the lying-instruments section below).

**The reproducible re-fit bug** (found by accident, now the acceptance test): minimize then
restore the Vivre window → `paneDIPs=951x616 fbConnect=1800x1066 fbNow=950x616 session=930x590`.
The restore fires a burst of re-fits; the first (intermediate 930x590) LANDED, the final
(950x616) was silently DROPPED in-flight; a later single drag healed it. So: **re-fits land when
spaced, drop when back-to-back, and a burst can land a stale intermediate** — worse than a clean
drop. Additionally, **MS-RDPEDISP protocol law: display-control widths MUST be even and ≥ 200**
(odd widths are silently rejected while the API returns success — every recorded "refit=OK but
stale" had an odd width or arrived back-to-back). Reference:
`https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rdpedisp/ea2de591-9203-42cd-9908-be7a55237d1c`

**The shipped engine** (in `RdpSessionView.xaml.cs`): one send choke point (`ResizeRemote`, the
only `UpdateSessionDisplaySettings` call and one of the pin's two readers) + minimum inter-send
spacing (500ms) + a one-shot verify timer with backoff that reads `DesktopWidth/Height` back (the
oracle — it tracked reality faithfully in every recorded observation) and re-sends until the
session matches the expectation **recomputed at fire time** (windowed-logical / full-screen-
physical / degraded-physical) + the OCX's `OnRemoteDesktopSizeChange` event as the applied-signal
accelerator + kicks on login, auto-reconnect, visible-change, and window-restore (a restore to
identical bounds fires no `SizeChanged`). Retries count only genuine drops (expectation unchanged
since the send); a moved target is a new intent. All sizes floored to EVEN on both dimensions and
to the ≥200 protocol minimum inside the three size-computing methods. Failure paths degrade to
today's fill-but-compact model (zoom off + SmartSizing + physical framebuffer; pre-8.1 servers
get one rebuild at session start and the engine goes dormant) with one latched, number-carrying
activity-log warning per session.

**Smoke-test round 1 (2026-07-11) — probe 96 PASS, FCM PASS, plus two engine-adjacent bugs, both
fixed (`f9b014e`):**

- **Full screen went dead** — a one-click latch, not an engine fault. The toolbar only ever sets
  the VM's `FullScreen` flag to *true*; the OCX refused the first entry (prime suspect: ZoomLevel
  still live at the moment of `FullScreen = true` — the park ran inside `OnEnterFullScreen`, i.e.
  only after a *successful* entry, backwards relative to mstsc, which resets zoom BEFORE
  switching); the failure was swallowed by a comment-only catch; and the stuck-true flag made
  every later click a true→true no-op (the MVVM setter skips unchanged values) — button
  permanently dead for that tab, surviving Reconnect. Fixed in mstsc's own order: zoom parks
  BEFORE the entry attempt, a switch that doesn't take is LOGGED (Warn with
  zoom/SmartSizing/Connected state), and the VM flag reverts to the control's real state in BOTH
  directions (a failed exit would otherwise latch you IN full-screen the same way) so every click
  is a fresh attempt. The latch was latent on master too (clicking Full screen while disconnected
  latched it) — the un-latch fixes that case as well.
- **Freeze / stuck pointer while resizing** — round 1 shipped a quiet-hands guard at the single
  send choke point: `TrySendRefit` defers (reschedules, costs no retry budget) while a drag is
  fresh (<450ms) or any mouse button is down (`GetAsyncKeyState` — WPF's `Mouse` class can't see
  buttons held inside the OCX's own HWND or during the modal border-resize loop). The guard is
  CORRECT behavior and stays — but round 2 (below) proved it was NOT the fix, and round 1's two
  candidate mechanisms (sync COM on the UI thread; a lost button-up in the OCX) were BOTH
  disproven by instrumentation. Round 1's "NOT the UI thread / inherited from master" reasoning
  was wrong on both counts — see lying instruments 4 and 5.

**Smoke-test round 2 (2026-07-11) — the freeze diagnosed by instrumentation and fixed (`48eba5b`):**

A throwaway instrument (`RdpFreezeInstrument.cs`, commits `778d627` + `d67979b`, modeled on the
cold-start poolwatch: a BACKGROUND-thread watchdog that keeps measuring while the UI thread is
dead, plus per-call COM timings, modal-loop markers, and a Win32-`GetCapture` stuck-capture
detector) settled it on APVHOP:

| Measured (hard border drag) | Run A (no clicking) | Run B (drag + clicking) |
|---|---|---|
| UI-thread block | 12,437 ms | 12,453 ms |
| COM calls inside the modal drag | 0 | 0 |
| comMsTotal during the block | 0 | 0 |
| OCX mutations with a button held | 0 | 0 |
| `[RDP stuckcapture]` fired | no | no |

- **Sync COM on the UI thread: DEAD.** Zero COM calls during every block; the feared post-settle
  burst measured 12 calls / 0 ms total. The engine's COM calls cost nothing.
- **Stuck capture / lost button-up: DEAD.** Never fired; `physBtn=1 captured=1` throughout — the
  border drag holds capture correctly.
- **The clicking is irrelevant** (Run A→B delta: 16 ms) — the symptom is just DRAG.
- **Operator bisection:** no RDP visible → no freeze; session open but its tab switched away → no
  freeze, switch back → freezes; **master (1.14.6), same session, same long drag → NO freeze.**
  Round 1's "master has the identical hole — inherited, not a regression" was WRONG: master had
  never been tested. The freeze was a regression of THIS branch.

**The real mechanism — a RENDER stall, not a call.** A border drag fires `SizeChanged` per mouse
move; `ApplyHostSize` pushed the new size into the `WindowsFormsHost` per tick; each OCX HWND
re-layout repaints the control's whole image synchronously on the UI thread inside Windows' modal
resize loop. On master that repaint is a cheap 1:1 present (SmartSizing ON, no zoom). On this
branch (SmartSizing OFF + ZoomLevel 150) every repaint rescales the whole framebuffer — dozens of
times a second = the 12 seconds, with `comCalls=0` throughout because none of that work crosses a
wrappable call site.

**The fix (`48eba5b`) — defer the host resize until the drag settles.** While the window is inside
`WM_ENTERSIZEMOVE`..`WM_EXITSIZEMOVE` OR a physical mouse button is held (a **GridSplitter** drag —
`CrossDomainRdpView.xaml` column 1 — raises no modal messages, so the button is the only signal),
`ApplyHostSize` STASHES the pending size instead of pushing it; it is applied once on settle via
three redundant triggers (`WM_EXITSIZEMOVE`, a 200ms button-release poll, the resize settle), then
one `KickSettle` converges the session through the normal verified engine. Same discipline as the
send debounce, one layer down. Accepted visual trade: the pane holds a stale image during the drag
and snaps on release. The fix changes only WHEN the control HWND is resized — never the scale, the
framebuffer, or the session (pin cardinal untouched).

**Pre-build probe (a/b/c — odd zoom values, FS zoom persistence, compact pulse): results pending;
the conservative defaults shipped (ladder snap, zoom parked at 100 before full-screen entry,
re-assert at send AND on verified-applied). Record the probe numbers here when the operator runs
them.**

---

## Lying instruments — the transferable lesson of this whole arc

Every wrong turn in this investigation was an instrument reporting something other than what it
measured. Burn these in:

1. **`Control.DeviceDpi` lies about per-window DPI contexts.** It returns the process-cached
   system DPI (on a non-PerMonitorV2 thread, a static value captured at startup) and never
   queries the HWND — in a genuinely DPI-unaware window it still reported 144. Use
   `GetDpiForWindow(hwnd)` (96 = unaware) and `GetWindowDpiAwarenessContext`. This false negative
   cost a full spike round.
2. **`UpdateSessionDisplaySettings` returning cleanly means SUBMITTED, not APPLIED.** The server
   silently drops protocol-invalid (odd-width) and back-to-back requests while the API reports
   success. The only truth is the read-back (`DesktopWidth/Height`) or the
   `OnRemoteDesktopSizeChange` event — hence the verify-and-retry engine.
3. **A diagnostic that conflates two moments lies by construction.** The spike strip's single
   `fb=` overwrote the connect-time request with later recomputations, which misread the status
   bar's 32-DIP collapse as a server-side "height deficit that tracks the request". Split it
   (`fbConnect=` vs `fbNow=`) and the mystery evaporated. Every number on an instrument must say
   what it IS, not what the reader assumes it means.
4. **"No sleep/wait/lock anywhere" does not prove the UI thread is unblocked.** Every
   `DispatcherTimer` tick RUNS ON the UI thread, every OCX property get/set is a synchronous call
   into an STA control, and — the round-2 lesson — a per-tick HWND re-layout can park the thread
   for 12 seconds without ANY of them. Static code reading is a proxy that cannot observe
   blocking; only a background-thread liveness watchdog (the cold-start poolwatch pattern)
   measures it.
5. **`comCalls=0` does not mean the OCX is idle.** The control's render path (HWND resize →
   synchronous full repaint) never crosses a wrappable call site — an instrument that times CALLS
   is structurally blind to work the control does inside the layout/paint pipeline. Pair call
   timing with a UI-liveness gap measurement, or the biggest cost is invisible.

### Spike round history (for the record; all on `feat/rdp-popout-window`)

- **Round 1:** UNAWARE variants rendered tiny-with-borders — a false negative: `DeviceDpi` lied
  (144 in an unaware window) and the framebuffer math double-divided. Control variant
  (System-Aware + physical + SmartSizing) validated the harness: fills/crisp/compact.
- **Rounds 2–3** (truthful `GetDpiForWindow` diagnostics, opened maximized): the unaware context
  PROVEN applied and the stretch magnifies — but fill capped at exactly 2/3 (the mstscax
  physical-surface incoherence above). Corner-drag: torn rendering; refit=OK while the session
  stayed stale.
- **Round 4:** ZoomLevel — full pass (table above).

---

## Paths forward — updated verdicts

- **Path 1 (per-host scale toggle):** viable but **rejected by the operator** (per-box
  configuration; leaves the FCM/cluster boxes — the ones that most need readability — compact).
  Superseded by ZoomLevel, which needs no per-host config.
- **Path 2 (pop-out WinForms window):** **dead and unnecessary.** The DPI-unaware premise died
  (mstscax worker-thread incoherence, above), and Step 0b then proved ZoomLevel works in the
  EXISTING embedded control — the fix shipped inside `RdpSessionView.xaml.cs` with no re-hosting.
  A pop-out remains only a possible future UX feature (multiple sessions visible at once), never
  a rendering necessity.
- **Path 3 (Fit-To-Panel under WindowsFormsHost):** **closed from source** (above).

**All design questions were answered and the build shipped** (design v3, red-teamed twice):
embedded-tab zoom; zoom derived from the display scale and snapped to the mstsc ladder;
full-screen keeps today's crisp-filled-compact fallback; live resize is verify-and-retry (the
re-fit engine above); and the (100,100) pin stays exactly as-is at its two read sites — the pin
is the cardinal rule of this arc.

---

## Related RDP item (separate from scaling) — Reconnect, SHIPPED (`87674c2`)

`OnReconnectRequested` tears down and rebuilds the OCX (`TearDownControl` + `CreateControl` +
`Connect`); `OnRdpDisconnected` distinguishes deliberate sign-out (`ExtendedDisconnectReason`
2/4/6) from involuntary drops (tab stays open with Reconnect); `EnableAutoReconnect` +
`GrabFocusOnConnect` wired; full-screen reflows to monitor resolution and restores on exit; live
resize is debounced. The rebuild pattern (TearDownControl → CreateControl → Connect) is the
template for any session-hosting work.

---

## Cardinal / safety note

None of this work involves a reboot path — it is **RDP rendering only**. `Win32Shutdown` lives
only in `DcomRebootTrigger.cs`, behind the operator-confirmed gate. Re-grep on any merge as usual.
