# Common fix for stuck Windows Update / download issues. Every failed phase is surfaced,
# and service recovery is attempted even when cache replacement fails.
param(
    [scriptblock]$StopServices = { Stop-Service -Name wuauserv, bits -Force -ErrorAction Stop },
    [scriptblock]$StartServices = {
        $startErrors = @()
        foreach ($service in @('wuauserv', 'bits')) {
            try { Start-Service -Name $service -ErrorAction Stop }
            catch { $startErrors += "${service}: $($_.Exception.Message)" }
        }
        if ($startErrors.Count -gt 0) { throw ($startErrors -join '; ') }
    },
    [scriptblock]$ResetCache = {
        $sd = Join-Path $env:windir 'SoftwareDistribution'
        if (Test-Path -LiteralPath $sd) {
            $old = "$sd.old"
            if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Recurse -Force -ErrorAction Stop }
            Rename-Item -LiteralPath $sd -NewName (Split-Path -Leaf $old) -ErrorAction Stop
        }
    }
)

$ErrorActionPreference = 'Stop'
$phase = 'stop services'
$operationError = $null
$recoveryError = $null
try {
    & $StopServices
    $phase = 'reset cache'
    & $ResetCache
} catch {
    $operationError = "Windows Update cache reset failed during ${phase}: $($_.Exception.Message)"
} finally {
    try { & $StartServices } catch { $recoveryError = "service recovery failed: $($_.Exception.Message)" }
}

if ($operationError -or $recoveryError) {
    throw (@($operationError, $recoveryError) | Where-Object { $_ }) -join '; '
}

'Windows Update cache reset completed and services were restarted successfully.'
