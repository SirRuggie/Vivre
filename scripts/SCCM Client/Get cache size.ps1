$cfg = Get-CimInstance -Namespace 'ROOT\ccm\SoftMgmtAgent' -ClassName CacheConfig -Filter "ConfigKey='Cache'"
$usedKb = 0
Get-CimInstance -Namespace 'ROOT\ccm\SoftMgmtAgent' -ClassName CacheInfoEx | ForEach-Object { $usedKb += $_.ContentSize }
'Cache: {0:N0} MB used of {1:N0} MB  ({2})' -f ($usedKb / 1024), $cfg.Size, $cfg.Location
