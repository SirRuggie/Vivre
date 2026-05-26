# Install every update the ConfigMgr client knows is required but missing. May reboot the machine.
$missing = @(Get-CimInstance -Namespace 'ROOT\ccm\ClientSDK' -ClassName CCM_SoftwareUpdate -Filter 'ComplianceState = 0')
if ($missing.Count -eq 0) {
    'No missing updates to install.'
} else {
    Invoke-CimMethod -Namespace 'ROOT\ccm\ClientSDK' -ClassName CCM_SoftwareUpdatesManager -MethodName InstallUpdates -Arguments @{ CCMUpdates = [ciminstance[]]$missing } | Out-Null
    'Installing {0} update(s)...' -f $missing.Count
}
