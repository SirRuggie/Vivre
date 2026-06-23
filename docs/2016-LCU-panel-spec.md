# Server 2016 CU panel — as-built spec + maintenance constraints

**Status: BUILT and committed.** This document was the worker hand-off spec; it is now the
as-built record plus the standing rules for anyone touching these files again. Commits:
`5631a61` (panel, clean rebuild) · `ecb798e` (filter reset when the last 2016 box leaves) ·
`e4047a0` (guided missing-package prompt) · `332efe8` (Clean up wording). Build 0/0, 313 tests.

> **Post-build drift — corrected 2026-06-23 (the lane evolved after this hand-off spec was frozen; read
> these overrides alongside the as-built text below):**
> - **The 2016 actions are now opt-in per box.** Clean up / Stage / Verify act on `Server2016Targets()`
>   = `StagePreconditions.IsStageTarget` (`LcuRouting.Is2016(OsBuild)` **AND** `RequiresStagedPatching`),
>   not "all 2016 when none selected." Triggering them with no flagged box shows `StagedPatchingNeededDialog`.
> - **Reboot Wave button re-points to `OnRebootAndVerify`** (the generalized fleet-wide reboot-and-verify),
>   not `OnRebootWave2016`; `IsEnabled = SelectedComputers.Count > 0` (any selection, not 2016-only).
> - **Chip count:** the Patching-mode chip bar now has 10 chips (All, Updates, Reboot pending, Errors,
>   Offline, Done, Unhealthy, Not scanned, Scheduled, Server 2016); the Health bar has 6 — not "8".
> - **Action-bar MultiDataTrigger has 3 conditions:** `ActiveFilter==Server2016` AND `HasServer2016` AND
>   `IsUpdateMode==True` (Patching-only).
> - **STAGED tag has 3 conditions:** `PatchState==RebootPending` AND `OsBuild==14393` AND
>   `StagedThisSession==True` (so a non-LCU reboot-pending 2016 box never shows the tag).
> - **Settings:** `MonthlyCu.ExpectedSizeMb` and the `LcuSizeBox` field were **removed** — the package is
>   matched by KB + arch, never size. `MonthlyCu` is now `Kb / Arch / TargetUbr / Display`.
> - New since this spec: the `StagedInstallDecisionDialog` (Stage CU / minor-only / Cancel), the
>   scan-before-Stage gate, and the `LcuVerifiedThisSession` skip — see `2016-LCU-lane-spec.md`.

The point of the panel: the people taking over patching **don't know which servers are 2016**.
Vivre classifies them (a vitals check fills `Computer.OsBuild`) and surfaces the classification
as a one-click filtered view with the four 2016 actions — nothing to look up, nothing manual.

---

## ⚠ Read this first: how the previous attempt broke (the lesson that shapes these rules)

The first panel commit compiled 0/0 and passed 313 tests but **rendered broken** — two
overlapping header rows, all filter chips lit, added machines missing, banner at idle, window
opening hidden. Root cause (diagnosed by adversarial static review): **one binding** —
`<Run Text="{Binding LcuPackagesFolder}"/>`. `Run.Text` binds **two-way by default**; the
property is get-only; the binding threw during view load; the app's global exception handler
swallowed it (`Handled = true`); the render pass aborted mid-tree and everything after kept raw
defaults. One line, five symptoms, invisible to build + tests.

Standing consequences:
- **Never bind a `Run.Text`** to a VM property — use `TextBlock.Text` (+ `StringFormat`), and
  prefer explicit `Mode=OneWay` on display-only bindings.
- **Build-green is not render-green.** Any structural change to `WorkspaceView.xaml` gets the
  static checklist below + the operator's visual check before commit.
- **Don't re-index outer grid rows.** The action bar lives in a third *inner* row of the
  filter-bar's own grid specifically so no other element's `Grid.Row` ever changes.

## Static review checklist (run before AND after any change to these files)

1. **One grid per view** — exactly one DataGrid visible at a time (`ShowMachineGrid` /
   `ShowUpdateGrid`, BoolToVis → Collapsed); no duplicate header block.
2. **No accidental cell-sharing** — Auto rows collapse when empty; nothing stacks unintentionally.
3. **Defaults hidden at idle** — banner, 2016 chip, action bar, STAGED tag all default Collapsed.
4. **Chip radio group intact** — all 8 chips share EnumEquals + `Unchecked="OnFilterChipUnchecked"`.
5. **ItemsSource live** — both grids bind `{Binding Computers}`.
6. **Window startup untouched** — MainWindow is out of bounds for panel work.
7. **Diff scope** — panel work touches ONLY: `WorkspaceView.xaml(.cs)`, `SettingsPage.xaml(.cs)`,
   `HelpContent.cs`, and standalone dialog files. Anything else: stop and flag.

---

## As built

### The VM contract (in `WorkspaceViewModel.cs`, committed in `b078014`/`ecb798e`)

| Member | Type | Role |
|---|---|---|
| `RowFilter.Server2016` | enum value | the chip's filter; predicate is `LcuRouting.Is2016(OsBuild)` |
| `Server2016Count` / `HasServer2016` | int / bool | chip count + visibility; re-raised on rows changing and on `OsBuild` |
| `MonthlyCuDisplay` / `LcuPackagesFolder` | string | action-bar header (reads saved settings; display-only) |
| `CleanUp2016Command` / `Stage2016Command` / `Verify2016Command` | commands | act on selected 2016 rows, or all 2016 when none selected |
| `RebootWave2016Command` | command | **selected 2016 rows ONLY** — never falls back to "all"; no-ops with an activity note |
| `CheckLcuStageReadiness()` → `LcuStageReadiness` | method | read-only package pre-check feeding the guided prompt |
| Filter reset | behavior | when `HasServer2016` flips false while its filter is active, `ActiveFilter` resets to All |

