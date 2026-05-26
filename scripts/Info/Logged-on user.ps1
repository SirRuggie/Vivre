# Interactive user(s): the owner of each explorer.exe process.
$users = Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | ForEach-Object {
    $o = Invoke-CimMethod -InputObject $_ -MethodName GetOwner
    if ($o.ReturnValue -eq 0) { '{0}\{1}' -f $o.Domain, $o.User }
} | Sort-Object -Unique
if ($users) { 'User(s): ' + ($users -join ', ') } else { 'No interactive user logged on.' }
