$svc = Get-Service -Name CcmExec -ErrorAction SilentlyContinue
if ($svc) { 'CM agent (CcmExec): ' + $svc.Status } else { 'CM agent (CcmExec) is NOT installed.' }
