namespace Vivre.Core.Remediation;

/// <summary>Builds the self-reporting PowerShell used by general disk cleanup.</summary>
internal static class DiskCleanupScript
{
    internal static string Build(IEnumerable<string>? protectedPaths, string testPrelude = "")
    {
        string literals = string.Join(", ", (protectedPaths ?? [])
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(PsLiteral));

        return $$"""
        {{testPrelude}}
        $ErrorActionPreference = 'Stop'
        $failures = [System.Collections.Generic.List[string]]::new()
        $skippedLocked = 0
        $skippedProtected = 0
        $skippedReparse = 0
        $removedItems = 0
        $completed = 0
        $serviceStoppedByCleanup = $false
        $recoveryVerified = $true
        $protected = @({{literals}})
        $protected += @(
            (Join-Path $env:SystemRoot 'Temp\VivrePackages'),
            (Join-Path $env:SystemRoot 'Temp\VivreStage'),
            (Join-Path $env:TEMP 'VivrePackages'),
            (Join-Path $env:TEMP 'VivreStage'),
            (Join-Path $env:ProgramData 'Vivre')
        )

        function Normalize-Path([string] $path) {
            if ([string]::IsNullOrWhiteSpace($path)) { return $null }
            try { return [System.IO.Path]::GetFullPath($path).TrimEnd('\') }
            catch { return $null }
        }

        function Test-FullyQualifiedWindowsPath([string] $path) {
            return $path -match '^[A-Za-z]:[\\/]' -or $path -match '^\\\\[^\\]+\\[^\\]+'
        }

        $normalizedProtected = @()
        foreach ($pathToKeep in $protected) {
            $normalized = Normalize-Path $pathToKeep
            if (-not $normalized -or -not (Test-FullyQualifiedWindowsPath $pathToKeep)) {
                [PSCustomObject]@{ outcome = 'Failed'; reclaimed = $null; newFreePercent = $null; safeToReleaseHost = $true; message = "Disk cleanup blocked: invalid protected path '$pathToKeep'" }
                return
            }
            $normalizedProtected += $normalized
        }
        $protected = @($normalizedProtected | Select-Object -Unique)

        function Test-Protected([string] $candidate) {
            $full = Normalize-Path $candidate
            if (-not $full) { return $true }
            foreach ($keep in $protected) {
                if ($full.Equals($keep, [StringComparison]::OrdinalIgnoreCase) -or
                    $full.StartsWith($keep + '\', [StringComparison]::OrdinalIgnoreCase) -or
                    $keep.StartsWith($full + '\', [StringComparison]::OrdinalIgnoreCase)) { return $true }
            }
            $name = [System.IO.Path]::GetFileName($full)
            return $name -like 'VivreStage_*.zip' -or $name -like 'Vivre_Stage_*.zip'
        }

        function Test-SharingViolation([Exception] $exception) {
            while ($exception) {
                $code = $exception.HResult -band 0xFFFF
                if ($exception -is [System.IO.IOException] -and ($code -eq 32 -or $code -eq 33)) { return $true }
                $exception = $exception.InnerException
            }
            return $false
        }

        function Remove-SafeItem([System.IO.FileSystemInfo] $item, [string] $component) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { $script:skippedReparse++; return $true }
            if (Test-Protected $item.FullName) { $script:skippedProtected++; return $true }
            $ok = $true
            if ($item.PSIsContainer) {
                try { $children = @(Get-ChildItem -LiteralPath $item.FullName -Force -ErrorAction Stop) }
                catch {
                    $failures.Add("$component enumeration '$($item.Name)': $($_.Exception.Message)")
                    return $false
                }
                foreach ($child in $children) {
                    if (-not (Remove-SafeItem $child $component)) { $ok = $false }
                }
                try { $remaining = @(Get-ChildItem -LiteralPath $item.FullName -Force -ErrorAction Stop) }
                catch {
                    $failures.Add("$component final enumeration '$($item.Name)': $($_.Exception.Message)")
                    return $false
                }
                if ($remaining.Count -gt 0) { return $ok }
            }
            try { Remove-Item -LiteralPath $item.FullName -Force -ErrorAction Stop }
            catch {
                if (Test-SharingViolation $_.Exception) { $script:skippedLocked++; return $ok }
                $failures.Add("$component '$($item.Name)': $($_.Exception.Message)")
                return $false
            }
            $script:removedItems++
            return $ok
        }

        function Clear-Children([string] $path, [string] $component) {
            if (-not (Test-Path -LiteralPath $path)) { return $true }
            $ok = $true
            $rootFull = Normalize-Path $path
            $systemRootFull = Normalize-Path $env:SystemRoot
            $driveRoot = if ($rootFull) { [System.IO.Path]::GetPathRoot($rootFull).TrimEnd('\') } else { $null }
            if (-not $rootFull -or -not (Test-FullyQualifiedWindowsPath $path) -or
                $rootFull.Equals($driveRoot, [StringComparison]::OrdinalIgnoreCase) -or
                $rootFull.Equals($systemRootFull, [StringComparison]::OrdinalIgnoreCase)) {
                $failures.Add("$component root is unsafe; cleanup skipped")
                return $false
            }
            try { $rootItem = Get-Item -LiteralPath $path -Force -ErrorAction Stop }
            catch {
                $failures.Add("$component root: $($_.Exception.Message)")
                return $false
            }
            if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                $failures.Add("$component root is a reparse point; cleanup skipped")
                return $false
            }
            try { $items = @(Get-ChildItem -LiteralPath $path -Force -ErrorAction Stop) }
            catch {
                $failures.Add("$component enumeration: $($_.Exception.Message)")
                return $false
            }
            foreach ($item in $items) {
                if (-not (Remove-SafeItem $item $component)) { $ok = $false }
            }
            return $ok
        }

        function Read-Drive {
            try {
                $disk = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$env:SystemDrive'" -ErrorAction Stop
                if ($null -eq $disk -or $null -eq $disk.FreeSpace) { return $null }
                return $disk
            } catch { return $null }
        }

        function Wait-ServiceState([System.ServiceProcess.ServiceControllerStatus] $status) {
            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            do {
                $current = Get-Service -Name wuauserv -ErrorAction Stop
                if ($current.Status -eq $status) { return $current }
                Start-Sleep -Milliseconds 250
            } while ([DateTime]::UtcNow -lt $deadline)
            throw "Windows Update service did not reach $status"
        }

        $before = Read-Drive
        try { $activeOperations = @(Get-Process -ErrorAction Stop | Where-Object { $_.ProcessName -in @('Vivre.UpdateAgent', 'dism', 'TiWorker') }) }
        catch {
            [PSCustomObject]@{ outcome = 'Failed'; reclaimed = $null; newFreePercent = $null; safeToReleaseHost = $true; message = "Disk cleanup blocked: couldn't inspect active servicing operations: $($_.Exception.Message)" }
            return
        }
        if ($activeOperations.Count -gt 0) {
            $names = @($activeOperations | Select-Object -ExpandProperty ProcessName -Unique) -join ', '
            [PSCustomObject]@{
                outcome = 'Failed'
                reclaimed = $null
                newFreePercent = $null
                safeToReleaseHost = $true
                message = "Disk cleanup blocked: active servicing operation ($names)"
            }
            return
        }
        try {
            $tempOk = $true
            foreach ($tempPath in @($env:TEMP, (Join-Path $env:SystemRoot 'Temp')) | Select-Object -Unique) {
                if (-not (Clear-Children $tempPath 'temporary files')) { $tempOk = $false }
            }
            if ($tempOk) { $completed++ }

            try { Clear-RecycleBin -Force -ErrorAction Stop; $completed++ }
            catch { $failures.Add("recycle bin: $($_.Exception.Message)") }

            $service = $null
            try { $service = Get-Service -Name wuauserv -ErrorAction Stop }
            catch { $failures.Add("Windows Update service query: $($_.Exception.Message)") }

            if ($service) {
                $wasRunning = $service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running
                $wasStopped = $service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped
                $canCleanCache = $wasRunning -or $wasStopped
                if (-not $canCleanCache) { $failures.Add("Windows Update service state is $($service.Status); cache cleanup skipped") }
                try {
                    if ($wasRunning) {
                        $serviceStoppedByCleanup = $true
                        Stop-Service -Name wuauserv -ErrorAction Stop
                        $service = Wait-ServiceState ([System.ServiceProcess.ServiceControllerStatus]::Stopped)
                    }
                } catch {
                    $canCleanCache = $false
                    $failures.Add("Windows Update service stop: $($_.Exception.Message)")
                }

                try {
                    if ($canCleanCache -and (Clear-Children (Join-Path $env:SystemRoot 'SoftwareDistribution\Download') 'Windows Update cache')) { $completed++ }
                } finally {
                    if ($serviceStoppedByCleanup) {
                        try {
                            $service = Get-Service -Name wuauserv -ErrorAction Stop
                            if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) { Start-Service -Name wuauserv -ErrorAction Stop }
                            $service = Wait-ServiceState ([System.ServiceProcess.ServiceControllerStatus]::Running)
                            $serviceStoppedByCleanup = $false
                            $recoveryVerified = $true
                        } catch { $recoveryVerified = $false; $failures.Add("Windows Update service recovery: $($_.Exception.Message)") }
                    }
                }
            }
        } catch {
            $failures.Add("cleanup: $($_.Exception.Message)")
        } finally {
            if ($serviceStoppedByCleanup) {
                try {
                    $service = Get-Service -Name wuauserv -ErrorAction Stop
                    if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) { Start-Service -Name wuauserv -ErrorAction Stop }
                    $service = Wait-ServiceState ([System.ServiceProcess.ServiceControllerStatus]::Running)
                    $serviceStoppedByCleanup = $false
                    $recoveryVerified = $true
                } catch { $recoveryVerified = $false; $failures.Add("Windows Update service recovery: $($_.Exception.Message)") }
            }
        }

        $after = Read-Drive
        $reclaimed = $null
        $newPct = $null
        if ($before -and $after) {
            $reclaimed = [Math]::Max([int64]0, ([int64]$after.FreeSpace - [int64]$before.FreeSpace))
            if ([int64]$after.Size -gt 0) { $newPct = [math]::Round(([double]$after.FreeSpace / [double]$after.Size) * 100, 1) }
        }

        $uniqueFailures = @($failures | Select-Object -Unique)
        $outcome = if ($uniqueFailures.Count -eq 0) { 'Completed' } elseif ($completed -gt 0 -or $removedItems -gt 0) { 'PartiallyCompleted' } else { 'Failed' }
        $message = switch ($outcome) {
            'Completed' { 'Disk cleanup completed' }
            'PartiallyCompleted' { 'Disk cleanup partially completed: ' + ($uniqueFailures -join '; ') }
            default { 'Disk cleanup failed: ' + ($uniqueFailures -join '; ') }
        }
        if ($skippedLocked -gt 0) { $message += "; skipped $skippedLocked locked item(s)" }
        if ($skippedProtected -gt 0) { $message += "; preserved $skippedProtected protected item(s)" }
        if ($skippedReparse -gt 0) { $message += "; skipped $skippedReparse reparse point(s)" }
        if ($null -eq $reclaimed) { $message += '; reclaimed space unavailable' }
        [PSCustomObject]@{ outcome = $outcome; reclaimed = $reclaimed; newFreePercent = $newPct; safeToReleaseHost = $recoveryVerified; message = $message }
        """;
    }

    private static string PsLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
