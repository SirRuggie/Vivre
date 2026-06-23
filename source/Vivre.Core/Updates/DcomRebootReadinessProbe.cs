using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;

namespace Vivre.Core.Updates;

/// <inheritdoc cref="IRebootReadinessProbe"/>
/// <remarks>
/// Checks reboot-readiness over a DCOM <see cref="CimSession"/> on the ambient Windows login — no
/// credential prompt, works on the Kerberos-broken Vision boxes. Three signals must all hold before
/// a box is safe to reboot into a staged LCU:
/// <list type="number">
///   <item><description><b>TrustedInstaller stopped</b> — online servicing is complete (or absent).
///   If TI is still running the update is mid-install; rebooting now forces a 2-hour Stopping
///   hang or corrupts the component store.</description></item>
///   <item><description><b>TiWorker.exe not running</b> — the CBS worker thread has finished.
///   TI can report Stopped while TiWorker is still flushing its last writes; waiting for both
///   avoids that race.</description></item>
///   <item><description><b>CBS RebootPending key present</b> — something is actually staged.
///   Without this signal a "clean" box could pass the first two checks and be rebooted for
///   nothing, resetting its uptime and alarming on-call.</description></item>
/// </list>
/// Any DCOM failure (offline, booting, denied) returns not-ready so the wave retries rather
/// than committing a box it cannot read.
/// </remarks>
public sealed class DcomRebootReadinessProbe : IRebootReadinessProbe
{
    private static readonly TimeSpan CimTimeout = TimeSpan.FromSeconds(8);

    /// <summary>HKEY_LOCAL_MACHINE hive constant for StdRegProv calls.</summary>
    private const uint HklmHive = 0x80000002;

    /// <summary>CBS key whose mere existence signals that an update is staged and waiting for a reboot
    /// to commit. We only need to know it's there — EnumKey ReturnValue 0 = present.</summary>
    private const string RebootPendingKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending";

    public Task<RebootReadiness> CheckAsync(string host, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        // CIM calls are synchronous — run off the caller's thread so the wave stays responsive.
        return Task.Run(() => CheckSync(host, cancellationToken), cancellationToken);
    }

    private static RebootReadiness CheckSync(string host, CancellationToken cancellationToken)
    {
        try
        {
            using var options = new DComSessionOptions { Timeout = CimTimeout };
            using CimSession session = CimSession.Create(host, options);
            using var cimOptions = new CimOperationOptions
            {
                Timeout = CimTimeout,
                CancellationToken = cancellationToken,
            };

            // Signal 1: TrustedInstaller service must be Stopped.
            string? tiState = QueryFirstString(
                session, cimOptions,
                "SELECT State FROM Win32_Service WHERE Name='TrustedInstaller'",
                "State");

            if (!string.Equals(tiState, "Stopped", StringComparison.OrdinalIgnoreCase))
            {
                string actual = tiState ?? "unknown";
                return new RebootReadiness(false,
                    $"TrustedInstaller is still {actual} (online servicing in progress)");
            }

            // Signal 2: TiWorker.exe must not be running — it can outlive TI reporting Stopped.
            bool tiWorkerRunning = QueryHasAny(
                session, cimOptions,
                "SELECT ProcessId FROM Win32_Process WHERE Name='TiWorker.exe'");

            if (tiWorkerRunning)
            {
                return new RebootReadiness(false, "TiWorker.exe is still running");
            }

            // Signal 3: CBS RebootPending key must exist — something is actually staged.
            bool rebootPending = EnumKeyExists(session, cimOptions, RebootPendingKey);

            if (!rebootPending)
            {
                return new RebootReadiness(false, "no pending reboot — nothing is staged");
            }

            return new RebootReadiness(true,
                "TrustedInstaller stopped, TiWorker idle, reboot pending — ready to commit.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Offline / still booting / DCOM not up / denied — not a verdict; the wave retries.
            return new RebootReadiness(false, $"couldn't reach {host} to check reboot-readiness");
        }
    }

    /// <summary>Returns the string value of <paramref name="property"/> from the first instance
    /// returned by <paramref name="wql"/>, or <see langword="null"/> when the query yields no
    /// rows or the property is absent.</summary>
    private static string? QueryFirstString(
        CimSession session, CimOperationOptions cimOptions, string wql, string property)
    {
        foreach (CimInstance instance in session.QueryInstances(@"root\cimv2", "WQL", wql, cimOptions))
        {
            using (instance)
            {
                return instance.CimInstanceProperties[property]?.Value?.ToString();
            }
        }

        return null;
    }

    /// <summary>Returns <see langword="true"/> when <paramref name="wql"/> yields at least one
    /// instance — used to detect a running process without reading any property values.</summary>
    private static bool QueryHasAny(CimSession session, CimOperationOptions cimOptions, string wql)
    {
        foreach (CimInstance instance in session.QueryInstances(@"root\cimv2", "WQL", wql, cimOptions))
        {
            using (instance)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Probes whether <paramref name="subKey"/> exists under HKLM by calling
    /// <c>StdRegProv.EnumKey</c>. A <c>ReturnValue</c> of 0 means the key is present (even if it
    /// has no subkeys of its own). This is how the CBS reboot-pending signal is detected — the key
    /// itself is the signal, its contents are irrelevant.</summary>
    private static bool EnumKeyExists(CimSession session, CimOperationOptions cimOptions, string subKey)
    {
        using var inParams = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("hDefKey", HklmHive, CimType.UInt32, CimFlags.In),
            CimMethodParameter.Create("sSubKeyName", subKey, CimType.String, CimFlags.In),
        };

        using CimMethodResult result = session.InvokeMethod(
            @"root\cimv2", "StdRegProv", "EnumKey", inParams, cimOptions);

        object? rv = result.ReturnValue?.Value;
        return rv is not null && Convert.ToUInt32(rv) == 0;
    }
}
