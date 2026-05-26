# Log off every interactive session. Users lose unsaved work.
$lines = @(quser 2>$null) | Select-Object -Skip 1
if (-not $lines) { return 'No sessions to log off.' }
$count = 0
foreach ($line in $lines) {
    $id = ($line -split '\s+' | Where-Object { $_ -match '^\d+$' } | Select-Object -First 1)
    if ($id) { logoff $id; $count++ }
}
"Logged off $count session(s)."
