# Trigger Machine Policy Retrieval & Evaluation.
Invoke-CimMethod -Namespace 'ROOT\ccm' -ClassName SMS_Client -MethodName TriggerSchedule -Arguments @{ sScheduleID = '{00000000-0000-0000-0000-000000000021}' } | Out-Null
'Machine policy retrieval & evaluation triggered.'
