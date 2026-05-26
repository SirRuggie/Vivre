# Changelog

## Renamed to Vivre (2026-05-27)

The app was renamed from **Collection Commander** to **Vivre** (French *"to live"*; the One Piece
*Vivre Card* tracks a person's life force — every grid row is a card for one machine). Full rename:
projects/namespaces `CMCollCtr.*` → `Vivre.*`, solution `Vivre.slnx`, the app ships as `Vivre.exe`,
and a new **Help ▸ About Vivre** dialog tells the story. Per-user data moved from
`%APPDATA%/%LOCALAPPDATA%\CMCollCtr` to `\Vivre`, migrated automatically on first run (old folder
kept as a backup). The legacy `source/CMCollCtr` reference below is the *original* .NET Framework
app's name and is left as historical fact.

## Rewrite — .NET 10 / WPF

Collection Commander was rebuilt from the legacy .NET Framework 4.8 WinForms app into a modern
.NET 10 / WPF application. The legacy projects (`source/CMCollCtr`, `source/plugin.collctr.*`)
were removed at cutover; they remain in git history.

### Architecture
- **.NET 10**, WPF + [WPF-UI](https://github.com/lepoco/wpfui) (Fluent), CommunityToolkit.Mvvm,
  manual composition root (DI) in `App.xaml.cs`.
- `CMCollCtr.Core` (non-UI) + `CMCollCtr.Desktop` (WPF) + `CMCollCtr.Core.Tests` (xUnit, 58 tests).
- **Microsoft.PowerShell.SDK 7.6** runspace host (replaces `System.Management.Automation 3.0` /
  the `sccmclictr.automation` library). Remote over WinRM verified against a live host.
- **Microsoft.Management.Infrastructure** (CIM) over DCOM for Enable-WinRM. Serilog for logging.

### Features
- Tabbed independent workspaces; status/health icon columns + relative last-reboot.
- Ping (ICMP) vs Check (ping + SCCM health, credential-aware, not gated on ICMP).
- Right-click client actions (ported cm12 schedule GUIDs), Run PowerShell (one/selected/all) with
  per-row output, Enable WinRM over DCOM.
- Named machine lists; searchable in-app activity log + rolling file; app icon + system tray.
- Session-only credentials (current login by default; explicit creds via Settings).

### Fixed from the legacy app
- The "health check keeps running after uncheck" bug — impossible by construction (async +
  cancellation token tears down ping + PowerShell together).
- Silent `catch {}` swallowing — failures now surface to the grid / activity log.
- Process-architecture registry mis-read, `Application.DoEvents()` / `Thread.Sleep` races,
  domain-name-as-key password "encryption", and the dead `sa` SQL connection string — all dropped.

### Not ported (by decision)
- RuckZuck plugin (dead upstream), the duplicate localpsscripts plugin, the SCCM Client Center
  launcher, and the MMC console extension.

### Remaining backlog
See `REBUILD_PLAN.md` §0: per-tab credential override + marker, and optional minimize-to-tray.
