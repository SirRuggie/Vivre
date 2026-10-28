# Repeat-install checklist and legacy OS routing

## Acceptance ledger

| Requested capability | Final implementation status |
| --- | --- |
| Fresh scans do not inherit installation-cleared checkboxes | Implemented and locally tested through the real scan/install/rescan paths |
| Deliberately unchecked updates remain unchecked | Verified, including all-unchecked, repeated scans and failed-install cases |
| Server 2012/2012 R2 builds classify from existing vitals | Implemented and locally tested through ApplyVitals and WUA dispatch; no new remote reads |
| CU-staged and operation-local reboot approval guards remain intact | Verified by isolated workspace probe and update/servicing guard tests |

Scope: production checklist reconciliation and build parsing, plus isolated tests and operator documentation. No target installs, reboots, security-agent changes, or additional remote probes are authorized by this validation. This change does not claim to resolve the separate APVPatching load incident.

## Findings and correction

- Before the fix, a successful installation cleared selected checkboxes and set `InstalledThisSession`. `ReplaceUpdatesForScope` preserved those false selections into a new scan while dropping the completion flags. A still-applicable result therefore appeared available but a repeat install skipped it as “No updates selected.” A fresh Applicable scan now uses the default selected state for previously completed items, retaining genuine operator opt-outs and Installed-scope uninstall choices. Without a new scan, the completed selection remains unchecked.
- `LcuRouting.ParseBuild` accepted only `10.0.<build>`, although the reported Server 2012 R2 vitals already contained `6.3.9600`. It now recognises Windows-captioned 6.0–6.3 and 10.0 version tokens. Only build 14393 can enter the opted-in CU lane; missing or malformed builds remain unknown. Recognising a build does not guarantee update availability or all remote transports on that OS.

## Validation — 2026-10-28

- Before-fix regressions: six OS-parser cases failed (legacy versions and malformed-token rejection); the real workspace probe failed at “Fresh applicability inherited the automatic post-install deselection.”
- After-fix update suite: **780 passed, 0 failed, 0 skipped**, including 36 OS-routing cases. Command: `dotnet test source/Vivre.Core.Tests/Vivre.Core.Tests.csproj -c Release -p:RestoreLockedMode=true --filter FullyQualifiedName~Vivre.Core.Tests.Updates`.
- `InstallOverrideProbe` plus `RepeatInstallProbe`: passed using actual `WorkspaceViewModel` with inert `IPatchService`; six scans and six captured installs in the new probe. Verified same-KB/new-title and same-title applicability, deliberate/all-unchecked skips, failed-install behavior, Installed-scope choices, legacy vitals routing, partial-vitals preservation, unknown OS skip and staged-CU refusal. Existing approval checks still observed `[true, false, false]` for approved immediate, subsequent unapproved immediate, and scheduled operations.
- Probe command: `dotnet run --project tools/PerformanceAudit/PerformanceAudit.csproj -c Release -p:RestoreLockedMode=true -- --install-override-only <output.json>`; output parent directory must exist. Production startup, monitor and real remote dependencies are suppressed.
- Release solution build: **0 errors**, one existing missing local agent code-signing certificate warning. No signing policy changed.
- README, in-app Help, patching guide, repository guidance and backlog/index updated. Version remains **1.20.2**; these fixes accumulate under **Unreleased**.

Limits: source and synthetic-response integration validation, not a live Server 2012/R2 install, target reboot, native confirmation-dialog exercise or production deployment. No ZIP produced. Private test/incident artifacts remain outside the commit. The screenshot's staged CU still requires its approved reboot; an offline unknown-OS row is not assumed to be Server 2012.
