# Wipe and re-request all client policy. Forces a fresh policy download on the next cycle.
Invoke-CimMethod -Namespace 'ROOT\ccm' -ClassName SMS_Client -MethodName ResetPolicy -Arguments @{ uFlags = [uint32]1 } | Out-Null
'Policy reset requested.'
