using System.Globalization;
using System.Management.Automation;
using Vivre.Core.PowerShell;

namespace Vivre.Core.Remediation;

/// <inheritdoc cref="IRemediationService"/>
/// <remarks>
/// Mirrors <see cref="Vitals.VitalsProbe"/>: one short embedded script per call, run through
/// <see cref="IPowerShellHost"/> (local vs WinRM chosen by <see cref="HostName.IsLocal"/>) under the
/// supplied admin credential. Scripts emit a single <c>[PSCustomObject]</c> (or, for processes, a row
/// per item) that the parse helpers read into typed results. Values that vary (service name, PID) are
/// injected via token replacement — names are single-quote-escaped to block script injection.
/// </remarks>
public sealed class RemediationService : IRemediationService
{
    private readonly IPowerShellHost _powerShell;

    public RemediationService(IPowerShellHost powerShell) => _powerShell = powerShell;

    public async Task<RemediationResult> StartServiceAsync(
        string host, string serviceDisplayName, PSCredential? credential = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceDisplayName);

        // Single-quote-escape the display name and match exactly (no wildcard semantics).
        string script = StartServiceScript.Replace("__DN__", serviceDisplayName.Replace("'", "''"), StringComparison.Ordinal);
        PSExecutionResult result = await RunAsync(host, script, credential, cancellationToken).ConfigureAwait(false);
        return ParseResult(result, $"Couldn't start '{serviceDisplayName}'");
    }

    public async Task<DiskCleanupResult> FreeDiskSpaceAsync(
        string host, PSCredential? credential = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        PSExecutionResult result = await RunAsync(host, FreeDiskScript, credential, cancellationToken).ConfigureAwait(false);
        PSObject? row = result.Output.Count > 0 ? result.Output[0] : null;
        if (row is null)
        {
            string detail = result.Errors.Count > 0 ? result.Errors[0] : "no result returned";
            return new DiskCleanupResult(false, 0, null, $"Disk cleanup failed: {detail}");
        }

        long reclaimed = GetLong(row, "reclaimed") ?? 0;
        double? newPct = GetDouble(row, "newFreePercent");
        string message = GetString(row, "message") ?? "Disk cleanup complete";
        return new DiskCleanupResult(GetBool(row, "ok"), reclaimed, newPct, message);
    }

    public async Task<IReadOnlyList<ProcessInfo>> GetTopProcessesAsync(
        string host, PSCredential? credential = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        PSExecutionResult result = await RunAsync(host, TopProcessesScript, credential, cancellationToken).ConfigureAwait(false);
        var list = new List<ProcessInfo>();
        foreach (PSObject row in result.Output)
        {
            string? name = GetString(row, "name");
            int? id = GetInt(row, "id");
            if (name is null || id is null)
            {
                continue;
            }

            list.Add(new ProcessInfo(name, id.Value, GetDouble(row, "wsMb") ?? 0, GetDouble(row, "cpu")));
        }

        return list;
    }

    public async Task<RemediationResult> EndProcessAsync(
        string host, int processId, PSCredential? credential = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        string script = EndProcessScript.Replace("__PID__", processId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        PSExecutionResult result = await RunAsync(host, script, credential, cancellationToken).ConfigureAwait(false);
        return ParseResult(result, $"Couldn't end process {processId}");
    }

    private Task<PSExecutionResult> RunAsync(string host, string script, PSCredential? credential, CancellationToken token) =>
        HostName.IsLocal(host)
            ? _powerShell.RunLocalAsync(script, token)
            : _powerShell.RunRemoteAsync(host, script, credential, cancellationToken: token);

    /// <summary>Reads the single <c>{ ok; message }</c> result row, falling back to stderr on no output.</summary>
    private static RemediationResult ParseResult(PSExecutionResult result, string fallbackPrefix)
    {
        PSObject? row = result.Output.Count > 0 ? result.Output[0] : null;
        if (row is null)
        {
            string detail = result.Errors.Count > 0 ? result.Errors[0] : "no result returned";
            return new RemediationResult(false, $"{fallbackPrefix}: {detail}");
        }

        return new RemediationResult(GetBool(row, "ok"), GetString(row, "message") ?? fallbackPrefix);
    }

    // --- PSObject scalar readers (same shape as VitalsProbe's) ---

    private static object? Value(PSObject row, string name) => row.Properties[name]?.Value;

    private static bool GetBool(PSObject row, string name) => Value(row, name) is bool b && b;

    private static string? GetString(PSObject row, string name)
    {
        object? value = Value(row, name);
        return string.IsNullOrWhiteSpace(value?.ToString()) ? null : value!.ToString();
    }

    private static int? GetInt(PSObject row, string name) => Value(row, name) switch
    {
        null => null,
        int i => i,
        IConvertible c => Try(() => c.ToInt32(CultureInfo.InvariantCulture)),
        _ => null,
    };

    private static long? GetLong(PSObject row, string name) => Value(row, name) switch
    {
        null => null,
        long l => l,
        IConvertible c => TryL(() => c.ToInt64(CultureInfo.InvariantCulture)),
        _ => null,
    };

    private static double? GetDouble(PSObject row, string name) => Value(row, name) switch
    {
        null => null,
        double d => d,
        IConvertible c => TryD(() => c.ToDouble(CultureInfo.InvariantCulture)),
        _ => null,
    };

    private static int? Try(Func<int> f) { try { return f(); } catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException) { return null; } }

    private static long? TryL(Func<long> f) { try { return f(); } catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException) { return null; } }

    private static double? TryD(Func<double> f) { try { return f(); } catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException) { return null; } }

    // --- Embedded scripts (run via IPowerShellHost; __DN__/__PID__ replaced before sending) ---

    private const string StartServiceScript = """
        $ErrorActionPreference = 'Stop'
        $dn = '__DN__'
        try {
            $svc = Get-Service -ErrorAction Stop | Where-Object { $_.DisplayName -eq $dn } | Select-Object -First 1
            if (-not $svc) {
                [PSCustomObject]@{ ok = $false; message = "Service not found: $dn" }
            } else {
                if ($svc.Status -ne 'Running') {
                    Start-Service -InputObject $svc -ErrorAction Stop
                    $svc.Refresh()
                    [PSCustomObject]@{ ok = $true; message = "Started '$($svc.DisplayName)' (now $($svc.Status))" }
                } else {
                    [PSCustomObject]@{ ok = $true; message = "'$($svc.DisplayName)' is already running" }
                }
            }
        } catch {
            [PSCustomObject]@{ ok = $false; message = $_.Exception.Message }
        }
        """;

    private const string FreeDiskScript = """
        $ErrorActionPreference = 'SilentlyContinue'
        $sys = $env:SystemDrive
        function DriveProp($p) { (Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$sys'")."$p" }
        $before = [int64](DriveProp 'FreeSpace')

        Get-ChildItem -Path $env:TEMP, "$env:SystemRoot\Temp" -Force -ErrorAction SilentlyContinue |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        try { Clear-RecycleBin -Force -ErrorAction SilentlyContinue } catch { }
        try {
            Stop-Service wuauserv -Force -ErrorAction SilentlyContinue
            Remove-Item "$env:SystemRoot\SoftwareDistribution\Download\*" -Recurse -Force -ErrorAction SilentlyContinue
            Start-Service wuauserv -ErrorAction SilentlyContinue
        } catch { }

        $after = [int64](DriveProp 'FreeSpace')
        $size  = [int64](DriveProp 'Size')
        $reclaimed = $after - $before
        if ($reclaimed -lt 0) { $reclaimed = 0 }
        $newPct = if ($size -gt 0) { [math]::Round(($after / $size) * 100, 1) } else { $null }
        [PSCustomObject]@{ ok = $true; reclaimed = [int64]$reclaimed; newFreePercent = $newPct; message = "Cleared TEMP, Windows Update cache, and recycle bin" }
        """;

    private const string TopProcessesScript = """
        $ErrorActionPreference = 'SilentlyContinue'
        Get-Process -ErrorAction SilentlyContinue |
            Sort-Object -Property WorkingSet64 -Descending |
            Select-Object -First 15 |
            ForEach-Object {
                [PSCustomObject]@{
                    name = $_.ProcessName
                    id   = [int]$_.Id
                    wsMb = [math]::Round($_.WorkingSet64 / 1MB, 1)
                    cpu  = if ($null -ne $_.CPU) { [math]::Round([double]$_.CPU, 1) } else { $null }
                }
            }
        """;

    private const string EndProcessScript = """
        $ErrorActionPreference = 'Stop'
        try {
            $p = Get-Process -Id __PID__ -ErrorAction Stop
            $n = $p.ProcessName
            Stop-Process -Id __PID__ -Force -ErrorAction Stop
            [PSCustomObject]@{ ok = $true; message = "Ended $n (PID __PID__)" }
        } catch {
            [PSCustomObject]@{ ok = $false; message = $_.Exception.Message }
        }
        """;
}