### Filter chip (`WorkspaceView.xaml`)
Copy of the Unhealthy chip: `Tag`/`ConverterParameter` = `RowFilter.Server2016`, icon `Box24`
(`Server24` doesn't exist in WPF-UI), label `{Binding Server2016Count, StringFormat='Server 2016 ({0})'}`,
`Visibility="{Binding HasServer2016, BoolToVis}"`, `Unchecked="OnFilterChipUnchecked"` kept.
Self-populating: appears only once a vitals check confirms a 14393 box; unread boxes never count.

### Action bar (`WorkspaceView.xaml`)
A Border in a **third inner Auto row of the filter-bar grid** (NOT a new outer row). Style
defaults Collapsed; visible only via a **MultiDataTrigger: ActiveFilter == Server2016 AND
HasServer2016** (the second condition kills the orphaned-bar edge when the last 2016 box leaves).
Contents: "This month's CU: KB… / UBR" + "Drop the .msu in <folder>" (TextBlock + StringFormat,
`Mode=OneWay`), the caption "Clean up → Stage today → Reboot Wave tonight → Verify.", and four buttons:
- **Clean up** → `CleanUp2016Command`. Tooltip: "Clears the Windows update backlog (required for
  2016 boxes before staging). Never reboots."
- **Stage** → `Click="OnStage2016"` (NOT a direct command bind — see guided prompt below).
- **Reboot Wave** → `Click="OnRebootWave2016"`, `x:Name="RebootWaveButton"`, `IsEnabled` starts
  False and is driven from `OnGridSelectionChanged`: enabled only while ≥1 selected row is 2016.
- **Verify** → `Verify2016Command`.

### STAGED-box disambiguation (`WorkspaceView.xaml`, update grid Status cell)
The pill is wrapped in a horizontal StackPanel; a second amber-bordered tag
**"STAGED — needs Reboot Wave"** shows only via MultiDataTrigger
(`PatchState == RebootPending` AND `OsBuild == 14393`), default Collapsed. Same amber as a plain
reboot-pending box (no new colour/PatchState) — the tag wording is the disambiguator; a non-2016
reboot-pending row never shows it.

### Reboot Wave confirm (`WorkspaceView.xaml.cs`, `OnRebootWave2016`)
Never bound straight to the command. The handler filters the selection to 2016 rows, **names
them** (up to 10 inline, then "+N more"), and shows a WPF-UI MessageBox: reboots now, gracefully;
**forced after 8 minutes** if a box won't go down (completing the operator-ordered reboot);
tracked until the UBR confirms. Primary "Reboot & commit" invokes the command; anything else does
nothing. Defense in depth: button gate (selection) → confirm → command's own selected-only no-op.

### Guided missing-package prompt (`OnStage2016` + `LcuPackageNeededDialog.xaml(.cs)`)
Stage's Click handler calls `CheckLcuStageReadiness()` **before any box is touched**. Ready →
invoke `Stage2016Command`. Not ready → show the standalone **"Add the Server 2016 update"**
dialog (560×480, CenterOwner): the plain reason + "no machine was touched", what's needed
(KB · arch · ~size MB), the **Catalog link pre-filled to the KB** + open-in-browser button, the
folder path (copy-pasteable + "Open folder", which creates the folder if absent), and "drop the
.msu, then Stage now". **Stage now** loops back to re-check; Cancel stages nothing. An unset KB
gets a "set this month's CU in Settings first" message instead of a resolver error.

### Settings (`SettingsPage.xaml(.cs)`)
"Server 2016 cumulative update" CardExpander (icon `Box24`), following the exact existing
seed-in-`Initialize` + `OnXxxChanged` → `PersistSettings` pattern: **KB** (`LcuKbBox`),
**Target UBR** (`LcuUbrBox`, int-parse, snaps back on junk), **Approx. package size MB**
(`LcuSizeBox`, int-parse, display-only guidance), read-only **x64** label, **CU package folder**
(`LcuPackagesFolderBox` + Browse). Model: `AppSettings.MonthlyCu` (Kb / Arch / TargetUbr /
ExpectedSizeMb / Display) + `AppSettings.LcuPackagesFolder` (default `C:\Vivre\VivrePackages`).

### Help (`HelpContent.cs`)
One topic in Fleet ▸ Patching: **"Patch a Windows Server 2016 box"** — the five-step flow,
a "Why Clean up?" explainer (2016's hidden update backlog; clearing it prevents staging stalls;
never reboots), and a Tip pointing at the Settings section + package folder.

## Hard "don't"s (unchanged, plus the new ones)
- No separate import or manual machine list — it's a **filter** over the one fleet.
- Don't classify boxes in the UI — `LcuRouting.Is2016` via the VM is the single predicate.
- Don't bind Reboot Wave's or Stage's `Command` directly (confirm / pre-check first).
- Don't invent a new `PatchState` or colour for "staged".
- Don't bind `Run.Text`; don't re-index outer grid rows; don't touch MainWindow/shell hosting.
- No credential prompts, no transport wording on operation results.

## Known cosmetic limitation (accepted)
`MonthlyCuDisplay` / `LcuPackagesFolder` raise no change notifications, so an already-open
keep-alive tab shows stale values after a Settings edit until the tab is reopened. The commands
always read the saved settings at execution. Fix would need VM plumbing — deferred deliberately.
