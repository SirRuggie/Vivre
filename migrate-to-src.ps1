#requires -Version 5.1
<#
.SYNOPSIS
    One-time helper to migrate the Vivre repo out of OneDrive to C:\src\Vivre.

.DESCRIPTION
    Run this from the CURRENT (old, OneDrive) repo location. It does five things and
    nothing else -- it never builds, tests, publishes, or touches the old copy:

      1. Verifies the current repo is clean (no uncommitted or untracked changes);
         aborts with a clear warning if it is not. Nothing is cloned on abort.
      2. Reads the GitHub remote URL from `git remote get-url origin` (never hardcoded).
      3. Clones a fresh copy from that remote to C:\src\Vivre.
      4. Shows `git log --oneline -5` from the fresh clone so you can confirm all
         recent commits landed on the remote.
      5. Prints a manual next-steps checklist for you to run after it exits.

    Review it, then run it manually:  .\migrate-to-src.ps1

    ----------------------------------------------------------------------------------
    ALREADY HANDLED -- no action needed for any of these after the move:

      * Code-signing cert lives in the CurrentUser\My store and is looked up by
        THUMBPRINT at build time. It has no path dependency, so it works unchanged
        from C:\src\Vivre.

      * publish.ps1 uses RELATIVE paths ($PSScriptRoot) throughout. No path changes
        are needed anywhere in the codebase after the move.

      * The old OneDrive publish\ output folder: do NOT migrate it. It is git-ignored
        and machine-specific. Let the first fresh `.\publish.ps1` create it at the new
        location (so the output is real on-disk files, not OneDrive placeholders).
    ----------------------------------------------------------------------------------

.PARAMETER Target
    Destination for the fresh clone. Default C:\src\Vivre.
#>
[CmdletBinding()]
param(
    [string]$Target = 'C:\src\Vivre'
)

$ErrorActionPreference = 'Stop'   # for cmdlets; native git is checked via $LASTEXITCODE

function Abort([string]$message) {
    Write-Host ""
    Write-Host "MIGRATION ABORTED" -ForegroundColor Red
    Write-Host "  $message" -ForegroundColor Red
    exit 1
}

# The repo to migrate is wherever this script lives (it is committed in the repo root),
# so it works no matter which directory you launch it from.
$RepoRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($RepoRoot)) { $RepoRoot = (Get-Location).Path }

Write-Host "Migrating Vivre repository" -ForegroundColor Cyan
Write-Host "  from (current) : $RepoRoot"
Write-Host "  to   (new)     : $Target"
Write-Host ""

# Sanity: this script's folder must actually be a git work tree.
$null = git -C $RepoRoot rev-parse --is-inside-work-tree
if ($LASTEXITCODE -ne 0) {
    Abort "This script's folder is not a git repository: $RepoRoot"
}

# --- Step 1 of 4: refuse to migrate a dirty repo ------------------------------
Write-Host "[1/4] Checking the current repo is clean..." -ForegroundColor Cyan
git -C $RepoRoot status
$dirty = git -C $RepoRoot status --porcelain
if ($dirty) {
    Write-Host ""
    Write-Host "Uncommitted or untracked changes were found:" -ForegroundColor Yellow
    $dirty | ForEach-Object { Write-Host "    $_" -ForegroundColor Yellow }
    Abort "Commit, ignore, or remove the changes listed above, then re-run. Nothing was cloned."
}
Write-Host "  Clean -- no uncommitted or untracked changes." -ForegroundColor Green
Write-Host ""

# --- Step 2 of 4: read the remote URL (never hardcoded) -----------------------
Write-Host "[2/4] Reading the GitHub remote URL (origin)..." -ForegroundColor Cyan
$remoteUrl = git -C $RepoRoot remote get-url origin
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($remoteUrl)) {
    Abort "Could not read the 'origin' remote URL. Is a remote named 'origin' configured?"
}
$remoteUrl = $remoteUrl.Trim()
Write-Host "  origin = $remoteUrl" -ForegroundColor Green
Write-Host ""

# --- Step 3 of 4: clone fresh to the new location -----------------------------
Write-Host "[3/4] Cloning fresh to $Target ..." -ForegroundColor Cyan
if (Test-Path -LiteralPath $Target) {
    $existing = Get-ChildItem -LiteralPath $Target -Force -ErrorAction SilentlyContinue
    if ($existing) {
        Abort "Target already exists and is not empty: $Target. Remove it (or pick another path) and re-run."
    }
}
$parent = Split-Path -Parent $Target
if (-not (Test-Path -LiteralPath $parent)) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
}
git clone $remoteUrl $Target
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $Target '.git'))) {
    Abort "git clone failed. Nothing usable at $Target."
}
Write-Host "  Cloned." -ForegroundColor Green
Write-Host ""

# --- Step 4 of 4: confirm recent commits landed -------------------------------
Write-Host "[4/4] Recent commits in the fresh clone (top should match the old location):" -ForegroundColor Cyan
git -C $Target log --oneline -5
Write-Host ""

# --- Done: manual next-steps for you to run after this exits -------------------
Write-Host "Clone complete. Manual next steps (run these yourself):" -ForegroundColor Green
Write-Host ""
Write-Host "  1. Open $Target in your IDE / terminal."
Write-Host "  2. dotnet build source\Vivre.slnx   ->  expect 0 warnings / 0 errors."
Write-Host "  3. dotnet test  source\Vivre.slnx   ->  expect 344 green."
Write-Host "  4. .\publish.ps1 -Zip"
Write-Host "       Then in Explorer, confirm $Target\publish\Vivre-win-x64\ shows SOLID GREEN"
Write-Host "       CHECK icons (fully on disk), NOT cloud / placeholder icons, BEFORE copying"
Write-Host "       to any test box."
Write-Host ""
Write-Host "The old OneDrive copy is untouched -- delete it only after the steps above pass." -ForegroundColor Green
