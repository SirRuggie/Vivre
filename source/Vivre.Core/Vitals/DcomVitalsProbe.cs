using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;

namespace Vivre.Core.Vitals;

/// <inheritdoc cref="IDcomVitalsReader"/>
/// <remarks>
/// Mirrors the session-creation pattern of <c>WmiHostProbe</c> (DComSessionOptions, hostname, NO
/// explicit credential) so it always runs on the operator's current Windows login — the same read-only
/// management plane that SCCM and PowerShell use. The CimSession is disposed after each call.
///
/// Each CIM/InvokeMethod call sits in its own try/catch. A denied or absent source leaves the
/// corresponding field null rather than failing the whole pull, matching the per-probe try/catch
/// contract in <c>VitalsProbe.VitalsScript</c>.
/// </remarks>
public sealed class DcomVitalsProbe : IDcomVitalsReader
{
    // CIM call timeout — same as WmiHostProbe.ProbeTimeout, long enough for a responsive server
    // but short enough that a hung WMI provider doesn't stall the sweep indefinitely.
    private static readonly TimeSpan CimTimeout = TimeSpan.FromSeconds(8);

    // HKEY_LOCAL_MACHINE hive constant used by StdRegProv key-existence checks.
    private const uint HklmHive = 0x80000002;

    public Task<MachineVitals> ReadAsync(string host, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        // CIM calls are synchronous — run off the caller's thread so the sweep stays async.
        return Task.Run(() => ReadSync(host, cancellationToken), cancellationToken);
    }

