# Restart ONLY if the machine actually has a reboot pending. Safe to run against everything:
# machines with nothing pending are left alone.
$pending = $false
if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') { $pending = $true }
if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') { $pending = $true }
if (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction SilentlyContinue) { $pending = $true }
try {
    $ccm = Invoke-CimMethod -Namespace 'ROOT\ccm\ClientSDK' -ClassName CCM_ClientUtilities -MethodName DetermineIfRebootPending -ErrorAction Stop
    if ($ccm.RebootPending -or $ccm.IsHardRebootPending) { $pending = $true }
} catch { }

if ($pending) {
    shutdown.exe /r /t 5 /f /c "Vivre: restarting now."
    'Reboot pending -> restart scheduled in 5 seconds.'
} else {
    'No reboot pending. Nothing to do.'
}
