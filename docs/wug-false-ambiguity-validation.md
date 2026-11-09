# WUG false-ambiguity correction — 2026-11-09

Point-in-time record; finalized with this change. Subsequent field results belong in the living backlog.

## Acceptance ledger

| Capability | Status |
| --- | --- |
| Compare working and regressed resolvers | Compared parent of 614b2d9 with current source |
| Restore unique matches with label/IP host fields | Implemented and locally tested under Windows PowerShell 5.1 |
| Prevent choosing a different device among duplicate matches | Verified locally: distinct valid ID, exact-name/IP and qualified-name conflict safeguards retained |
| Confirm affected production fleet | Not tested; manual lookup failed TLS authentication before returning device records |

## Evidence and correction

Commit 614b2d9 introduced an identity-conflict check alongside the necessary distinct-ID checks. The conflict check rejected any non-equivalent name/hostName pair, although its stated purpose was to reject contradictory qualified DNS names. This rejects operator labels and IP-valued hostName fields. The previous resolver accepted these matches, but also selected the first matching name; that unsafe first-pick behavior is not restored.

Reproduced with the exact current resolver under Windows PowerShell 5.1, using an inert single-device inventory: id 601, name TULVMANHOUR, hostName/networkAddress 10.20.0.1. Before correction it returned Ambiguous with hits=1. No WUG connection or device mutation was performed.

The corrected conflict check compares two qualified non-IP names only. Every resolution still requires exactly one distinct valid positive scalar device ID. Multiple distinct matching IDs, missing/invalid IDs, conflicting qualified names, failed searches and substring-only collisions remain guarded. DNS cannot select a device after an ambiguous name result. Read and maintenance paths use the same resolver. Existing SSL callback handling and request count are unchanged.

The operator's standalone Connect-WUGServer attempt failed certificate validation before reading devices. It did not exercise Vivre's compiled certificate callback and does not establish that Vivre's TLS path failed. The actual production device fields remain unknown, so this is a locally reproduced compatibility fix rather than a confirmed fleet recovery.

## Validation

- Baseline resolver suite: 36 passed before correction. The separate exact-source single-device probe reproduced Ambiguous/hits=1 before correction.
- After correction: complete WUG suite **168 passed, 0 failed, 0 skipped**. Command: `dotnet test source/Vivre.Core.Tests/Vivre.Core.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~Vivre.Core.Tests.Wug`. Includes five new cases for unique label/IP/alias matches and captured maintenance enable/disable targeting. Existing duplicate-ID, missing-ID, conflicting-FQDN, prefix/IP-collision, search-error, pool, and TLS tests passed without weakening their assertions.
- Release desktop build passed: **0 errors, 1 existing missing local agent-signing certificate warning**. Command: `dotnet build source/Vivre.Desktop/Vivre.Desktop.csproj -c Release -p:RestoreLockedMode=true`.
- An initial overlapping baseline test/build attempt encountered a locked test assembly; it made no code diagnosis. After baseline completion, the complete suite above built and passed.
- In-app Help reviewed: existing unique-identity, ambiguous-device and retry instructions remain accurate; no workflow changed, so HelpContent.cs is unchanged.
- Tests use local dummy inventory and captured maintenance calls; no production changes were performed. No independent-agent review is claimed. Version remains 1.20.2, change under Unreleased; no ZIP produced.

## Reference

[Progress device information](https://docs.progress.com/bundle/whatsupgold-user-help-23-0/page/Using-Device-Properties-Device-information.html) documents editable display names independently of polling identity.