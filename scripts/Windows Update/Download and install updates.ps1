# Scan, download and install all applicable updates directly from the chosen source.
# Bypasses ConfigMgr deployment rules and may reboot. Can take a long time.
# Source: 2 = Windows Update, 1 = Managed (WSUS/SCCM SUP), 3 = Microsoft Update.
# -Exclude drops updates whose title matches any term (e.g. -Exclude 'SQL','Silverlight').
#
# NOTE: run from an interactive/SYSTEM context — WUA install fails inside a WinRM
# network logon (WU_E_NO_INTERACTIVE_USER). Vivre's app runs this as a one-time SYSTEM
# scheduled task (Updates/WuaUpdateLane.cs); this script is the standalone reference.
param([int]$ServerSelection = 2, [string[]]$Exclude = @())

$ErrorActionPreference = 'Stop'
$session  = New-Object -ComObject Microsoft.Update.Session
$searcher = $session.CreateUpdateSearcher()

if ($ServerSelection -eq 3) {
    $muId = '7971f918-a847-4430-9279-4a52d1efe18d'
    try { $null = (New-Object -ComObject Microsoft.Update.ServiceManager).AddService2($muId, 2, '') } catch { }
    $searcher.ServerSelection = 3
    $searcher.ServiceID = $muId
} else {
    $searcher.ServerSelection = $ServerSelection
}

$result = $searcher.Search("IsInstalled=0 and IsHidden=0 and DeploymentAction='Installation'")

$applicable = @()
foreach ($u in $result.Updates) {
    $skip = $false
    foreach ($x in $Exclude) { if ($u.Title -like "*$x*") { $skip = $true; break } }
    if (-not $skip) { $applicable += $u }
}
if ($applicable.Count -eq 0) { return 'No applicable updates found.' }

$installed = 0; $failed = 0; $rebootRequired = $false
foreach ($u in $applicable) {
    $coll = New-Object -ComObject Microsoft.Update.UpdateColl
    $null = $coll.Add($u)

    $downloader = $session.CreateUpdateDownloader()
    $downloader.Updates = $coll
    $null = $downloader.Download()

    $installer = $session.CreateUpdateInstaller()
    $installer.Updates = $coll
    $r = $installer.Install()
    if ($r.ResultCode -eq 2) { $installed++ } else { $failed++ }
    if ($r.RebootRequired) { $rebootRequired = $true }
}

'Installed {0}, failed {1}, reboot required: {2}' -f $installed, $failed, $rebootRequired
