# Release 1.17.3 completion ledger

Supersedes the 1.17.2 candidate; previous evidence remains historical. F12 and fast-watch lifecycle retain their explicit deferrals. Remote operations remain unauthorized; only Z:\Vivre\testing is authorized for dummy/shared files.

| Capability | Status | Evidence / remaining step |
|---|---|---|
| Multiple schedules per host | Implemented and tested locally | Track actual task names, advance to earliest pending, retain uncertain replacement times |
| Schedule display wiring | Implemented and tested locally | Actual WorkspaceViewModel probe verifies next-time/count, later-task retention and cancellation-warning preservation |
| Completion banner overflow/order/dismissal | Implemented and tested locally | Actual MainWindow/InfoBar: 30 completions, 25 omitted, four recent queued, correct severity/order/dismissal |
| Visible tray and interactive UI | Blocked by unavailable computer-use helper | Initialization timed out again with a 60-second budget |
| Full regressions | Implemented and tested | 1,462 Core Release tests passed, 5m21s, including reboot primitive gate; zero runner skips does not establish privilege-conditional branches |
| Signed ZIP and shared copy | Implemented and tested | 862 ZIP files match publish output; final agent signature Valid; shared-copy SHA-256 matches |
| Server patch/cleanup/reboot and RDP integration | Blocked by user-only authorization | User limited live work to testing folder |

Not a claim that every historical backlog feature or operational test is complete.

## Package and runtime evidence

- Local ZIP: C:\src\Vivre\publish\Vivre-1.17.3-win-x64.zip (97,863,421 bytes).
- Shared ZIP: Z:\Vivre\testing\release-1.17.3-20261014\Vivre-1.17.3-win-x64.zip.
- SHA-256: ED6C1F55E29FCB609428C3B33561DE79D7F90EACD0AA7448F55A854070DF3B59.
- Agent signature: Valid, expected thumbprint 1A5CE867A4660C271C9C7AA0DD2F923A1FE05953. Publisher checks succeeded despite the existing MSBuild child-process signing warning.
- Self-contained startup: main window created, responsive and process alive after 15 seconds; test process then stopped. No computer lists or operations were started.
- Full local performance harness completed. Uses installed .NET 10.0.12; published app bundles 10.0.13. Banner control rendered offscreen with production application resources. Two initial harness setup failures were corrected (entry-assembly icon resolution); these were not production-app failures.
- Evidence: [package](release-1.17.3-package.json), [shared copy](release-1.17.3-shared-package.json), [signature](release-1.17.3-signature.json), [startup](release-1.17.3-startup.json), [banner-only check](release-1.17.3-banners.json), [complete harness](release-1.17.3-performance.json).

## F25 behavior and limits

Scheduled install results carry the exact locally generated remote task name. Tracking uses host/task identity, so unique install tasks coexist and a confirmed same-name reboot registration replaces only that task. An unconfirmed replacement retains earlier and proposed trigger times as possibilities. The row shows the earliest remaining time and other-task count. Passing a time advances the display; it is not execution proof. Verified cancellation clears the host; failed/uncertain cancellation retains conservative tracking and warning text.

This tracks tasks observed by one workspace session. It does not enumerate remote tasks, reconstruct tasks after restart, coordinate other tabs/operators, or prove which tasks survived a partial cancellation. Those are not new capabilities claimed here. Scheduled-task scripts, reboot primitives, dispatch counts and cancellation behavior are unchanged. No live task registration/cancellation or server operation was performed.

## Remaining dependencies

Visible interaction/tray and native RDP require a working UI helper or operator observation; helper initialization timed out again after 60 seconds. Live scan/install/cleanup/reboot validation requires Shaun's specifically named targets and operation authorization, which the testing-folder-only constraint does not provide. These remain unverified, not passed. Existing explicit F12 and fast-watch deferrals remain unchanged.
