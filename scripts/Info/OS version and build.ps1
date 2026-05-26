$os = Get-CimInstance Win32_OperatingSystem
$ubr = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name UBR -ErrorAction SilentlyContinue).UBR
$build = if ($ubr) { '{0}.{1}' -f $os.BuildNumber, $ubr } else { $os.BuildNumber }
'{0} (version {1}, build {2})' -f $os.Caption, $os.Version, $build
