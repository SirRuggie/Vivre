# Direct Windows Update Agent scan (bypasses ConfigMgr). May be slow on the first call.
# Source: 2 = Windows Update, 1 = Managed (WSUS/SCCM SUP), 3 = Microsoft Update (registers the MU service).
# Human-readable reference for Vivre's embedded scan (Updates/WuaUpdateLane.cs).
param([int]$ServerSelection = 2, [object]$SearchResult)

$ErrorActionPreference = 'Stop'
if ($null -eq $SearchResult) {
    $session  = New-Object -ComObject Microsoft.Update.Session
    $searcher = $session.CreateUpdateSearcher()

    if ($ServerSelection -eq 3) {
        # Microsoft Update — register the service, then select it by ServiceID.
        $muId = '7971f918-a847-4430-9279-4a52d1efe18d'
        try { $null = (New-Object -ComObject Microsoft.Update.ServiceManager).AddService2($muId, 2, '') } catch { }
        $searcher.ServerSelection = 3
        $searcher.ServiceID = $muId
    } else {
        $searcher.ServerSelection = $ServerSelection
    }

    $result = $searcher.Search('IsHidden=0 and IsInstalled=0')
} else { $result = $SearchResult }
if ([int]$result.ResultCode -ne 2) {
    throw "Windows Update search did not complete cleanly (result code $($result.ResultCode), HRESULT 0x80240438) - the update source was not fully reached."
}
'Missing updates: ' + $result.Updates.Count
$result.Updates | ForEach-Object { '  - ' + $_.Title }
