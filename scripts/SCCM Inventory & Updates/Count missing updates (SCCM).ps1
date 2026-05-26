# Updates targeted to this client that are required but not yet installed (per the ConfigMgr client).
$missing = @(Get-CimInstance -Namespace 'ROOT\ccm\ClientSDK' -ClassName CCM_SoftwareUpdate -Filter 'ComplianceState = 0')
'Missing updates (SCCM): ' + $missing.Count
