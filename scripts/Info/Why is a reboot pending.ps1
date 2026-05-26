# List the reasons a reboot is pending, if any.
$reasons = @()
if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') { $reasons += 'Component-Based Servicing' }
if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') { $reasons += 'Windows Update' }
if (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction SilentlyContinue) { $reasons += 'Pending file rename' }
$active = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName' -Name ComputerName -ErrorAction SilentlyContinue).ComputerName
$pendingName = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName' -Name ComputerName -ErrorAction SilentlyContinue).ComputerName
if ($active -and $pendingName -and $active -ne $pendingName) { $reasons += 'Pending computer rename' }
try {
    $ccm = Invoke-CimMethod -Namespace 'ROOT\ccm\ClientSDK' -ClassName CCM_ClientUtilities -MethodName DetermineIfRebootPending -ErrorAction Stop
    if ($ccm.RebootPending) { $reasons += 'ConfigMgr client' }
    if ($ccm.IsHardRebootPending) { $reasons += 'ConfigMgr (hard reboot)' }
} catch { }
if ($reasons.Count) { 'Reboot pending: ' + ($reasons -join ', ') } else { 'No reboot pending.' }
