# Common fix for stuck Windows Update / download issues: stop the services, clear the
# SoftwareDistribution download cache, and start them again.
Stop-Service -Name wuauserv, bits -Force -ErrorAction SilentlyContinue
$sd = Join-Path $env:windir 'SoftwareDistribution'
if (Test-Path $sd) {
    $old = "$sd.old"
    if (Test-Path $old) { Remove-Item $old -Recurse -Force -ErrorAction SilentlyContinue }
    Rename-Item $sd $old -Force -ErrorAction SilentlyContinue
}
Start-Service -Name wuauserv, bits -ErrorAction SilentlyContinue
'Windows Update cache reset (SoftwareDistribution cleared) and services restarted.'
