# Search installed software by name. Change $Name to your search term ('*' lists everything).
$Name = '*'
$keys = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
)
Get-ItemProperty $keys -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -and $_.DisplayName -like "*$Name*" } |
    Select-Object DisplayName, DisplayVersion, Publisher |
    Sort-Object DisplayName
