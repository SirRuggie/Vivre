# Vivre — embedded RDP scaling & Failover Cluster Manager: full findings

> **Project knowledge note.** Everything we learned chasing two problems in Vivre's embedded
> RDP control: (1) Failover Cluster Manager (FCM) right-click menus collapsing — **SOLVED and
> committed**; and (2) the remote desktop rendering small/compact vs mRemoteNG's bigger/readable
> image — **UNSOLVED, deliberately parked**. This captures what was tested, what worked, what
> didn't, exactly how mRemoteNG does it (read from its source), and the candidate paths forward
> so we can resume cold. Nothing here touches the reboot path — RDP rendering only.

---

## TL;DR / status

| Problem | Status |
|---|---|
| **FCM context menus collapse** in embedded RDP | **SOLVED** — committed `1ce1abf` (pin display scale to 100%). |
| **Magnification** (compact vs mRemoteNG's readable size) | **UNSOLVED — parked.** Baseline fills + is crisp + FCM-safe, but renders at native 100% = small. |

**Current shipping state (the known-good floor): commit `1ce1abf`.** The embedded RDP **fills the
pane**, full-screen works, **FCM menus work** — but the image is **compact** (native 100% scale,
smaller than mRemoteNG). That trade was deliberate: we pinned scale to 100% globally to kill the
FCM bug, which cost us readability everywhere.

**The single most promising un-tried lever (recommended for next time): make the display scale
per-host** — real scale (readable) by default, force-100% (FCM-safe) only on the cluster boxes.
See *Paths forward → Path 1*.

**Ruggie's instinct to revisit:** replicate mRemoteNG's **"Fit To Panel"** faithfully under the
WPF host (native render at panel resolution, no SmartSizing). There's a real open question about
whether .NET 10's `WindowsFormsHost` will DPI-scale the hosted control the way pure WinForms does.
See *Paths forward → Path 3*.

---

## The setup (so it's reproducible cold)

**Nested-RDP test environment (this is why DPI got confusing):**
- 4K workstation at **150%** scaling →
- RDP into jump box **APVHOP** (session runs at **150% / 144 DPI**) →
- **Both Vivre and mRemoteNG run *on* the jump box** →
- each opens its own embedded RDP into the **target hosts**.
- Target hosts used: **APPMXHV4**, **DCVTRCHOSTS1** (domain `trchosts`, user `admin_sbridges`).
- Runtime logs: `%LOCALAPPDATA%\Vivre\logs\vivre-*.log` on the jump box. The **PM cannot read the
  runtime log** (the build box is separate; the file is FTH-prefixed) — Ruggie pastes it.

**The control stack (the heart of the issue):**
```
WPF  →  WindowsFormsHost (RdpHostElement)  →  WinForms Panel  →  AxMsRdpClient9NotSafeForScripting (v9 OCX)
```
The RDP image is drawn by the **ActiveX OCX** (`AxMsRdpClient9`), hosted in a WinForms `Panel`,
hosted in WPF via `WindowsFormsHost`. **This hosting chain is the root of the magnification gap**
(explained below) — mRemoteNG hosts the same OCX in *pure WinForms*, with no WPF/airspace layer.

**Key files:**
- `source\Vivre.Desktop\RdpSessionView.xaml.cs` (+ `.xaml`) — **the file that matters.** Owns the
  control creation, `LocalScale()`, the framebuffer (`DesktopWidth`/`DesktopHeight`), SmartSizing,
  Dock, and the host panel.
- `source\Vivre.Desktop\ViewModels\RdpSessionViewModel.cs`
- `source\Vivre.Desktop\ViewModels\CrossDomainRdpViewModel.cs`
- `source\Vivre.Desktop\CrossDomainRdpView.xaml(.cs)`
- `MainWindow.xaml`
- Per-host RDP settings resolve via `_creds.Resolve(host, RdpTree.AncestorsOf(_tree, host))` in
  `ConnectTo` — this is the hook a **per-host scale setting** would plug into.

---

## Problem 1 — FCM context menus collapse — **SOLVED (`1ce1abf`)**

### Symptom
Inside Vivre's embedded RDP, right-click menus in **Failover Cluster Manager** collapse/won't stay
open. Works fine in mRemoteNG.

### It's a real, documented Microsoft bug (won't-fix)
FCM's custom controls collapse their context menus at **display scaling > 100%** — a ~10-year-old
FCM bug, reproduces **both locally and over RDP**, Microsoft marked it **won't-fix**.
Advisory: `https://techcommunity.microsoft.com/discussions/windowsserverinsiders/issue---wont-fix---failover-cluster-manager-via-rdp/4009683`

So the entire game is: **keep the RDP session at exactly 100% scale**, and FCM behaves.

### Dead-end theories we eliminated first (don't re-chase these)
All of these were investigated and ruled out as the FCM cause: `DeviceScaleFactor` alone, the RDP
control version, process bitness, the framebuffer/redirection mode, the DPI-awareness manifest, and
WindowsFormsHost-vs-pure-Form hosting. **None of them was the FCM trigger.** The trigger was simply
the session's effective scale being >100%.

### The breakthrough — *measure* the session's real scale, don't guess
We ran a probe **inside each live RDP session** (on the remote box) to read its true DPI/scale.

**Probe (paste into a remote PowerShell console):**
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
Reads `GetDeviceCaps(LOGPIXELSX=88)`: **96 = 100%, 120 = 125%, 144 = 150%.**

**Measurement gotchas that make this probe lie (must respect all three):**
1. Run it in a **fresh, blue Windows PowerShell 5.1 console** — NOT PowerShell ISE (`SetProcessDPIAware`
   is a no-op in an already-initialized GUI process, so ISE reports the wrong value).
2. NOT **PowerShell 7** (it's DPI-unaware and reports 96 regardless).
3. The `SetProcessDPIAware()` call inside the probe is what forces a trustworthy read in a fresh 5.1
   console.

**Result (trustworthy, fresh console):**
- **mRemoteNG session = 96 (100% scale)**
- **Vivre session = 144 (150% scale)**

→ **Root cause, measured not theorized: Vivre was driving the session to 150%, tripping the FCM bug.
mRemoteNG ran at 100%, so it was safe.**

### The fix — commit `1ce1abf`
The committed master's `LocalScale()` returned the **real** display scale (~150 on a 150% box), so
plain master tripped FCM. Fix = keep master's fill machinery, change exactly one thing:

- **Pin `LocalScale()` → `(100, 100)`** (i.e. `DesktopScaleFactor = DeviceScaleFactor = 100`).
- Master's fill mechanism preserved: framebuffer = pane physical px, the reflow, and `Dock=Fill`.

**Result (confirmed by Ruggie):** fills the pane, full-screen works, **FCM right-click menus work.**
Committed as `1ce1abf` (`RdpSessionView.xaml.cs` + a CHANGELOG *Unreleased ▸ Fixed* entry;
`publish.ps1` excluded). **This is the known-good floor / restore point.**

**The cost of this fix = Problem 2.** Pinning to 100% *globally* made every session render at native
100% → compact / small text. That's the readability gap below.

---

## Problem 2 — magnification (compact vs mRemoteNG's readable size) — **UNSOLVED, parked**

### Symptom
The `1ce1abf` baseline **fills and is crisp**, but renders the remote at native 100% → **small,
hard to read**. mRemoteNG, also at 100% session scale, looks **bigger / readable** AND fills.

### Measurements
- **Framebuffer sizes are nearly identical**, so framebuffer size is *not* the differentiator:
  - mRemoteNG framebuffer = **1970 × 1114**
  - Vivre (with a ÷1.5 experiment) = **1802 × 1142**
- **`[RDP fill diag]` instrumented build, from DCVTRCHOSTS1** (the decisive dump):
  - `control = 2702 × 1665` (the managed AxHost wrapper — **full pane**)
  - `framebuffer (DesktopWidth/Height) = 1802 × 1110` (pane ÷ 1.5)
  - `SmartSizing (read back) = True`
  - `WindowsFormsHost = 1801 × 1110` (DIP; = 2702 physical)
  - `panel (WinForms) = 2702 × 1665` (**physical**)
  - `panePhysical = 2702 × 1665`
  - **Operator confirmed: still small, with black borders.**
- **Key reading of that dump:** the managed wrapper *is* full-pane (2702) and SmartSizing *is* on,
  yet the image renders small. The wrapper size ≠ the OCX's **internal render surface** (which the
  diag can't see). And critically, **the WinForms `Panel` reports *physical* pixels (2702)** — so the
  OCX renders native into a physical-sized surface → compact.

### What we tested for magnification — and the verdicts

| # | Attempt | Result |
|---|---|---|
| 1 | `DSF=100` + framebuffer = pane ÷ 1.5 + SmartSizing, with **`Dock=Fill`** | **Small + borders.** Didn't fill. |
| 2 | Removed the `UpdateSessionDisplaySettings` reflow + re-assert SmartSizing post-connect | **No difference** (a stale build was suspected at the time). |
| 3 | `[RDP fill diag]` instrumented build (numbers above) | **Small + borders** — gave the decisive dump. |
| 4 | **Undock** (`Dock=None`) + explicit `Control.Size` = panel + `Anchor` (all sides) + SmartSizing + framebuffer ÷ 1.5 + `DSF/DeviceSF=100` — i.e. *exactly mRemoteNG's SmartSize structure* | **Small + borders.** |

**Decisive conclusion from #1 and #4:** we tried SmartSizing both with `Dock=Fill` and with the
proper undock + explicit Size + Anchor. **Both render small with borders.** Therefore **RDP
SmartSizing will not *magnify* (upscale a sub-pane framebuffer) inside Vivre's `WindowsFormsHost`.**
SmartSizing only ever *shrinks* a larger framebuffer to fit — and mRemoteNG never relies on it to
enlarge either (see below). **Client-side magnification inside the embedded WPF host is a dead end.**

---

## How mRemoteNG actually does it (read from its source)

Cloned `github.com/mRemoteNG/mRemoteNG` and read the RDP protocol. Files:
`Connection/Protocol/RDP/RDPResolutions.cs`, `RdpProtocol.cs` (`SetResolution`),
`RdpProtocol8.cs` (`DoResizeClient` / `DoResizeControl` / `UpdateSessionDisplaySettings`),
`RdpProtocol9.cs`, `UI/GraphicsUtilities/GdiPlusGraphicsProvider.cs`, and `Properties/app.manifest`.

### 1. There are three distinct resolution modes
`RDPResolutions` enum = **`SmartSize`**, **`FitToWindow`** (label = **"Fit To Panel"**),
**`Fullscreen`**. So **"Fit To Panel" and "Smart Size" are *different* modes** — they are not the
same thing. (Ruggie spotted this from the mRemoteNG dropdown; the source confirms it.)

### 2. `SetResolution()` — what each mode sets at connect
It sets `DesktopScaleFactor` and `DeviceScaleFactor` first, then branches by mode:

- **`FitToWindow` ("Fit To Panel"):**
  - `DesktopWidth/Height` (framebuffer) = `InterfaceControl.DisplayRectangle` (the content area)
  - `Control.Dock = None`; `Control.Location/Size = fitRect`
  - `InterfaceControl.AutoScroll = true`; `AutoScrollMinSize = fitRect.Size`
  - **No SmartSizing.** Renders **native 1:1** at the panel's resolution; scrollbars cover overflow.
  - Source comment (paraphrased): lock the session to the content-area size; the control is undocked
    so it keeps that fixed size; AutoScroll provides scrollbars when the panel shrinks below it.

- **`SmartSize`:**
  - `DesktopWidth/Height` (framebuffer) = `screen.Bounds` (the **full monitor** resolution)
  - `AdvancedSettings2.SmartSizing = true`
  - `Control.Dock = None`; `Control.Size = DisplayRectangle`; `Control.Anchor = Top|Bottom|Left|Right`
  - Source comment (paraphrased): connect at full-screen resolution for quality, then SmartSizing
    scales the image to fit the panel. **"Use Anchor instead of `Dock.Fill` because the AxHost
    ActiveX wrapper doesn't forward `Dock`-triggered resizes to the COM control's internal rendering
    surface."** ← this is the exact mechanism our `[RDP fill diag]` ran into.

- **`Fullscreen`:** `FullScreen = true`; framebuffer = `screen.Bounds`.

### 3. The three findings that reframe everything
- **mRemoteNG NEVER uses `Dock=Fill`.** Both real modes **undock** the control and set an **explicit
  Size** (`FitToWindow` → AutoScroll; `SmartSize` → Anchor). Vivre's `Dock=Fill` is the non-idiomatic
  outlier, and the OCX's internal surface doesn't get resized through it.
- **"Fit To Panel" does NOT use SmartSizing.** `SmartSizing = true` appears **only** in the
  `SmartSize` branch. Fit To Panel gets "fit" by setting the **session resolution to the panel size**
  and rendering **native** — not by bitmap-scaling.
- **Why Fit To Panel looks bigger than Smart Size:** Smart Size connects at the **full monitor**
  resolution and scales it **down** into the panel (→ everything smaller). Fit To Panel renders at the
  **panel's own (lower) resolution** (→ everything bigger). That's the entire visible difference.

### 4. Resize handling (`RdpProtocol8.cs`)
- `DoResizeClient()`: in the HEAD source, **only `Fullscreen` mode dynamically re-resolves** (calls
  `UpdateSessionDisplaySettings`). `FitToWindow` and `SmartSize` skip dynamic resize (comment:
  *"FitToWindow: fixed resolution set at connect time, scrollbars handle overflow. SmartSize:
  SmartSizing scales the image client-side, no session resize needed. Only Fullscreen benefits from
  dynamically changing the remote session resolution."*).
  - ⚠ **Caveat:** Ruggie's *released* mRemoteNG **does** resize on full-screen with Fit To Panel (he
    observed it directly). The HEAD source above looks like a newer refactor that may differ from the
    shipped version. **Trust the empirical behavior** for the target: classic Fit To Panel
    re-resolves the session to the panel on resize.
- `DoResizeControl()`: returns/skips for `FitToWindow`. For other modes, if the control is `Dock=Fill`
  it temporarily undocks, resizes, then re-docks (because WinForms ignores `Size` on docked controls).
- `UpdateSessionDisplaySettings`: **v8** → `RdpClient8.Reconnect(width, height)`. **v9** →
  `RdpClient9.UpdateSessionDisplaySettings(w, h, w, h, Orientation, DesktopScaleFactor, DeviceScaleFactor)`
  — the modern dynamic-resolution API that carries the **scale factors**.

### 5. Scale-factor computation (and the quirk that explains the 96 measurement)
- `DeviceScaleFactor = 100` (**hardcoded constant**).
- `DesktopScaleFactor = (uint)(ResolutionScalingFactor.Width * 100)`.
- `ResolutionScalingFactor` = `GdiPlusGraphicsProvider.GetResolutionScalingFactor()` =
  `new Form().CreateGraphics().DpiX / 96`.
- **The quirk:** it reads the DPI of a **brand-new, unparented `Form`** that was never shown on a
  monitor. Such a form commonly reports **96** → factor **1.0** → **`DesktopScaleFactor = 100`**.
  This is the most likely reason **mRemoteNG's session measured 96 (100%)** even though the formula
  *looks* like it would produce 150 on a 150% box. Net effect: mRemoteNG effectively runs the session
  at **100% scale** (which is also why it's FCM-safe).

### 6. Manifest
`Properties/app.manifest`: **`<dpiAwareness>PerMonitorV2</dpiAwareness>` + `<dpiAware>true</dpiAware>`.**
So mRemoteNG **is** DPI-aware (Per-Monitor-V2) — it is **not** a DPI-unaware app being DWM-scaled.

### 7. The synthesis — *why mRemoteNG is bigger and Vivre is compact*
Both run the **session at ~100% scale** (FCM-safe). The difference is the **host**:
- **mRemoteNG = pure PerMonitorV2 WinForms.** The **WinForms framework DPI-scales the OCX control**
  up to the physical 150% size. The session stays 100% (low framebuffer ≈ logical pane size); the
  enlargement is the framework scaling the control — **not** SmartSizing, **not** server-side
  `DesktopScaleFactor`.
- **Vivre = the same OCX under WPF's `WindowsFormsHost`** (an airspace child HWND). WPF does **not**
  DPI-scale the hosted native HWND's pixels, and the diag confirms Vivre's WinForms `Panel` reports
  **physical** pixels (2702). So the OCX renders **native into physical** → **compact**.

**That structural difference — pure-WinForms framework scaling vs WPF-airspace no-scaling — is the
real reason Vivre is compact and mRemoteNG is not.** It is not a setting we missed.

---

## The three levers for a readable image — with verdicts

1. **Server-side `DesktopScaleFactor = 150`** (Vivre's *original* master behavior). **Readable AND
   fills** — *proven*, because master was readable (and that's exactly what broke FCM, which proves
   the 150 took effect, i.e. the target fleet **does** honor `DesktopScaleFactor`). **Downside:**
   breaks FCM at >100%. **Mitigation:** make it **per-host** — 150 by default, 100 on FCM/cluster
   boxes. ✅ **Recommended.**
2. **Framework DPI scaling at session=100%** (mRemoteNG's way). Readable **and** FCM-safe — but
   requires hosting the OCX in **pure WinForms** (Path B), which breaks the embedded tab and brings
   back airspace fragility. ⚠ Heavy.
3. **Client-side RDP SmartSizing-upscale** (what we tried). ❌ **DEAD** — SmartSizing won't enlarge
   inside `WindowsFormsHost` (proven by attempts #1 and #4). It only shrinks.

---

## Paths forward (for when we resume)

### Path 1 — per-host display-scale toggle  ← recommended pragmatic fix
Un-globalize the `1ce1abf` pin. Make the RDP display scale **per-host**:
- **Default = the real display scale** (restores the readable, pre-`1ce1abf` behavior + fill).
- **Per-host override = "Force 100% (FCM-safe)"** for the cluster/FCM boxes (compact but FCM works).
- Seed the known cluster hosts (the **APVVISIONB** cluster members) as Force-100 by default.
- Net result: **readable everywhere except the handful of FCM hosts.** No rewrite, embedded tab kept,
  no airspace fragility.

Implementation notes to chase first (it's load-bearing — it changes the committed FCM fix, so
investigate + propose before coding):
- Where/how `LocalScale()` is pinned to `(100,100)` in `RdpSessionView.xaml.cs` (the `1ce1abf` change)
  and what feeds it.
- The per-host RDP settings model (`_creds.Resolve(host, RdpTree.AncestorsOf(...))`, the RDP tree node
  settings, `AppSettings`) — can it carry a per-host "display scale / force-100" field, and where does
  the UI live (`CrossDomainRdpView` / host node settings)?
- Gotcha to keep in mind: confirm the fleet still honors `DesktopScaleFactor=150` (master proved it
  did), plus any per-monitor edge cases.

### Path 2 — Path B: re-host the OCX in a plain WinForms window
Host `AxMsRdpClient9` in a top-level WinForms window the way mRemoteNG does, so the WinForms framework
DPI-scales it → **readable + FCM-safe everywhere**. **But** it breaks the embedded-in-tab model and
reintroduces the **airspace** overlap fragility the NavigationView refactor specifically removed.
Real work + real regression risk. Only worth it if Path 1 proves insufficient.

### Path 3 — faithful "Fit To Panel" under `WindowsFormsHost`  ← Ruggie's instinct to revisit
Replicate mRemoteNG's `FitToWindow` exactly: **undock + explicit `Size` = content rect + framebuffer
= content rect + `AutoScroll` + NO SmartSizing + `DSF/DeviceSF=100`**.
- **Open caveat:** the diag showed Vivre's WinForms `Panel` reports **physical** pixels (2702), so a
  faithful native-at-content-rect render would just reproduce the **compact** baseline — *unless* the
  hosted control tree gets PerMonitorV2 DPI scaling. **The key experiment to run:** does **.NET 10's
  `WindowsFormsHost` apply PerMonitorV2 DPI scaling to the hosted WinForms control tree?** (WinForms
  DPI support improved a lot in .NET Core/5+; it's worth verifying empirically rather than assuming
  the old .NET-Framework airspace behavior.) If it *does* scale the hosted control, faithful Fit To
  Panel could give a **crisp, bigger, filling** image without Path B. If it does **not**, Path 1 or
  Path 2 are the only readable options.
- Also worth a look: the **v9 `UpdateSessionDisplaySettings(w,h,w,h,orientation,DSF,DeviceSF)`** path
  for a true dynamic re-resolve on pane resize (closer to classic Fit To Panel's "resizes on
  full-screen" behavior), as opposed to a fixed-at-connect resolution.

---

## Reproduction cheat-sheet

- **Measure a session's real scale:** the DPI probe above, in a **fresh Windows PowerShell 5.1
  console** (not ISE, not PS7). 96 = 100% (FCM-safe), 144 = 150% (FCM breaks).
- **Compare against mRemoteNG:** run the same probe inside an mRemoteNG session → it reads 96.
- **The `[RDP fill diag]` build** (throwaway, on the experimental tree) logs `control`, `framebuffer`,
  `SmartSizing`, `WindowsFormsHost`, `panel`, and `panePhysical` to the runtime log. Rebuild it if you
  need the managed sizes again — but remember it **cannot see the OCX's internal render surface**, so
  the **operator's eyes (fills? bigger? crisp?) are the real test.**
- **Whatever you test, the decision criteria are three visual checks:** (a) does it **fill** the pane,
  (b) does it read **bigger**, (c) is it **crisp or soft**.

---

## Related deferred RDP item (separate from scaling)

**Reconnect is dead** (Issue 1) — diagnosed, **not implemented**, deferred. Agreed fix is **B + C**:
recreate the control on reconnect; stop auto-closing involuntary drops; fold in `EnableAutoReconnect`
+ `GrabFocusOnConnect`. Pick this up independently of the scaling work.

---

## Cardinal / safety note
None of this work involves a reboot path — it is **RDP rendering only**. Any branch resumed from here
keeps the cardinal rule intact: `Win32Shutdown` lives only in `DcomRebootTrigger.cs`, behind the
operator-confirmed gate. Re-grep on any merge as usual.

---

## State of the code as of parking this
- **Committed (push pending):** `1ce1abf` — the FCM fix (pin `LocalScale` → `(100,100)`), fills +
  FCM-safe + compact. This is the floor to build any of the paths above on top of.
- **Throwaway experimental working tree** (discard with `git restore` — it was never meant to ship):
  the magnification experiments (÷1.5 framebuffer, undock + explicit Size + Anchor, SmartSizing
  re-assert, `[RDP fill diag]` logging) across `RdpSessionView.xaml.cs` + the two VM diag-plumbing
  files, plus the orthogonal `-X86` switch in `publish.ps1` (decide keep/drop separately).
