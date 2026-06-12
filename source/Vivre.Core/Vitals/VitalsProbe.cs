using System.Collections;
using System.Globalization;
using System.Management.Automation;
using Vivre.Core.PowerShell;

namespace Vivre.Core.Vitals;

/// <inheritdoc cref="IVitalsProbe"/>
/// <remarks>
/// Structure mirrors <see cref="Sccm.ConfigMgrClient"/>: one <see cref="VitalsScript"/> run through
/// <see cref="IPowerShellHost"/> (local vs remote chosen by <see cref="HostName.IsLocal"/>), emitting a
/// single <c>[PSCustomObject]</c> that <see cref="Parse"/> reads into <see cref="MachineVitals"/>.
/// PS7 has no <c>Get-WmiObject</c>, so every probe uses <c>Get-CimInstance</c> and
/// sits in its own <c>try/catch</c> — a denied or absent source degrades to a null field rather than
/// failing the whole pull.
///
/// When a <see cref="IDcomVitalsReader"/> is supplied and WinRM is rejected with Kerberos error
/// 0x80090322, the probe automatically falls back to DCOM to retrieve real vitals, while keeping
/// <see cref="WinRmHealth.KerberosRejected"/> so the row stays visibly flagged for the admin's attention.
/// If the DCOM reader also fails the original blank-flagged snapshot is returned unchanged.
/// </remarks>
public sealed class VitalsProbe : IVitalsProbe
{
    private readonly IPowerShellHost _powerShell;
    private readonly IDcomVitalsReader? _dcomReader;

    public VitalsProbe(IPowerShellHost powerShell, IDcomVitalsReader? dcomReader = null)
    {
        _powerShell = powerShell;
        _dcomReader = dcomReader;
    }

