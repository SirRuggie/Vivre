# Vivre 1.19.2 validation — 2026-10-20

Scope: the previously accumulated saved-list display-name correction and U-41's already-open list behavior. This is local release preparation, not a signed package or live-server validation. No remote operations were authorized or run.

Implementation commit: `cac0fc7` on `master`.

| Requested capability | Status and evidence |
| --- | --- |
| Keep punctuation in saved-list display names without losing file safety | Implemented before this release; the Core storage tests cover punctuation, collisions, reload, revision checks, delete and restore using temporary dummy files. |
| Recognize an already-open list in the same Fleet section, despite tab renaming | Implemented; the focused tab probe checks case-insensitive saved-list identity and Health/Patching separation with dummy workspaces. |
| OK selects the existing tab; Cancel leaves the workspace alone | Implemented; the focused probe invokes the production selection path with controlled OK/Cancel responses and checks the selected tab, tab count and preserved result. Native physical clicks remain unverified. |
| Preserve current-tab Append/Replace and avoid a duplicate from New tab | Implemented; the focused probe verifies current-tab detection differs from other-tab detection. Existing list-choice and workspace-safety behavior remains in the regression checks. |
| Align release metadata, README, in-app help and backlog | Updated for 1.19.2; the executable file/product version is checked after the final build. |

Local checks:

- Release solution build passed with zero errors; the configured update-agent signing certificate is absent in this build account, producing one warning. The compiled `Vivre.exe` reports file version `1.19.2.0` and a product version beginning `1.19.2`.
- Core regression suite: **1,575 passed, 2 skipped, 0 failed**. No dependency or Core behavior changed in this release.
- `--tab-handoff-only` passed with dummy workspaces and fake offline transports. The strengthened checks cover renamed tabs, case-insensitive list identity, same-name lists in separate sections, empty current tabs, controlled OK/Cancel responses, preserved results and changed membership, current-tab Append/Replace access, and no duplicate from New tab.
- `--workspace-safety-only` passed with dummy storage, including saved-list opening and replacement protections.

These checks exercise production handlers and view models but do not physically click the native prompt or certify visual layout. No signed ZIP was produced here; build and sign the deployable package on the workstation that holds the configured certificate.

Outside this release: U-40 and U-42 are deferred by Shaun; S-1 remains a separate security/availability decision; U-21/U-22 native DPI, physical keyboard and spoken screen-reader validation remain open. No live patch, reboot, deployment or signed package validation is claimed.
