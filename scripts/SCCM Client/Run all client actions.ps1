# Fire the common ConfigMgr client actions in one go. Good for a freshly-fixed or sluggish client.
$actions = [ordered]@{
    'Machine Policy Retrieval & Evaluation' = '{00000000-0000-0000-0000-000000000021}'
    'Hardware Inventory'                     = '{00000000-0000-0000-0000-000000000001}'
    'Discovery Data Collection (Heartbeat)'  = '{00000000-0000-0000-0000-000000000003}'
    'Software Updates Scan'                  = '{00000000-0000-0000-0000-000000000113}'
    'Software Updates Evaluation'            = '{00000000-0000-0000-0000-000000000108}'
}
foreach ($name in $actions.Keys) {
    Invoke-CimMethod -Namespace 'ROOT\ccm' -ClassName SMS_Client -MethodName TriggerSchedule -Arguments @{ sScheduleID = $actions[$name] } | Out-Null
    "Triggered: $name"
}
