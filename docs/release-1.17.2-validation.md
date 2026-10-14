# Release 1.17.2 acceptance ledger

Scope: release preparation, reconciliation of named historical concerns, local validation, and dummy-file checks restricted to Z:\Vivre\testing. No server operations authorized. F12 stays skipped. This is a release candidate; production deployment validation is incomplete.

| Capability / check | Status | Evidence / remaining work |
|---|---|---|
| F13-F20 implementation | Implemented and locally tested; F20 visible behavior unverified | performance implementation report and release performance JSON |
| Release version and ZIP | Implemented and tested | 1.17.2; 862 archived files match publish output; CRC and SHA-256 verified |
| Published agent signature and bundled files | Implemented and tested | Valid Authenticode signature with configured thumbprint; scripts, modules, runtime included |
| Packaged startup | Implemented and tested narrowly | final self-contained EXE survived 15 seconds, created main window and responded; test process stopped afterwards |
| Visible banners / tray / UI interaction | Blocked by unavailable UI helper | Windows automation initialization timed out twice; no visible interaction pass claimed |
| Dummy-file shared-drive staging | Implemented and tested | production CU copy/hash and package folder replacement/rollback on Z:\Vivre\testing\release-1.17.2-20261014 |
| Live scan/install/cleanup/reboot/RDP integration | Blocked by user-only authority | Shaun limited live testing to shared-drive dummy files |
| Historical transport-cache issue | Implemented and tested in prior batch | F10 connection-specific cache and Retry WinRM |
| CU copy integrity (F21) | Implemented and tested | SHA-256 comparison, equal-size corruption rejection, truncation rejection, pre-cancel preservation; reused existing bounded hash helper |
| Monitor-only Stop (F22) | Implemented and tested locally | removed conflicting IsBusy binding; real view-model probe halts empty-workspace monitoring; help and tooltip corrected; visible click not tested |
| Dependency security servicing (F23) | Implemented and tested | Xml/Pkcs 10.0.13; self-contained Core/Desktop runtimes 10.0.13; NuGet audit reports no vulnerable dependencies |
| Release signing enforcement (F24) | Implemented and tested | publisher signs final agent and requires Valid status/matching publisher; missing key fails before replacing output; writes ZIP checksum |
| Multiple scheduled tasks per host | Not implemented | host-keyed _scheduledTasks still stores one timestamp; tracked as an open design/implementation item, not closed by this release |
| Fast reboot watch lifecycle | Explicitly deferred previously | preserves earlier operator decision; no dispatch changes |

## Evidence

- Full Release Core suite after changes and dependency servicing: **1,457 passed**, zero runner skips, 5m27s. Existing privilege-conditional tests can return early; zero reported skips does not establish those cases. Focused CU/SMB/reboot-gate tests: 17 passed.
- Local performance harness completed, including monitor-only Stop and actual offscreen workspace rendering. Harness runtime was .NET 10.0.12; the separately published and startup-tested package bundles .NET 10.0.13. Native RDP remains untested.
- Shared-drive copy: Z:\Vivre\testing\release-1.17.2-20261014\Vivre-1.17.2-win-x64.zip; destination SHA-256 matches the local ZIP.
- ZIP: C:\src\Vivre\publish\Vivre-1.17.2-win-x64.zip (97,861,267 bytes).
- SHA-256: 5AB9E52A5EB65C5CBB18B96CE4DAB8E5EF3B9B81DD9BACB9D9B8673AD9A669FA.
- Agent signature: Valid; thumbprint 1A5CE867A4660C271C9C7AA0DD2F923A1FE05953. Build-time child-process signing still warns; final publisher signing and validation succeeded in the publishing host. Target-machine certificate trust/EDR acceptance is not established by local signature validation.
- [Package evidence](release-1.17.2-package.json), [shared-drive evidence](release-1.17.2-share-smoke.json), [performance evidence](release-1.17.2-performance.json), [dependency audit](release-1.17.2-dependency-audit.json), [startup evidence](release-1.17.2-startup.json), [shared ZIP evidence](release-1.17.2-shared-package.json).

## Tradeoffs and retained limits

CU hash verification adds a source read and destination read per copy. It detects equal-length corruption without retaining whole files in memory. File.Copy and a blocked network filesystem read cannot be interrupted mid-call. No fleet-scale CU throughput claim is made. Shared-drive checks used 1 MiB dummy bytes and dummy folders; they did not start agents or test server servicing.

The historical backlog was reconciled for the named candidates, not exhaustively reclassified. Multiple-task scheduling remains open; fast-watch lifecycle and F12 retain their prior deferrals. No release tag, deployment or live server operation was performed.

Security servicing references: [Microsoft PKCS advisory](https://github.com/advisories/GHSA-846f-f764-76mx) and [Microsoft XML advisory](https://github.com/advisories/GHSA-r955-8q5j-3h83), both identify 10.0.13 as patched. The local dependency audit is a point-in-time package-advisory check, not proof of absence of all vulnerabilities.
