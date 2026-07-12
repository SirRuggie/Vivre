<#
    Get-VivreFreezeLog.ps1 — pull the [RDP ...] instrument lines out of Vivre's log.

    Run this ON APVHOP (the box Vivre runs on).

    THE TEST:
        .\Get-VivreFreezeLog.ps1 -Mark      <-- run BEFORE the drag
        ...do the drag...
        .\Get-VivreFreezeLog.ps1            <-- run AFTER  the drag (prints + copies to clipboard)

    Do that twice:  Run A (control drag, no clicking)  then  Run B (drag + clicking, until it freezes).

    Other modes:
        .\Get-VivreFreezeLog.ps1 -All       everything in today's log, ignore the mark
        .\Get-VivreFreezeLog.ps1 -Tail 60   just the last 60 RDP lines
#>

[CmdletBinding()]
param(
    [switch]$Mark,
    [switch]$All,
    [int]$Tail = 0
)

$ErrorActionPreference = 'Stop'

$logDir   = Join-Path $env:LOCALAPPDATA 'Vivre\logs'
$markFile = Join-Path $env:TEMP        'vivre-rdp-mark.txt'
$pattern  = '\[RDP (instrument|uithread|com|modal|stuckcapture|landed)\]'

# ---------------------------------------------------------------- find the newest log
if (-not (Test-Path $logDir)) { throw "No log folder at $logDir  — is Vivre installed on this box?" }

$log = Get-ChildItem -Path $logDir -Filter 'vivre*.log' |
       Sort-Object LastWriteTime -Descending |
       Select-Object -First 1

if (-not $log) { throw "No vivre*.log found in $logDir" }

# ------------------------------------------- read it even while Serilog holds the file
function Read-LockedFile([string]$path) {
    $fs = [System.IO.File]::Open($path,
              [System.IO.FileMode]::Open,
              [System.IO.FileAccess]::Read,
              [System.IO.FileShare]::ReadWrite)
    try {
        $sr = New-Object System.IO.StreamReader($fs)
        try   { , ($sr.ReadToEnd() -split "`r?`n") }
        finally { $sr.Dispose() }
    }
    finally { $fs.Dispose() }
}

$lines = Read-LockedFile $log.FullName

# ---------------------------------------------------------------------------- MARK mode
if ($Mark) {
    "$($log.FullName)|$($lines.Count)" | Set-Content -Path $markFile -Encoding ASCII
    Write-Host ""
    Write-Host "  MARKED at line $($lines.Count) of $($log.Name)" -ForegroundColor Cyan
    Write-Host "  Go do the drag. Then run:  .\Get-VivreFreezeLog.ps1" -ForegroundColor Cyan
    Write-Host ""
    return
}

# ---------------------------------------------------------------------------- GRAB mode
$start = 0
$mode  = 'WHOLE LOG'

if (-not $All -and $Tail -le 0 -and (Test-Path $markFile)) {
    $m = ((Get-Content $markFile -Raw).Trim() -split '\|')
    if ($m[0] -eq $log.FullName) {
        $start = [int]$m[1]
        $mode  = "SINCE MARK (line $start)"
    }
    else {
        Write-Host "  Log rolled over since the mark — showing the whole file instead." -ForegroundColor Yellow
    }
}

if     ($start -ge $lines.Count) { $slice = @() }
elseif ($start -gt 0)            { $slice = $lines[$start..($lines.Count - 1)] }
else                             { $slice = $lines }

$rdp = @($slice | Where-Object { $_ -match $pattern })

if ($Tail -gt 0 -and $rdp.Count -gt $Tail) {
    $rdp  = $rdp[-$Tail..-1]
    $mode = "LAST $Tail RDP LINES"
}

# ------------------------------------------------------------------------------- output
Write-Host ""
Write-Host ("=" * 78) -ForegroundColor DarkGray
Write-Host "  $($log.Name)   |   $mode   |   $($rdp.Count) RDP lines" -ForegroundColor White
Write-Host ("=" * 78) -ForegroundColor DarkGray
Write-Host ""

