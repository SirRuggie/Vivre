# Remove all items from the ConfigMgr client cache (ccmcache) using the supported client API.
$mgr = New-Object -ComObject UIResource.UIResourceMgr
$cache = $mgr.GetCacheInfo()
$elements = $cache.GetCacheElements()
$count = 0
foreach ($e in $elements) { $cache.DeleteCacheElement($e.CacheElementID); $count++ }
"Removed $count cache element(s)."
