# Free space on every fixed drive.
Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object {
    '{0} {1:N1} GB free of {2:N1} GB' -f $_.DeviceID, ($_.FreeSpace / 1GB), ($_.Size / 1GB)
}