    private static MachineVitals ReadSync(string host, CancellationToken cancellationToken)
    {
        // No explicit credential: ambient Windows identity, exactly like WmiHostProbe.
        using var options = new DComSessionOptions { Timeout = CimTimeout };
        using CimSession session = CimSession.Create(host, options);
        using var cimOptions = new CimOperationOptions
        {
            Timeout = CimTimeout,
            CancellationToken = cancellationToken,
        };

        // --- Fixed disks (DriveType 3) ---
        double? sysFreePct = null;
        double? sysFreeGb = null;
        var drives = new List<DriveVitals>();
        string? sysDrive = null;

        // Win32_OperatingSystem.SystemDrive is read below; pre-populate from the OS query so the
        // drive loop can match the system drive letter.
        double? memUsedPct = null;
        DateTime? lastBoot = null;
        string? operatingSystem = null;

        try
        {
            foreach (CimInstance os in session.QueryInstances(@"root\cimv2", "WQL",
                         "SELECT Caption, Version, SystemDrive, TotalVisibleMemorySize, FreePhysicalMemory, LastBootUpTime FROM Win32_OperatingSystem",
                         cimOptions))
            {
                using (os)
                {
                    sysDrive = os.CimInstanceProperties["SystemDrive"]?.Value as string;

                    ulong total = ToULong(os.CimInstanceProperties["TotalVisibleMemorySize"]?.Value);
                    ulong free = ToULong(os.CimInstanceProperties["FreePhysicalMemory"]?.Value);
                    if (total > 0)
                    {
                        memUsedPct = Math.Round((1.0 - (double)free / total) * 100, 0);
                    }

                    lastBoot = os.CimInstanceProperties["LastBootUpTime"]?.Value as DateTime?;

                    string? caption = os.CimInstanceProperties["Caption"]?.Value as string;
                    string? version = os.CimInstanceProperties["Version"]?.Value as string;
                    if (!string.IsNullOrWhiteSpace(caption))
                    {
                        string trimmed = caption.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase)
                            ? caption["Microsoft ".Length..].TrimStart()
                            : caption.Trim();
                        operatingSystem = !string.IsNullOrWhiteSpace(version)
                            ? $"{trimmed} — {version}"
                            : trimmed;
                    }
                }

                break; // only one instance
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // denied / WMI unavailable — leave all OS fields null
        }

        try
        {
            foreach (CimInstance disk in session.QueryInstances(@"root\cimv2", "WQL",
                         "SELECT DeviceID, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType = 3",
                         cimOptions))
            {
                using (disk)
                {
                    string? letter = disk.CimInstanceProperties["DeviceID"]?.Value as string;
                    if (letter is null) { continue; }

                    ulong size = ToULong(disk.CimInstanceProperties["Size"]?.Value);
                    ulong freeBytes = ToULong(disk.CimInstanceProperties["FreeSpace"]?.Value);
                    if (size == 0) { continue; }

                    double freePct = Math.Round((double)freeBytes / size * 100.0, 1);
                    double freeGb = Math.Round((double)freeBytes / (1024.0 * 1024 * 1024), 1);
                    double sizeGb = Math.Round((double)size / (1024.0 * 1024 * 1024), 1);

                    drives.Add(new DriveVitals(letter, freePct, freeGb, sizeGb));

                    if (!string.IsNullOrWhiteSpace(sysDrive) &&
                        string.Equals(letter, sysDrive, StringComparison.OrdinalIgnoreCase))
                    {
                        sysFreePct = freePct;
                        sysFreeGb = freeGb;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // denied / absent — leave drives empty and sys-drive fields null
        }

        // --- CPU load (coarse instantaneous snapshot; averaged across sockets) ---
        double? cpuLoad = null;
        try
        {
            var loads = new List<double>();
            foreach (CimInstance proc in session.QueryInstances(@"root\cimv2", "WQL",
                         "SELECT LoadPercentage FROM Win32_Processor",
                         cimOptions))
            {
                using (proc)
                {
                    object? val = proc.CimInstanceProperties["LoadPercentage"]?.Value;
                    if (val is not null)
                    {
                        loads.Add(Convert.ToDouble(val));
                    }
                }
            }

            if (loads.Count > 0)
            {
                cpuLoad = Math.Round(loads.Average(), 0);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // denied / absent
        }

        // --- Pending reboot ---
        // Mirrors the script's three sources: SCCM CCM_ClientUtilities, CBS registry key, Windows
        // Update registry key. Each source is in its own try so a denied/absent one leaves its
        // contribution false rather than aborting the whole check.
        bool? rebootPending = null;

        bool? ccmReboot = null;
        try
        {
            // InvokeMethod on a static CIM method (same as the script's Invoke-CimMethod).
            using var ccmResult = session.InvokeMethod(
                @"root\ccm\ClientSDK",
                "CCM_ClientUtilities",
                "DetermineIfRebootPending",
                null,
                cimOptions);

            object? rp = ccmResult.OutParameters?["RebootPending"]?.Value;
            object? hrp = ccmResult.OutParameters?["IsHardRebootPending"]?.Value;
            ccmReboot = (rp is true) || (hrp is true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // CCM namespace absent or access denied — leave this source unknown (null)
        }

        bool? cbsReboot = null;
        try
        {
            // StdRegProv EnumKey: ReturnValue 0 means the key exists.
            var inParams = new CimMethodParametersCollection();
            inParams.Add(CimMethodParameter.Create("hDefKey", HklmHive, CimType.UInt32, CimFlags.In));
            inParams.Add(CimMethodParameter.Create("sSubKeyName",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",
                CimType.String, CimFlags.In));

            using var cbsResult = session.InvokeMethod(@"root\cimv2", "StdRegProv", "EnumKey", inParams, cimOptions);
            object? rv = cbsResult.ReturnValue?.Value;
            cbsReboot = rv is not null && Convert.ToUInt32(rv) == 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // access denied — leave this source unknown (null)
        }

        bool? wuReboot = null;
        try
        {
            var inParams = new CimMethodParametersCollection();
            inParams.Add(CimMethodParameter.Create("hDefKey", HklmHive, CimType.UInt32, CimFlags.In));
            inParams.Add(CimMethodParameter.Create("sSubKeyName",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
                CimType.String, CimFlags.In));

            using var wuResult = session.InvokeMethod(@"root\cimv2", "StdRegProv", "EnumKey", inParams, cimOptions);
            object? rv = wuResult.ReturnValue?.Value;
            wuReboot = rv is not null && Convert.ToUInt32(rv) == 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // access denied — leave this source unknown (null)
        }

        // null = no source could be read (unknown — matches the script leaving it null on total
        // failure); otherwise the OR of the sources that actually answered.
        rebootPending = (ccmReboot is null && cbsReboot is null && wuReboot is null)
            ? null
            : ((ccmReboot ?? false) || (cbsReboot ?? false) || (wuReboot ?? false));

        return new MachineVitals(
            SystemDriveFreePercent: sysFreePct,
            SystemDriveFreeGb: sysFreeGb,
            MemoryUsedPercent: memUsedPct,
            CpuLoadPercent: cpuLoad,
            LastBootTime: lastBoot,
            RebootPending: rebootPending)
        {
            OperatingSystem = operatingSystem,
            Drives = drives,
        };
    }

    private static ulong ToULong(object? value) =>
        value is null ? 0UL : Convert.ToUInt64(value);
}
