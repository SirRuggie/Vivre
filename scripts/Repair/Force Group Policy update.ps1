# Force a Group Policy refresh on the machine (computer policy). Runs non-interactively.
$out = & gpupdate.exe /target:computer /force | Out-String
'Group Policy update requested.' + [Environment]::NewLine + $out.Trim()
