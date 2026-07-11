# Vivre — embedded RDP scaling & Failover Cluster Manager: full findings

> **Project knowledge note — REWRITTEN 2026-07-11 after the Path 2 spike rounds.** This version
> supersedes the parked 2026-06-17 doc, whose central mRemoteNG explanation turned out to be
> **wrong** (see "What mRemoteNG actually does" below). Everything here is measured or fetched
> evidence, labeled. Two problems: (1) FCM context menus collapsing — **SOLVED and shipped**;
> (2) the remote image rendering small/compact — **SOLVED IN PRINCIPLE via ZoomLevel**, proven by
> spike on `feat/rdp-popout-window`; the real build is pending design + red-team. Nothing here
> touches the reboot path — RDP rendering only.

---

## TL;DR / status

| Problem | Status |
|---|---|
| **FCM context menus collapse** in embedded RDP | **SOLVED** — shipped (master commit `a7b8833`, pin session scale to 100%). |
| **Magnification** (compact vs mRemoteNG) | **SOLVED IN PRINCIPLE — ZoomLevel.** Proven 2026-07-11 on APPMXHV4: fills, 1.5× bigger, quality acceptable, probe 96, FCM verified on the live cluster. Build pending. |

**The winning configuration (spike round 4):** a **System-DPI-Aware window** (no DPI trickery of
any kind) + **framebuffer = LOGICAL client size** (physical ÷ display scale) + **SmartSizing OFF**
+ **`ZoomLevel = 150`** set **post-login** via `IMsRdpExtendedSettings` and re-asserted after
re-fits, with a read-back to verify. The session's own scale stays pinned at **(100,100)** — the
FCM guarantee — and the OCX scales the rendered image client-side. No pop-out window, no
per-window DPI context, no thread-context switching is required by the mechanism itself.

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
`CrossDomainRdpViewModel.ConnectTo`. The throwaway spike lives on `feat/rdp-popout-window`
(`source\Vivre.Desktop\Rdp\RdpPopoutSpike.cs` + temporary hooks; never merges).

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
- **Path 2 (pop-out WinForms window):** the DPI-unaware premise is **dead**, but ZoomLevel doesn't
  need a pop-out at all. **The first question of the build design: can ZoomLevel be applied to the
  EXISTING embedded WindowsFormsHost control?** The embedded control lives in the same
  System-Aware process, fills today, and hosts the same OCX — if zoom works there, the fix may be
  a handful of lines in `RdpSessionView.xaml.cs` (logical framebuffer + SmartSizing off +
  ZoomLevel post-login + verify-and-retry re-fit) with no re-hosting. A pop-out remains a separate
  UX question (multiple sessions visible at once, multi-monitor), not a rendering necessity.
- **Path 3 (Fit-To-Panel under WindowsFormsHost):** **closed from source** (above).

**Design questions the build must answer (then red-team, then build):** embedded-tab zoom vs
pop-out; zoom level fixed 150 vs derived from the actual display scale vs settable; full-screen
fallback confirmation; verify-and-retry live resize; and the (100,100) pin staying exactly as-is.

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
