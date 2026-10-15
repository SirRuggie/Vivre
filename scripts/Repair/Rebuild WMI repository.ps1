# Verify the WMI repository; salvage it only after an explicit inconsistent result.
param([scriptblock]$Winmgmt = {
    param($Argument)
    $output = @(& winmgmt $Argument) | Out-String
    [pscustomobject]@{ Output = $output.Trim(); ExitCode = $LASTEXITCODE }
})

$ErrorActionPreference = 'Stop'

function Invoke-Winmgmt([string]$Argument) {
    $result = & $Winmgmt $Argument
    if ($null -eq $result -or $null -eq $result.PSObject.Properties['Output'] -or $null -eq $result.PSObject.Properties['ExitCode']) {
        throw 'WMI command adapter returned no structured exit result.'
    }
    [pscustomobject]@{ Output = ([string]$result.Output).Trim(); ExitCode = [int]$result.ExitCode }
}

function Get-RepositoryState($Result) {
    # Microsoft documents ERROR_INTERNAL_DB_CORRUPTION (1358 / HRESULT 0x8007054E)
    # as the verification result for an inconsistent repository.
    if ($Result.ExitCode -eq 1358 -or $Result.ExitCode -eq -2147023538) { return 'Inconsistent' }
    if ($Result.Output -match '(?i)\brepository is inconsistent\b') { return 'Inconsistent' }
    if ($Result.ExitCode -eq 0 -and $Result.Output -match '(?i)\brepository is consistent\b') { return 'Healthy' }
    if ($Result.ExitCode -ne 0) { return 'Failed' }
    return 'Unrecognized'
}

$before = Invoke-Winmgmt '/verifyrepository'
switch (Get-RepositoryState $before) {
    'Healthy' { 'WMI repository is consistent. No action needed.'; return }
    'Failed' { throw "WMI repository verification failed (exit $($before.ExitCode)): $($before.Output)" }
    'Unrecognized' { throw "WMI repository verification returned an unrecognized response: $($before.Output)" }
}

$salvage = Invoke-Winmgmt '/salvagerepository'
if ($salvage.ExitCode -ne 0) {
    throw "WMI repository salvage failed (exit $($salvage.ExitCode)): $($salvage.Output)"
}

$after = Invoke-Winmgmt '/verifyrepository'
if ((Get-RepositoryState $after) -ne 'Healthy') {
    throw "WMI repository salvage did not produce a verified healthy repository (exit $($after.ExitCode)): $($after.Output)"
}

'WMI repository was inconsistent; salvage completed and the repository is now verified consistent.'
