# Vivre 1.20.0 release validation — 2026-10-20

Scope: promote the approved changes through `8098aba` on `master` into release **1.20.0**. This release step changes version/documentation only; application behavior and dependency versions are unchanged.

## Acceptance ledger

| Capability | Status |
|---|---|
| App/Explorer/About version derives from the single VersionPrefix | Verified: Release solution build passed; executable file version and assembly version are 1.20.0.0, product version begins 1.20.0+build; About derives its label from that metadata |
| Dated release notes include all approved Unreleased entries | Included: saved-tab naming, bookmark-style list folders, U-45–U-57 operational/navigation/People improvements; a fresh Unreleased heading remains |
| README, in-app Help and current documentation agree | Updated current version references; existing Help verified for saved-tab naming, folders, toolbar/CU scope, People review, removal/undo and export/exit choices |
| Prior regression/red-team evidence remains applicable | Application logic unchanged in this step; reuse the implementation evidence linked below |
| Commit and push to master | Included in the release metadata commit containing this ledger; verify remote master at handoff |
| Signed ZIP and deployment | User will publish on the signing workstation; not performed in this version update |

## Release checks

- `dotnet build source/Vivre.slnx -c Release --no-restore` passed with zero errors and the existing single update-agent signing-certificate warning; this account did not produce a signed package.
- Checked compiled EXE file/product version and managed assembly version; reviewed the About metadata formatter. All previous Unreleased changelog entries are preserved verbatim under the 1.20.0 dated heading.
- No new application logic, dependencies, publish paths or runtime settings changed. Prior focused regression results were reused rather than claiming a new full-suite or live-environment run.

## Existing implementation evidence

- [UI/UX backlog](vivre-ui-ux-backlog.md): saved-tab rename and list-folder checks, U-45–U-57 acceptance ledgers and independent review corrections.
- [People validation](people-lookup-validation.md): nine core matching tests, offscreen recovery/export/exit checks, People flow/layout/Stop and 2,000-entry responsiveness probe passed before this metadata-only step.
- Shared operational/navigation checks and folder persistence checks are recorded in their batch ledgers; they are prior execution evidence, not a new full live-environment audit.
- [U-21/U-22 validation](ui-ux-u21-u22-validation.md): native Windows DPI, physical input and spoken screen-reader gaps remain open; deferred S-1/U-40/U-42 work remains deferred.

## Packaging

The unchanged command is `.\publish.ps1 -Zip` from `C:\src\Vivre`. Default output stays `C:\src\Vivre\publish\Vivre.zip`, with extracted app folder `C:\src\Vivre\publish\Vivre` and checksum `Vivre.zip.sha256`. No version number is added to the ZIP or folder name. The signing certificate/private key must be available to the publishing account; source version approval does not claim that a signed ZIP or deployment has been completed.