if ($rdp.Count -eq 0) {
    Write-Host "  *** NO [RDP ...] LINES AT ALL. ***" -ForegroundColor Red
    Write-Host ""
    Write-Host "  That is itself a finding. Either:" -ForegroundColor Red
    Write-Host "    - the instrumented build (d67979b) is not the one running, or" -ForegroundColor Red
    Write-Host "    - no RDP session tab was opened, or" -ForegroundColor Red
    Write-Host "    - the lines are not reaching the log." -ForegroundColor Red
    Write-Host ""
    Write-Host "  Sanity check: open a session and look for '[RDP instrument] armed'." -ForegroundColor Red
    Write-Host ""
    return
}

$rdp | ForEach-Object { Write-Host $_ }

# ------------------------------------------------------------------------------ summary
function MaxOf([string[]]$src, [string]$rx) {
    $vals = @($src | ForEach-Object { if ($_ -match $rx) { [int]$Matches[1] } })
    if ($vals.Count) { ($vals | Measure-Object -Maximum).Maximum } else { 0 }
}

$nGap    = @($rdp | Where-Object { $_ -match '\[RDP uithread\].*gapMs=' }).Count
$nStuck  = @($rdp | Where-Object { $_ -match '\[RDP stuckcapture\]' -and $_ -notmatch 'cleared' }).Count
$nCom    = @($rdp | Where-Object { $_ -match '\[RDP com\]' }).Count
$nLanded = @($rdp | Where-Object { $_ -match '\[RDP landed\]' }).Count
$nModal  = @($rdp | Where-Object { $_ -match '\[RDP modal\] ENTER' }).Count

$maxGap     = MaxOf $rdp 'gapMs=(\d+)'
$maxBlocked = MaxOf $rdp 'blockedMs=(\d+)'
$maxComMs   = MaxOf @($rdp | Where-Object { $_ -match '\[RDP com\]' }) '\bms=(\d+)'
$maxComTot  = MaxOf $rdp 'comMsTotal=(\d+)'

$landedHot = @($rdp | Where-Object { $_ -match '\[RDP landed\]' -and $_ -match 'physBtn=1' }).Count

Write-Host ""
Write-Host ("-" * 78) -ForegroundColor DarkGray
Write-Host "  SUMMARY" -ForegroundColor White
Write-Host ("-" * 78) -ForegroundColor DarkGray
Write-Host ("  uithread gap lines : {0,-6}  max gapMs      = {1}"     -f $nGap,    $maxGap)
Write-Host ("  stuckcapture fired : {0,-6}  max blockedMs  = {1}"     -f $nStuck,  $maxBlocked)
Write-Host ("  slow com calls     : {0,-6}  slowest com ms = {1}"     -f $nCom,    $maxComMs)
Write-Host ("  OCX mutations      : {0,-6}  of those with a button held = {1}" -f $nLanded, $landedHot)
Write-Host ("  modal drags        : {0,-6}  max comMsTotal = {1}"     -f $nModal,  $maxComTot)
Write-Host ""

if     ($nStuck -gt 0)                     { Write-Host "  >> stuckcapture FIRED — lost button-up. This is (b)."           -ForegroundColor Yellow }
elseif ($nGap -gt 0 -and $maxComMs -gt 0)  { Write-Host "  >> Big gap + a slow COM call — likely (a), sync COM on the UI thread." -ForegroundColor Yellow }
elseif ($nGap -gt 0)                       { Write-Host "  >> Big gap, no slow COM call — (a), but maybe not our COM calls." -ForegroundColor Yellow }
else                                       { Write-Host "  >> Quiet. Expected for Run A (control). If this is Run B and it froze, both theories are in trouble." -ForegroundColor Green }
Write-Host ""

# --------------------------------------------------------------------------- clipboard
try {
    $rdp -join "`r`n" | Set-Clipboard
    Write-Host "  Copied $($rdp.Count) lines to the clipboard. Paste them into the chat." -ForegroundColor Cyan
}
catch {
    Write-Host "  (Clipboard copy failed — just select + copy the lines above.)" -ForegroundColor Yellow
}
Write-Host ""