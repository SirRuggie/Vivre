[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('Vivre-validation-' + [guid]::NewGuid().ToString('N')))
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $destination) {
    throw 'Use a new output directory so earlier validation evidence is not overwritten.'
}
[IO.Directory]::CreateDirectory($destination) | Out-Null
$report = [ordered]@{
    StartedUtc = [DateTime]::UtcNow.ToString('o')
    Scope = 'Local build, Core tests and isolated Desktop performance probes; no live servers or production operations.'
    PublishSafety = 'Not run'
    Build = 'Not run'
    CoreTests = 'Not run'
    DesktopProbes = 'Not run'
    LiveServicing = 'Not tested'
    NativeRdpSoak = 'Not tested'
    ScreenReader = 'Not tested'
    SignedRelease = 'Not produced; inspect build signing warnings and use publish.ps1 for a separately authorized release'
}
Push-Location $repo
try {
    try {
        & (Join-Path $PSScriptRoot 'Test-PublishSafety.ps1') *> (Join-Path $destination 'publish-safety.log')
        $report.PublishSafety = 'Passed under the invoking PowerShell version'
    }
    catch { $report.PublishSafety = 'Failed'; throw }
    & dotnet build source/Vivre.slnx -c Release --nologo -p:RestoreLockedMode=true *> (Join-Path $destination 'build.log')
    if ($LASTEXITCODE -ne 0) { $report.Build = 'Failed'; throw 'Build failed; see build.log.' }
    $report.Build = 'Passed; inspect build.log for warnings, including signing availability'
    & dotnet test source/Vivre.Core.Tests/Vivre.Core.Tests.csproj -c Release --no-build --logger 'trx;LogFileName=core.trx' --results-directory $destination *> (Join-Path $destination 'tests.log')
    if ($LASTEXITCODE -ne 0) { $report.CoreTests = 'Failed'; throw 'Core tests failed; see tests.log and core.trx.' }
    [xml]$testResults = Get-Content -LiteralPath (Join-Path $destination 'core.trx') -Raw
    $counts = $testResults.TestRun.ResultSummary.Counters
    $report.CoreTests = "Passed: $($counts.passed); failed: $($counts.failed); not executed: $($counts.notExecuted). See core.trx for prerequisite reasons."
    & dotnet run --project tools/PerformanceAudit/PerformanceAudit.csproj -c Release -- (Join-Path $destination 'desktop-probes.json') *> (Join-Path $destination 'desktop-probes.log')
    if ($LASTEXITCODE -ne 0) { $report.DesktopProbes = 'Failed'; throw 'Desktop probes failed; see desktop-probes.log.' }
    $report.DesktopProbes = 'Passed isolated probes only; no live/native integration claim'
}
finally {
    Pop-Location
    $report.CompletedUtc = [DateTime]::UtcNow.ToString('o')
    $report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'validation.json') -Encoding utf8
    Write-Host "Validation evidence: $destination"
}
