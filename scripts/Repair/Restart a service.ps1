# Restart a named Windows service. Change $ServiceName before running.
$ServiceName = 'Spooler'
Restart-Service -Name $ServiceName -Force
$svc = Get-Service -Name $ServiceName
'{0}: {1}' -f $svc.Name, $svc.Status
