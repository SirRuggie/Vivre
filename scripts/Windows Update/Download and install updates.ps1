# Scan, download and install all applicable updates directly from Windows Update / WSUS.
# Bypasses ConfigMgr deployment rules and may reboot. Can take a long time.
$session  = New-Object -ComObject Microsoft.Update.Session
$searcher = $session.CreateUpdateSearcher()
$result   = $searcher.Search("IsInstalled=0 and IsHidden=0 and DeploymentAction='Installation'")
if ($result.Updates.Count -eq 0) { return 'No applicable updates found.' }

$toProcess = New-Object -ComObject Microsoft.Update.UpdateColl
foreach ($u in $result.Updates) { $null = $toProcess.Add($u) }

$downloader = $session.CreateUpdateDownloader()
$downloader.Updates = $toProcess
$null = $downloader.Download()

$toInstall = New-Object -ComObject Microsoft.Update.UpdateColl
foreach ($u in $toProcess) { if ($u.IsDownloaded) { $null = $toInstall.Add($u) } }

$installer = $session.CreateUpdateInstaller()
$installer.Updates = $toInstall
$r = $installer.Install()
'Installed {0} update(s). Result code: {1}. Reboot required: {2}' -f $toInstall.Count, $r.ResultCode, $r.RebootRequired
