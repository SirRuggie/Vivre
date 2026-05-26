# Force a FULL hardware inventory: clear the action status so a full resync is sent, then trigger.
Get-CimInstance -Namespace 'ROOT\ccm\invagt' -ClassName InventoryActionStatus -Filter "InventoryActionID='{00000000-0000-0000-0000-000000000001}'" | Remove-CimInstance -ErrorAction SilentlyContinue
Invoke-CimMethod -Namespace 'ROOT\ccm' -ClassName SMS_Client -MethodName TriggerSchedule -Arguments @{ sScheduleID = '{00000000-0000-0000-0000-000000000001}' } | Out-Null
'Full hardware inventory triggered.'
