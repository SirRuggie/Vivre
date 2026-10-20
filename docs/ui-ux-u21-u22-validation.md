# U-21/U-22 — layout and accessibility validation

Date: 2026-10-19. Status: **product fixes implemented; automated validation passed; native-environment validation remains open.** Current release: 1.19.1; evidence below was collected during development from the 1.18.0 baseline. This is not a full accessibility certification or a claim that every layout combination is verified.

## Implemented

- Fleet filters use a separate wrapping row, keeping search and Select shown reachable; the Patching first-run hint can wrap correctly.
- Server 2016 captions/actions and script save controls wrap instead of extending past their available width.
- CU comparison dialog can resize; comparison text and footer wrap at its 460-DIP minimum.
- Filter chips have a separate contrasting focus ring; tab-close buttons have visible focus and names including their tab title.
- Menu key/Shift+F10 opens the focused tab's context menu, including Open tab in Patching.
- Named inputs cover list/paste, scripts, exclusions, software, deployment, maintenance/WUG, custom columns, and settings.
- Icon status cells expose Yes/No/Unknown, progress state and Activity severity; dialog errors/filter feedback reuse polite live-region support without announcing every row-progress tick.

## Evidence

| Check | Result | Boundary |
| --- | --- | --- |
| Debug and Release builds | Passed | Existing missing agent-signing-certificate warning; no signed package |
| Health/Patching layout | 48 cases passed: 560/800/1100 DIP widths × 100/125/150/200% raster scale × dark/light | 420-DIP height; checks visible filter bounds and remaining grid height; includes Server 2016 chip, not every active action-bar state |
| Five dialogs | Minimum/default widths checked and rendered in both themes | Name prompt, Paste, Exclusions, Schedule, CU comparison; horizontal action bounds, not exhaustive text/input assertions |
| CU comparison | 460 and 580 DIP layouts passed | Dummy long values; selected renders visually reviewed |
| Settings/Scripts | Field labels and narrow toolbar layout checked; renders inspected | Not every expanded settings card or every library content combination |
| Keyboard focus | Offscreen Focus/MoveFocus reached filters; selected focus ring contrasted in both themes | Programmatic traversal, not physical keyboard input |
| Tab menu | Focused tab Menu-key handler opened correct context menu; close title/focus checked | Controlled offscreen event, not physical Apps/Shift+F10 sequence |
| Automation | Input/filter names, online Yes/Unknown, reboot No, polite error setting checked | Automation properties/peers, not spoken output |
| Name dialog keys | Default/Cancel button wiring asserted | Offscreen access-key attempt timed out; physical Enter/Escape remains unverified, not classified as a product defect |
| Regression | Existing audit-fixes/script execution/editor, settings clarity, and tab handoff probes passed | Local dummy data/inert services; no live servers or production settings changes |
| Independent red team | Separate gpt-5.6-sol review found a checked-chip focus-color conflict; fixed and re-reviewed without another concrete finding | Reviewer inspected code/tests and did not rerun parent-owned tests |

Reproduce local checks from the repository:

```powershell
dotnet run --project tools/PerformanceAudit -c Release -- --layout-accessibility-only .artifacts/u21-u22.json
```

The probe creates PNGs under `.artifacts/u21-renders/dark` and `light`; generated images/JSON are local evidence, not committed production files. Raster output at 96/120/144/192 DPI does **not** exercise actual Windows monitor scaling, a per-monitor DPI change, or OS text scaling. Initial renders were corrected to apply the actual app theme and inherited foreground before visual review.

## Still required to close U-21 and U-22

Use a normal Windows desktop with Vivre and dummy machine names; no server operations are needed.

- [ ] Actual Windows display scale 100%, 125%, 150%, 200%; test the main window at minimum size and maximized, including moving between differently scaled monitors where available.
- [ ] Inspect all main sections, expanded settings cards, Server 2016 action bar, Software/Deploy/WUG dialogs, Help, Details, and RDP views; check scrolling, labels, footer reachability, and text enlargement.
- [ ] Physical Tab/Shift+Tab, arrows, Menu key/Shift+F10, Enter and Escape through tabs, list menus, Paste, Save/Rename/Restore, script selection, and dialogs; confirm focus is visible and no operation starts merely from focusing a control.
- [ ] Narrator or equivalent: hear input labels, selected tab, status Unknown/Yes/No, validation errors, and operation scope; check reading order and avoid duplicate/chatty announcements.

These checks need a real interactive validation session. U-21/U-22 remain unchecked in the backlog until that evidence is recorded; the implemented improvements are committed independently of this outstanding validation.
