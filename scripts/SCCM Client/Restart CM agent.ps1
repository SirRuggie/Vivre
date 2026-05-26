# Restart the ConfigMgr client agent service (CcmExec).
Restart-Service -Name CcmExec -Force
'CcmExec: ' + (Get-Service -Name CcmExec).Status
