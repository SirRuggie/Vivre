# Trigger a software-update source scan, then an evaluation cycle.
Invoke-CimMethod -Namespace 'ROOT\ccm' -ClassName SMS_Client -MethodName TriggerSchedule -Arguments @{ sScheduleID = '{00000000-0000-0000-0000-000000000113}' } | Out-Null
'Software update scan triggered.'
Invoke-CimMethod -Namespace 'ROOT\ccm' -ClassName SMS_Client -MethodName TriggerSchedule -Arguments @{ sScheduleID = '{00000000-0000-0000-0000-000000000108}' } | Out-Null
'Software update evaluation triggered.'
