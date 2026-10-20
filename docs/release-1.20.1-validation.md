# Vivre 1.20.1 release validation — 2026-10-20

Scope: promote the approved changes through `2a0c790` on `master` into release **1.20.1**, as requested by Shaun. This release step changes version/documentation only; application behavior, dependencies and publish paths are unchanged.

## Acceptance ledger

| Requested capability | Status |
| --- | --- |
| App/Explorer/About report 1.20.1 from the single VersionPrefix | Verified: Release solution build passed; executable file version and managed assembly version are 1.20.1.0; product version starts 1.20.1+build; About derives its label from that metadata |
| Release notes contain all approved Unreleased entries | Included under the dated 1.20.1 heading: simplified list organization, save-time folder choice and backlog reconciliation; a fresh Unreleased heading remains |
| README, in-app Help and current documentation agree | Current version references updated; existing Help verified for organizer, save-folder selection, top-level defaults and immediate folder creation |
| Prior independent review and regression evidence remains applicable | Logic unchanged in this step; implementation evidence reused as linked below |
| Commit and push to master | Included in the release metadata commit containing this ledger; remote master verification is required at handoff |
| Signed ZIP and deployment | Shaun will publish on the signing workstation and deploy; not performed in this version update |

## Release checks

- `dotnet build source/Vivre.slnx -c Release --no-restore` passed: zero errors and one existing update-agent signing-certificate warning; the configured certificate was unavailable to this account, so no signed ZIP was produced.
- Compiled EXE file version and DLL assembly version are 1.20.1.0; product version starts 1.20.1+build. The About formatter reads that assembly metadata.
- All 100 local documentation link targets in changed Markdown files resolve; UTF-8, unchanged prior changelog entries and `git diff --check` passed. The application diff is exactly the VersionPrefix change; no publish script, runtime logic or dependency changed.
- No new runtime features, dependency upgrades or server operations are included. Existing native validation gaps and explicit deferrals remain unchanged.

## Reused implementation evidence

- [U-58/U-59 acceptance](list-organization-validation.md): independent review corrections; 25 storage tests passed with one privilege-dependent skip; list-organization, workspace-safety and navigation-clarity probes passed; eight WPF layout renders at two widths in light/dark themes checked.
- [1.20.0 release record](release-1.20.0-validation.md): prior release evidence for the existing People, list and operational UI improvements.
- [Current backlog](vivre-backlog.md) and [UI checklist](vivre-ui-ux-backlog.md): remaining validation and explicit deferrals, not reopened by this release.
- The implementation tests above were not rerun for this metadata-only step; no new whole-app soak, native input, live AD, remote-server or deployed upgrade test is claimed.

## Packaging

From `C:\src\Vivre`, run `.\publish.ps1 -Zip`. Output stays `C:\src\Vivre\publish\Vivre.zip`, with the app folder `C:\src\Vivre\publish\Vivre` and checksum `Vivre.zip.sha256`. No version number is added to the ZIP or folder name. Publishing requires the existing signing certificate/private key in the publishing account; this release update does not claim a signed ZIP or deployment.
