using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;

namespace Vivre.Core.Updates;

/// <inheritdoc cref="IBootTimeReader"/>
/// <remarks>
/// Reads the uptime over a DCOM <see cref="CimSession"/> on the ambient Windows login (mirrors
/// <see cref="DcomLcuBuildReader"/> and <c>DcomVitalsProbe</c>), so it works on the Vision boxes where
/// WinRM/Kerberos is rejected. ONE query pulls both <c>LocalDateTime</c> and <c>LastBootUpTime</c> from the
/// target's OWN clock, so their difference is clock-step-immune. Any failure (offline, still booting, DCOM
/// not up, denied, either value missing) returns null so the caller retries rather than treating it as proof.
/// </remarks>
public sealed class DcomBootTimeReader : IBootTimeReader
{
    private static readonly TimeSpan CimTimeout = TimeSpan.FromSeconds(8);

    public Task<BootTimeReading?> ReadAsync(string host, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        // CIM calls are synchronous — run off the caller's thread so the wave stays responsive.
        return Task.Run(() => ReadSync(host, cancellationToken), cancellationToken);
    }

    private static BootTimeReading? ReadSync(string host, CancellationToken cancellationToken)
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

            foreach (CimInstance instance in session.QueryInstances(
                @"root\cimv2", "WQL",
                "SELECT LocalDateTime, LastBootUpTime FROM Win32_OperatingSystem",
                cimOptions))
            {
                using (instance)
                {
                    // MI maps CIM datetimes to System.DateTime; BOTH must be present or we can't derive the
                    // uptime — a missing value is not a verdict, so return null and let the caller retry.
                    var localNow = instance.CimInstanceProperties["LocalDateTime"]?.Value as DateTime?;
                    var lastBoot = instance.CimInstanceProperties["LastBootUpTime"]?.Value as DateTime?;
                    if (localNow is null || lastBoot is null)
                    {
                        return null;
                    }

                    return new BootTimeReading(localNow.Value, lastBoot.Value);
                }
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Offline / still booting / DCOM not up / denied — not a verdict; the caller retries.
            return null;
        }
    }
}
