# Direct Windows Update Agent query (bypasses ConfigMgr). May be slow on the first call.
$searcher = (New-Object -ComObject Microsoft.Update.Session).CreateUpdateSearcher()
$result = $searcher.Search('IsHidden=0 and IsInstalled=0')
'Missing updates (Windows Update): ' + $result.Updates.Count
