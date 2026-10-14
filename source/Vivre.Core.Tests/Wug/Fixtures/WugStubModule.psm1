# Vivre test fixture - NOT the real WhatsUpGoldPS module.
# Imported through the same override seam as production's module import. State-pool tests shadow these
# functions; maintenance tests configure canned inventory and capture exact mutation IDs without live WUG.
# Pure ASCII on purpose: this fixture is imported by the ISS directly, never written through the
# UTF-8-with-BOM WritePs51ScriptAsync helper the em-dash-bearing shell-out scripts require.
function Connect-WUGServer { }
function Get-WUGDevice {
    [CmdletBinding()]
    param([string]$SearchValue, [string]$View)
    if ($env:VIVRE_WUG_TEST_ERROR_NAMES -and @($env:VIVRE_WUG_TEST_ERROR_NAMES -split "`n") -contains $SearchValue) {
        Write-Error "stub search failed: $SearchValue"
        return
    }
    if (-not $env:VIVRE_WUG_TEST_INVENTORY_JSON) { return }
    $inventory = ConvertFrom-Json $env:VIVRE_WUG_TEST_INVENTORY_JSON
    foreach ($item in $inventory) {
        if ((($item.PSObject.Properties.Name -contains 'name') -and $item.name -like "*$SearchValue*") -or
            (($item.PSObject.Properties.Name -contains 'hostName') -and $item.hostName -like "*$SearchValue*") -or
            (($item.PSObject.Properties.Name -contains 'displayName') -and $item.displayName -like "*$SearchValue*") -or
            (($item.PSObject.Properties.Name -contains 'networkAddress') -and $item.networkAddress -like "*$SearchValue*")) {
            Write-Output $item
        }
    }
}
function Set-WUGDeviceMaintenance {
    [CmdletBinding()]
    param([int[]]$DeviceId, [bool]$Enabled, [string]$Reason)
    if ($env:VIVRE_WUG_TEST_MUTATION_FILE) { Add-Content -LiteralPath $env:VIVRE_WUG_TEST_MUTATION_FILE -Value ("$Enabled`:" + (@($DeviceId) -join ',')) }
    if ($env:VIVRE_WUG_TEST_PARTIAL_SET -eq '1') {
        return @{ successfulOperations = 1; errors = 1; success = $false }
    }
    return @{ successfulOperations = @($DeviceId).Count; errors = 0; success = $true }
}
Export-ModuleMember -Function *
