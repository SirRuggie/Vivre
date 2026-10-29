# Vivre

A Windows desktop tool for managing Microsoft Configuration Manager (SCCM / MEMCM) clients at
scale. Load a list of computers into a tabbed grid, ping them, pull SCCM client health, and run
client actions or arbitrary PowerShell against the ones you select.

The name is from the One Piece *Vivre Card* — a slip that tracks a person's life force. Every row
in the grid is a Vivre Card for one machine: online or dark, here or there, healthy or wounded.
(See **Help ▸ About Vivre** in the app for the full story.)

Built with **.NET 10 / WPF** ([WPF-UI](https://github.com/lepoco/wpfui) Fluent styling).

## Features

- **People email lookup** — use Add or Paste list (one person per line) on the right, then Review matches and Copy all resolved emails on the toolbar. Unique enabled exact AD matches resolve automatically; uncertain matches require your choice. Drag the divider above the review panel for more room; disabled accounts and missing emails are identified. Open review or double-click revisits a selected row; right-click offers review, edit, and selected-field copying. Actions contains retry and CSV export. Uses your current Windows login and current AD domain; results are session-only.

- **Layout and keyboard clarity** — wrapping filters/script actions, visible focus rings, keyboard tab menus, and explicit field/status labels; native DPI and spoken screen-reader validation remain tracked in the UI/UX checklist.

- **Search and recovery** — filter saved-list menus and script libraries; restore deleted lists without overwriting existing lists.

- **Clear activity and settings** — Activity identifies its originating tab, including search/copy; CU KB/build/month save together, and invalid numeric settings explain the correction inline.

- **Menu target scope** - machine actions identify this machine, selected count, all-tab scope, or shown rows; changed menu targets require reopening. Cancelling schedules reviews the captured machines before removing all Vivre install/reboot schedules.
- **Actions dropdown** — selected-machine shortcuts open the existing Check software, Stage software, Run script, or WhatsUp Gold maintenance dialogs; Remove offline and Clear results are labeled **all in tab** and work without a selection. Right-click retains all commands.
- **Fleet toolbar** — Patching starts with Scan/Install; Ping, Check Vitals and Monitor stay together, with Stop visible. Empty tabs offer Paste machines and Open list directly.
- **Per-tab update options** — each Patching tab owns its update source, driver setting, exclusions, and applicable/installed scope; **Update options** opens its settings, the source appears in the tab header/banner and button tooltip, and new tabs start with Windows Update.
- **Tab saving** - right-click a Health or Patching tab and choose Save tab as list; a successful save names that tab after the list without switching tabs or losing results.
- **Opening saved lists** — empty tabs open directly; populated tabs offer Append list, Replace list, or New tab (default). Append preserves existing results. If the list is already open in the same Health/Patching section, OK switches to that tab without changing its machines or results; Cancel stays put. Reopening a list in its current tab still offers Append/Replace, while New tab avoids a duplicate.
- **Organizing saved lists** — Lists ▾ ▸ **Organize lists…** opens one searchable window: select a list, choose a folder and click Move; create, rename or remove folders there. **Save tab as list** also has a folder dropdown and New folder action. New lists default to **Top level (no folder)**; overwriting an existing list preserves its folder unless explicitly changed. Right-click **Move to folder…** remains available. Removing folders keeps the lists; list contents and open-tab results stay unchanged. This simplified flow is included in 1.20.1; see [validation](docs/list-organization-validation.md).
- **List names** — date/time punctuation such as `/`, `:` and `|` is preserved; legacy lists remain accessible without automatic renaming.
- **List clarity** — each tab shows its saved-list identity and membership changes; Paste and Quick Add accept the same separators, and machine names stay visible while scrolling results.
- **Workspace protection** — Paste adds without resetting existing results; explicit replacement and Clear confirm removals and refuse while work is active. Saved-list overwrites require approval and reject stale revisions; window and tray Exit warn about active or scheduled work.
- **Health to Patching** — right-click a Health tab and choose Open tab in Patching to copy all names into a distinctly named Patching tab; the source stays intact and normal monitoring/Auto-check on load apply, without starting update scans, installs, or reboots.
- **Tabbed workspaces** — each tab is an independent set of machines and operations, run side by side. Text searches settle briefly before refreshing large lists; background activity and completion banners keep bounded UI queues.
- **Ping All** — reachability, shown as colour-coded status dots.
- **Check Vitals** — one read-only pull per machine that gathers SCCM client health (site code, agent
  version, reboot-pending / missing-updates / install-running / users-online, last reboot) **and** deep
  OS vitals (disk, memory, CPU, uptime, OS build), scored into a 0–100 **vitality** chip (green/amber/red)
  with a fleet tally and an **Unhealthy** filter — at-a-glance triage of which boxes are sick. Stopped
  auto-services and recent error events are shown for context but not scored (too noisy on their own).
  From a machine's **Details ▸ Vitals** you can **act in place** — start a stopped service, free up
  disk space, or end a runaway process — and the score re-checks on the spot (a per-machine **Check
  Vitals** there reads just that one box). The vitality chip shows on both the Health and Patching grids.
  General disk cleanup reports full/partial/failed outcomes, verifies Windows Update service recovery,
  and protects known staged packages. It is coordinated with patching and staging across tabs; the
  Server 2016 component-store Clean up remains a separate operation.
- **Patching clarity** — per-tab source/driver/exclusion summaries, separate Install anyway and forced-reboot choices, and scheduling with target counts and host timezone.
- **Legacy Windows task startup** — Windows Update tasks explicitly specify their SYSTEM logon method. Local compatibility checks passed; the reported Server 2012 R2 registration failure still needs target validation ([details](docs/wua-system-task-logon-validation.md)).
- **Repeat scans** — newly applicable results are selected again after a successful install; deliberate checklist exclusions remain unchecked. Check Vitals recognises Server 2012/2012 R2 builds for the normal Windows Update lane, without extra remote calls.
- **Reboot approval** — concise counts, scope, and force consequences; reboot actions use the exact
  approved target snapshot even if the selection changes while the prompt is open.
- **Right-click actions** — SCCM client triggers (machine policy, hardware inventory, update scan,
  …), Run PowerShell (one machine, the selection, or all), and Enable WinRM (over DCOM).
  **Retry WinRM** performs a fresh connection check after a repair, clearing a cached Kerberos
  rejection only when verified. Changed credentials get a fresh attempt without restarting Vivre.
- **Run Script** — pick a saved script or paste one; per-machine output lands on each row.
- **Stage software** — copy a package (a single MSI/EXE, or a folder of files) to the selected
  machines (default `C:\Windows\Temp\VivrePackages`), with a per-machine result. Uses the fast SMB
  admin share (like SCCM/PsExec), falling back to WinRM (chunked + SHA-256 verified) where SMB is
  blocked. WinRM streams a disk-backed ZIP in bounded chunks and cleans transfer-owned temporary files;
  unreachable cleanup is recorded for retry on the next staging request to that host.
  Each replacement is verified separately before publication; failed transfers preserve the
  previous package, and replacement failures report recovery guidance. Vivre delivers the files;
  you run the install your way (e.g. Run script…). No install
  runs, so EDR agents that disrupt WinRM mid-install can't break it.
- **Windows Update lane** — scan, install, uninstall, and schedule updates per machine with live
  progress (a built-in BatchPatch-style patcher), with operator-tunable install concurrency
  (**Settings ▸ Max simultaneous installs**) and a separate **Server 2016 CU lane** (opt-in
  per box, for the boxes whose Express-delta CUs break under normal Windows Update). See `docs/windows-patching-lane.md`.
- **Reboot & verify** — after an operator-confirmed reboot, Vivre watches each box offline → back →
  ready, then auto-rescans and reports a plain outcome (Server 2016 boxes are build-verified first).
  Uncertain backup-channel reboot requests remain unconfirmed until restart evidence is observed;
  service creation alone is never reported as a confirmed reboot.
- **WhatsUp Gold maintenance** — put the selected machines into / out of WUG maintenance mode, with a
  pre-flight connection + module check — and a read-only **Check WhatsUp Gold state** action that
  reports each machine's current maintenance state without changing anything. Ambiguous device names
  are skipped with full-name retry guidance; mixed batches report actual coverage.
- **Personal preferences** — changes save in order without disk writes on the UI thread. Normal exit
  waits up to three seconds for pending saves and warns if the latest preferences may not be saved.
- **Check software** — see whether a named product is installed on each machine (a read-only registry
  check) in its own Software column; when WinRM is down on a box it falls back to a read-only DCOM
  channel, so Kerberos-broken machines still answer.
  Results distinguish **Installed**, **Not installed**, and **Could not check**. Partial reads retain
  proven matches with warnings; service-query failures remain unknown. CSV exports include reasons.
- **Custom columns** — run a PowerShell one-liner per machine into its own grid column; a column
  manager hides/shows built-in columns, adds predefined ones, and remembers your layout.
- **Cross-Domain RDP** — an embedded remote-desktop manager in its own left-nav section (machine-gated
  to the designated jump box): a folder tree of hosts with saved, DPAPI-protected logins and
  credential inheritance. Sessions re-fit to the pane as you resize, zoom to match a high-DPI display
  (the session itself stays at 100% scale so Failover Cluster Manager's menus keep working), and can
  go full screen. Signing out inside the remote closes its tab; any other disconnect keeps the tab
  with a Reconnect button. Saved connections and encrypted credentials use replacement saves and
  recoverable backups; failed reads block destructive empty saves and report the recovery needed.
- **Filter & report** — filter the grid by name or state (Updates / Reboot pending / Errors /
  Offline / Done), **Select shown** to act on just that subset, and **Export to CSV** for a
  maintenance-window write-up.
- **Named machine lists** and a **searchable activity log** (in-app panel + rolling file).
- Session credentials (current login by default, or supply your own in Settings).

## Operation details

Linux identification: Health and Patching show a small Linux label beside hostnames when the current AD domain advertises a recognized Linux OS. Background batched LDAP reads use the current Windows login without RSAT or target login. Hover the label for the AD value; directory metadata can be stale. This display-only label does not change scans or enable Linux operations. Missing/unreachable AD and other-domain records remain unlabeled.

Server 2016 **Install CU (no reboot)** copies and installs the full CU package using DISM; it can affect performance and requires a later reboot to complete. The **CU lane** badge only identifies the configured patching route, not completed installation. Use the row menu **Use CU lane** / **Remove CU lane flag** to change that route.

**Settings ▸ Patching ▸ Max simultaneous CU file transfers** controls per-host CU stage checks, large file copies and verification across tabs in a running Vivre instance. It defaults to **2**, accepts **1–200**, and is saved per user. Changes apply to waiting transfers; active transfers finish normally. Waiting rows say **Queued**; after a transfer is verified, its server installs while the next transfer proceeds, independently of this file limit. The initial package-choice precheck remains separate. Higher values increase controller disk/network load. This does not change **Max simultaneous installs**, target selection or reboot approval, cap bandwidth, or coordinate separate Vivre processes. See [settings validation](docs/cu-transfer-settings-validation.md) and [incident limits](docs/cu-transfer-load-validation.md).

In Health and Patching, **Paste** offers **Add to this tab**, **Replace this tab's machine list**, or **Create a new tab with this list**. New-tab paste selects a fresh tab in the same section with default update settings and keeps the original results intact.

Script saves ask before replacing an existing destination and reject stale approvals; declining preserves editor text. Custom-column names must be unique (case-insensitive); duplicate additions keep the existing script and values without executing a replacement.

Scripts explains when the open script is retained despite not matching a search. Scripts and Help offer **Clear search** when nothing matches; Fleet's existing **Clear filter** restores hidden rows. Scrollbars support track-click paging in both directions with a larger pointer area around the slim thumb.

Column widths and order are remembered separately for Health, Patching, People results, and People review. Right-click a Fleet grid and choose **Reset column layout**; in People, use **Actions → Reset results column layout** or **Reset review column layout**. Names stay pinned first. Changing the product in Check software clears the previous product’s service and uses a remembered suggestion only for the new product.

People shows **Showing X of Y** when filtering; **Clear filters** restores the view without changing results. **Review matches (N)** walks all pending matches, including filtered-out entries, and disables at zero; selected-row review remains available. **Copy all resolved emails** includes filtered-out resolved rows and removes duplicates; right-click copies only selected rows. An empty People view offers Paste list, while a filtered empty view offers Clear filters.

To correct an accidental People addition, select its rows and right-click **Remove from results**; **Undo removal** restores the last removed rows and decisions until the next result change. Removal is local only and waits for adding/lookup to finish. On exit, **Export CSV and exit**, **Exit without exporting**, or **Cancel** makes the choice explicit; copying emails alone does not save original inputs or unresolved entries. Export cancellation/failure keeps the session open. Stop and wait before exporting on exit; a normal CSV export during lookup is labeled as a snapshot.

CU **Install / Verify** use eligible selected machines, or eligible machines in the tab only when nothing is selected; their scope count is shown beside the actions. An ineligible selection never targets other rows. In the mixed-install dialog, **Skip CU — install other N** continues only the remainder; **Cancel**, Escape and close start no installations. Cleanup shows **Cleaning up** consistently in the grid and machine details.

In **Settings → Remote credentials**, Apply works for either credential choice, including switching back to Windows login. The **Applied** line shows the active identity separately from edits; credentials remain session-only, and SMB/DCOM fallback still uses the Windows login.

## Roadmap

- (Optional) an **SCCM-deployment update lane**, if updates ever get deployed through SCCM here.
- Remaining validation: native Windows scaling/layout and keyboard/screen-reader checks (U-21/U-22); People responsiveness (U-39) was confirmed resolved by the operator on 2026-10-20; see [the UI/UX backlog](docs/vivre-ui-ux-backlog.md).
- The [2026-10-20 UI/UX review](docs/ui-ux-full-spectrum-review-2026-10-20.md) records U-45–U-57, included in release 1.20.0. Current evidence, deferred items and native-validation limits are in [the living backlog](docs/vivre-ui-ux-backlog.md).
- **Refactor (someday):** the per-tab `WorkspaceViewModel` is large — it could be split into a
  `HostMonitor` + `PatchController` for readability. Purely cosmetic and lowest priority; do it on its
  own (it touches the load-bearing monitor/reboot logic, now backed by tests). Two full audits — a
  multi-agent code review (2026-06) and a five-lens audit (2026-07, `docs/archive/vivre-audit-findings.md`) —
  have been worked off; live status of any remaining findings is tracked in `docs/vivre-backlog.md`.

## Build & run

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), version 10.0.303 or a later servicing patch in that feature band (see `global.json`). Core dependencies are recorded in `packages.lock.json`; use `dotnet restore source/Vivre.Core/Vivre.Core.csproj --locked-mode` to verify the recorded graph.

```
dotnet build  source\Vivre.slnx
dotnet test   source\Vivre.slnx
dotnet run --project source\Vivre.Desktop
```

## Publish / deploy to another machine

Current release: **1.20.2** on the default branch **master**. Build the stable-name package with `.\publish.ps1 -Zip`. Output is `C:\src\Vivre\publish\Vivre.zip`; the app and manifest retain their version and commit identity. Keep this output path for subsequent releases. Older versioned archives, `Vivre-win-x64.zip`, and `Vivre-audited-8b0c2be.zip` are historical artifacts, not this release.

Use `publish.ps1` (wraps `dotnet publish` of the Desktop project):

```
.\publish.ps1            # self-contained win-x64 -> publish\Vivre\ (no .NET needed on target)
.\publish.ps1 -Zip       # same, plus publish\Vivre.zip to copy over
.\publish.ps1 -FrameworkDependent   # small build; target needs the .NET 10 Desktop Runtime (x64)
.\publish.ps1 -Runtime win-arm64
```

The default self-contained x64 build uses `publish\Vivre` and `publish\Vivre.zip`. Other runtimes and framework-dependent builds retain distinct output names; `-OutputDir` still overrides the destination. A shortcut targets the extracted `Vivre.exe`, not the ZIP: keep extracting into the same deployment folder to preserve existing shortcuts. Shortcuts pointing directly to the old `publish\Vivre-win-x64\Vivre.exe` need a one-time update to `publish\Vivre\Vivre.exe`.

Publish requires the configured agent code-signing certificate and private key in `CurrentUser\My`; it fails if the final agent signature is not valid. ZIP builds also write a `.zip.sha256` checksum file and verify every archived file against the candidate folder before replacing the last-good artifacts. Output folders must be new/empty or carry the Vivre publish ownership marker. For an older nonempty output without a marker, choose a fresh `-OutputDir` under `publish` instead of deleting or force-adopting it. Competing publishes to the same target are refused. The self-contained release bundles .NET 10.0.13.

Copy the output folder (or the zip) to the target and run `Vivre.exe`. The curated `Scripts\`
library ships in the output and seeds `%APPDATA%\Vivre\Scripts` on first run. (`publish\` is
git-ignored.)

## Repo layout

- `source/` — the solution (`Vivre.slnx`): `Vivre.Core`, `Vivre.Desktop`, `Vivre.UpdateAgent` (the tiny
  net48 EXE run as SYSTEM on the target for WUA install/uninstall), `Vivre.Core.Tests`.
- `tools/RemoteRun` — small console to test remote PowerShell against a host.
- `scripts/` — the curated PowerShell script library (PS7 / `Get-CimInstance`), organised into
  category folders. Shipped with the app and seeded into `%APPDATA%\Vivre\Scripts` on first
  run; opened from the grid's right-click **Run script…**. See `scripts/README.md`.
- `docs/windows-patching-lane.md` — the Windows patching (WUA) lane: how it works and its reliability constraints.

See [CHANGELOG.md](CHANGELOG.md) for version history.
