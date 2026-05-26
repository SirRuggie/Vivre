$os = Get-CimInstance Win32_OperatingSystem
$up = (Get-Date) - $os.LastBootUpTime
'Up {0}d {1}h {2}m  (last boot {3:yyyy-MM-dd HH:mm})' -f $up.Days, $up.Hours, $up.Minutes, $os.LastBootUpTime
