# WUA SYSTEM task logon compatibility

Point-in-time record for 2026-10-28; finalized with this change and not edited after the fact. Follow-up results belong in the living backlog.

## Acceptance ledger

| Capability | Status |
| --- | --- |
| Identify the failing step using existing target evidence | Confirmed: PowerShell event 4100 identifies Register-ScheduledTask / HRESULT 0x80070057 at the reported failure time |
| Explicitly describe the intended SYSTEM task identity | Implemented and locally tested; generated statement resolves to SYSTEM / ServiceAccount / Highest |
| Preserve task timing, installation behavior and reboot guards | Single principal-argument correction; 782 update regression tests passed |
| Confirm installation on Server 2012 R2 | Not tested: target task-registration tests were declined; production checks remain read-only |

## Evidence and scope

The reported target runs Windows Server 2012 R2 (6.3.9600), PowerShell 4.0 and .NET Framework release 528049. Creating task objects in memory succeeded, reporting SYSTEM / LogonType None / Highest and Compatibility Win7 / PT12H. Existing PowerShell logs subsequently located the failure in Register-ScheduledTask, before Vivre reaches Start-ScheduledTask. The nearby CreateObjectTask timeout belongs to a Windows task and does not establish the cause of Vivre's registration error.

The WUA bootstrap omitted LogonType. It now explicitly supplies ServiceAccount for the existing LocalSystem SID S-1-5-18. Microsoft documents ServiceAccount for LocalSystem, LocalService and NetworkService; this removes provider-dependent inference without changing the identity, privileges or credentials. The specific rejected parameter is not named in the event, so this remains a compatibility correction with a likely incident connection, not a proven resolution on the affected target.

No retries, transport fallback, triggers, execution limits, task compatibility level, servicing guards, remote scripts outside this bootstrap, or CU operations are changed. No target task registrations, starts, deletions, installs or reboots are part of validation. No version bump or ZIP is included.

## Validation

- Update regression suite: **782 passed, 0 failed, 0 skipped**. Command: `dotnet test source/Vivre.Core.Tests/Vivre.Core.Tests.csproj -c Release -p:RestoreLockedMode=true --filter FullyQualifiedName~Vivre.Core.Tests.Updates --no-restore`. Includes immediate and scheduled principal-contract cases, existing generated-script parsing, streaming and servicing guards.
- Executed only the principal statement extracted from production source in local Windows PowerShell **5.1.26100.9444**. It produced SYSTEM / SID S-1-5-18 / ServiceAccount / Highest. No task was registered, started, changed or deleted.
- In-app Help reviewed: this changes task setup internally; install steps, approval choices, progress and reboot instructions remain accurate, so HelpContent.cs is unchanged.
- Release solution build passed: **0 errors, 1 existing missing local agent-signing certificate warning**. The initial no-restore build found a stale agent assets file without net48/win-x86; rerunning with locked restore repaired only generated assets and passed. Command: `dotnet build source/Vivre.slnx -c Release -p:RestoreLockedMode=true`. Version remains 1.20.2 with this change under Unreleased; no ZIP produced.
- No independent-agent review or live Server 2012 R2 / PowerShell 4 registration test is claimed. Local principal construction and regression tests do not prove the target failure is resolved.

## References

- [Microsoft TASK_LOGON_TYPE](https://learn.microsoft.com/en-us/windows/win32/api/taskschd/ne-taskschd-task_logon_type): ServiceAccount is the documented built-in service-account logon mode.
- [Microsoft New-ScheduledTaskPrincipal](https://learn.microsoft.com/en-us/powershell/module/scheduledtasks/new-scheduledtaskprincipal): creates an in-memory principal and accepts the explicit logon type.
