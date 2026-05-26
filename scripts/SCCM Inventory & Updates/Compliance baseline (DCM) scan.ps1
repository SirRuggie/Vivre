# Trigger evaluation of assigned configuration baselines (Desired Configuration Management).
Invoke-CimMethod -Namespace 'ROOT\ccm' -ClassName SMS_Client -MethodName TriggerSchedule -Arguments @{ sScheduleID = '{00000000-0000-0000-0000-000000000110}' } | Out-Null
'Compliance (DCM) baseline evaluation triggered.'