    public async Task<MachineVitals> GetVitalsAsync(
        string host,
        PSCredential? credential = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        PSExecutionResult result;
        try
        {
            result = HostName.IsLocal(host)
                ? await _powerShell.RunLocalAsync(VitalsScript, cancellationToken).ConfigureAwait(false)
                : await _powerShell.RunRemoteAsync(host, VitalsScript, credential, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (KerberosWrongPrincipalException ex)
        {
            // The host rejected Kerberos: WinRM can't read it on the current login, and the routing host
            // has flipped it to the SMB/DCOM transport. If a DCOM reader is wired in, try to pull REAL
            // vitals over DCOM so the row shows real numbers — the KerberosRejected flag + the actual WinRM
            // error are kept either way so the admin's attention channel is never silenced. If DCOM also
            // fails, fall back to the blank flagged snapshot. Health channel only — no "fell back" wording
            // on any operation result.
            string detail = ex.Message;
            if (_dcomReader is not null)
            {
                try
                {
                    MachineVitals dcom = await _dcomReader.ReadAsync(host, cancellationToken).ConfigureAwait(false);
                    return dcom with { WinRmHealth = WinRmHealth.KerberosRejected, WinRmFailureDetail = detail };
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // DCOM also failed — surface it as the flagged "Unknown" snapshot below (the
                    // operator-visible signal that this box needs attention); no transport detail leaks.
                }
            }

            return new MachineVitals { WinRmHealth = WinRmHealth.KerberosRejected, WinRmFailureDetail = detail };
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !HostName.IsLocal(host) && _dcomReader is not null)
        {
            // WinRM failed for a NON-Kerberos reason (the WinRM service is stopped/misconfigured —
            // "the client cannot connect to the destination" — or the session dropped mid-read —
            // "the remote session ended"). DCOM may still read this host on the current login, so try it
            // before giving up, and flag the row WinRmUnavailable so a DCOM-rescued box never looks
            // identical to a healthy WinRM read. We do NOT cache this as a transport decision (it's often
            // transient) and it's reached only for remote hosts. If DCOM also fails, rethrow the ORIGINAL
            // WinRM error so the caller surfaces "Vitals failed — <reason>" unchanged.
            MachineVitals? rescued = null;
            try
            {
                rescued = await _dcomReader.ReadAsync(host, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // DCOM didn't rescue it — fall through to rethrow the original WinRM failure (ex).
            }

            if (rescued is not null)
            {
                return rescued with { WinRmHealth = WinRmHealth.WinRmUnavailable, WinRmFailureDetail = ex.Message };
            }

            throw; // rethrow the original WinRM error so it surfaces as today
        }

        // A successful WinRM read means the fast Kerberos path is healthy for this host.
        return Parse(result) with { WinRmHealth = WinRmHealth.Healthy };
    }

    private static MachineVitals Parse(PSExecutionResult result)
    {
        if (result.Output.Count == 0)
        {
            // Nothing came back — the box was unreachable over WinRM or every probe failed.
            string detail = result.Errors.Count > 0 ? result.Errors[0] : "no data returned";
            throw new VitalsProbeException($"Could not read vitals from target: {detail}");
        }

        PSObject row = result.Output[0];

        return new MachineVitals(
            SystemDriveFreePercent: GetDouble(row, "SystemDriveFreePercent"),
            SystemDriveFreeGb: GetDouble(row, "SystemDriveFreeGb"),
            MemoryUsedPercent: GetDouble(row, "MemoryUsedPercent"),
            CpuLoadPercent: GetDouble(row, "CpuLoadPercent"),
            LastBootTime: GetDateTime(row, "LastBootTime"),
            StoppedAutoServiceCount: GetInt(row, "StoppedAutoServiceCount"),
            RebootPending: GetNullableBool(row, "RebootPending"),
            UserLoggedOn: GetNullableBool(row, "UserLoggedOn"))
        {
            OperatingSystem = GetString(row, "OperatingSystem"),
            Drives = ParseDrives(row),
            StoppedAutoServices = GetStringList(row, "StoppedAutoServices"),
            LoggedOnUsers = GetStringList(row, "LoggedOnUsers"),
        };
    }

    private static IReadOnlyList<DriveVitals> ParseDrives(PSObject row)
    {
        var drives = new List<DriveVitals>();
        foreach (PSObject item in EnumerateObjects(row, "Drives"))
        {
            string? letter = GetString(item, "Letter");
            if (letter is null)
            {
                continue;
            }

            drives.Add(new DriveVitals(
                letter,
                GetDouble(item, "FreePercent") ?? 0,
                GetDouble(item, "FreeGb") ?? 0,
                GetDouble(item, "SizeGb") ?? 0));
        }

        return drives;
    }

    // --- PSObject reading helpers (extend ConfigMgrClient's set with double/int/list/object). ---

    private static DateTime? GetDateTime(PSObject row, string name) =>
        Value(row, name) is DateTime dt ? dt : null;

    private static string? GetString(PSObject row, string name)
    {
        object? value = Value(row, name);
        return string.IsNullOrWhiteSpace(value?.ToString()) ? null : value!.ToString();
    }

    private static bool? GetNullableBool(PSObject row, string name) =>
        Value(row, name) is bool b ? b : null;

    private static double? GetDouble(PSObject row, string name)
    {
        object? value = Value(row, name);
        return value switch
        {
            null => null,
            double d => d,
            IConvertible c => SafeToDouble(c),
            _ => null,
        };
    }

    private static int? GetInt(PSObject row, string name)
    {
        object? value = Value(row, name);
        return value switch
        {
            null => null,
            int i => i,
            IConvertible c => SafeToInt(c),
            _ => null,
        };
    }

    /// <summary>Reads a property that may be a single value or a (possibly single-element) array of
    /// strings, returning a flat string list. Tolerates the way PowerShell collapses one-item arrays.</summary>
    private static IReadOnlyList<string> GetStringList(PSObject row, string name)
    {
        object? value = Value(row, name);

        // Over PowerShell remoting, an array property comes back wrapped in a PSObject whose
        // BaseObject is the real collection — unwrap it, or the IEnumerable check below misses it
        // (locally the value is already the bare array). This is what makes the names list populate
        // for remote hosts, not just the count.
        if (value is PSObject wrapper)
        {
            value = wrapper.BaseObject;
        }

        if (value is null)
        {
            return [];
        }

        var list = new List<string>();
        if (value is string s)
        {
            if (!string.IsNullOrWhiteSpace(s))
            {
                list.Add(s);
            }
        }
        else if (value is IEnumerable seq)
        {
            foreach (object? element in seq)
            {
                string? text = (element is PSObject pso ? pso.BaseObject : element)?.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    list.Add(text);
                }
            }
        }

        return list;
    }

    /// <summary>Enumerates a property as a sequence of <see cref="PSObject"/>s (each array element
    /// wrapped), tolerating a single non-array value or a collapsed one-element array.</summary>
    private static IEnumerable<PSObject> EnumerateObjects(PSObject row, string name)
    {
        object? value = Value(row, name);

        // Remoting wraps an array property in a PSObject whose BaseObject is the real collection.
        // Unwrap it so the IEnumerable case below iterates the elements instead of treating the whole
        // wrapper as one item (which is why remote Drives/Events came back empty while counts didn't).
        if (value is PSObject wrapper && wrapper.BaseObject is IEnumerable and not string)
        {
            value = wrapper.BaseObject;
        }

        switch (value)
        {
            case null:
                yield break;
            case PSObject single when single.BaseObject is not IEnumerable or string:
                yield return single;
                break;
            case IEnumerable seq when value is not string:
                foreach (object? element in seq)
                {
                    if (element is not null)
                    {
                        yield return element as PSObject ?? new PSObject(element);
                    }
                }

                break;
            default:
                yield return new PSObject(value);
                break;
        }
    }

    private static object? Value(PSObject row, string name) => row.Properties[name]?.Value;

    private static double? SafeToDouble(IConvertible c)
    {
        try
        {
            return c.ToDouble(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static int? SafeToInt(IConvertible c)
    {
        try
        {
            return c.ToInt32(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    // One round-trip per host: every probe is in its own try/catch so a denied/absent source leaves a
    // null field instead of failing the pull. PS7 throughout (Get-CimInstance, not Get-WmiObject).
    private const string VitalsScript = """
        $ErrorActionPreference = 'SilentlyContinue'

        # --- Fixed disks (DriveType 3) ---
        $drives = @()
        $sysFreePct = $null; $sysFreeGb = $null
        try {
            $sysLetter = $env:SystemDrive
            foreach ($d in @(Get-CimInstance -ClassName Win32_LogicalDisk -Filter 'DriveType=3' -ErrorAction Stop)) {
                if (-not $d.Size -or $d.Size -le 0) { continue }
                $freePct = [math]::Round(($d.FreeSpace / $d.Size) * 100, 1)
                $freeGb  = [math]::Round($d.FreeSpace / 1GB, 1)
                $sizeGb  = [math]::Round($d.Size / 1GB, 1)
                $drives += [PSCustomObject]@{ Letter = $d.DeviceID; FreePercent = $freePct; FreeGb = $freeGb; SizeGb = $sizeGb }
                if ($d.DeviceID -eq $sysLetter) { $sysFreePct = $freePct; $sysFreeGb = $freeGb }
            }
        } catch { }

        # --- Memory + uptime + OS identity (free with this CIM pull, so the grid/Details has it
        #     without a separate lazy WinRM query later) ---
        $memUsedPct = $null; $lastBoot = $null; $operatingSystem = $null
        try {
            $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
            if ($os.TotalVisibleMemorySize -gt 0) {
                $memUsedPct = [math]::Round((1 - ($os.FreePhysicalMemory / $os.TotalVisibleMemorySize)) * 100, 0)
            }
            $lastBoot = $os.LastBootUpTime
            if ($os.Caption) { $operatingSystem = "$(($os.Caption -replace '^Microsoft ','').Trim()) — $($os.Version)" }
        } catch { }

        # --- CPU load (coarse instantaneous snapshot; averaged across sockets) ---
        $cpuLoad = $null
        try {
            $loads = @(Get-CimInstance -ClassName Win32_Processor -ErrorAction Stop | Select-Object -ExpandProperty LoadPercentage)
            $loads = @($loads | Where-Object { $_ -ne $null })
            if ($loads.Count -gt 0) { $cpuLoad = [math]::Round(($loads | Measure-Object -Average).Average, 0) }
        } catch { }

        # --- Stopped auto-start services ---
        $stoppedCount = $null; $stoppedNames = @()
        try {
            $stopped = @(Get-CimInstance -ClassName Win32_Service -Filter "StartMode='Auto' AND State<>'Running'" -ErrorAction Stop)
            $stoppedCount = $stopped.Count
            $stoppedNames = @($stopped | Select-Object -First 15 -ExpandProperty DisplayName)
        } catch { }

        # --- Pending reboot (CBS key / Windows Update / SCCM client) ---
        # PendingFileRenameOperations is deliberately NOT used: benign file ops (AV definition swaps,
        # installer temp cleanup) populate it and it accumulates on long-uptime servers, so it massively
        # over-reports "reboot pending". Match SCCM's own DetermineIfRebootPending plus the CBS and Windows
        # Update reboot keys instead, so the column agrees with the ConfigMgr console.
        $rebootPending = $null
        try {
            $cbs = Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending'
            $wu  = Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired'
            $ccm = $false
            try {
                $r = Invoke-CimMethod -Namespace 'ROOT\ccm\ClientSDK' -ClassName CCM_ClientUtilities -MethodName DetermineIfRebootPending -ErrorAction Stop
                $ccm = [bool]$r.RebootPending -or [bool]$r.IsHardRebootPending
            } catch { }
            $rebootPending = [bool]($cbs -or $wu -or $ccm)
        } catch { }

        # --- Logged-on interactive users ---
        $userLoggedOn = $null; $users = @()
        try {
            $explorers = @(Get-CimInstance -ClassName Win32_Process -Filter "Name='explorer.exe'" -ErrorAction Stop)
            $userLoggedOn = $explorers.Count -gt 0
            $users = @($explorers | ForEach-Object {
                $o = Invoke-CimMethod -InputObject $_ -MethodName GetOwner -ErrorAction SilentlyContinue
                if ($o -and $o.User) { $o.User }
            } | Sort-Object -Unique)
        } catch { }

        [PSCustomObject]@{
            OperatingSystem         = $operatingSystem
            SystemDriveFreePercent  = $sysFreePct
            SystemDriveFreeGb       = $sysFreeGb
            Drives                  = $drives
            MemoryUsedPercent       = $memUsedPct
            CpuLoadPercent          = $cpuLoad
            LastBootTime            = $lastBoot
            StoppedAutoServiceCount = $stoppedCount
            StoppedAutoServices     = $stoppedNames
            RebootPending           = $rebootPending
            UserLoggedOn            = $userLoggedOn
            LoggedOnUsers           = $users
        }
        """;
}
