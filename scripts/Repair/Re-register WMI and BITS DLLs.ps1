# Re-register the COM DLLs commonly involved in WMI / BITS / Windows Update failures.
$dlls = @(
    'actxprxy.dll','atl.dll','Bitsprx2.dll','Bitsprx3.dll','browseui.dll','cryptdlg.dll',
    'dssenh.dll','gpkcsp.dll','initpki.dll','jscript.dll','mssip32.dll','msxml3.dll','msxml6.dll',
    'ole32.dll','oleaut32.dll','Qmgr.dll','Qmgrprxy.dll','rsaenh.dll','sccbase.dll','scrrun.dll',
    'shdocvw.dll','slbcsp.dll','softpub.dll','urlmon.dll','vbscript.dll','Wintrust.dll',
    'wuapi.dll','wuaueng.dll','wucltui.dll','wups.dll','wups2.dll','wuweb.dll'
)
$n = 0
foreach ($d in $dlls) {
    $path = Join-Path $env:windir "system32\$d"
    if (Test-Path $path) { Start-Process regsvr32.exe -ArgumentList "/s `"$path`"" -Wait -NoNewWindow; $n++ }
}
"Re-registered $n DLL(s)."
