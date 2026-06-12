using System.ComponentModel;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;

namespace Vivre.Core.Updates;

/// <inheritdoc cref="IRebootTrigger"/>
/// <remarks>
/// Reboots over DCOM via <c>Win32_OperatingSystem.Win32Shutdown</c> on the ambient Windows login — the
/// same channel vitals use, so it works on the Kerberos-broken Vision boxes without a credential prompt.
/// Flags: 2 = reboot (graceful — services get their normal stop sequence so SQL flushes), 6 = reboot +
/// force (2 | 4) for the escalation when a graceful reboot won't take.
/// </remarks>
public sealed class DcomRebootTrigger : IRebootTrigger
{
    private static readonly TimeSpan CimTimeout = TimeSpan.FromSeconds(20);

    private const int EwxReboot = 2;
    private const int EwxForce = 4;

    public Task RebootAsync(string host, bool forced, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return Task.Run(() => RebootSync(host, forced, cancellationToken), cancellationToken);
    }

    private static void RebootSync(string host, bool forced, CancellationToken cancellationToken)
    {
        int flags = forced ? EwxReboot | EwxForce : EwxReboot;

        using var options = new DComSessionOptions { Timeout = CimTimeout };
        using CimSession session = CimSession.Create(host, options);
        using var cimOptions = new CimOperationOptions
        {
            Timeout = CimTimeout,
            CancellationToken = cancellationToken,
        };

        foreach (CimInstance os in session.QueryInstances(
                     @"root\cimv2", "WQL", "SELECT __PATH FROM Win32_OperatingSystem", cimOptions))
        {
            using (os)
            {
                var inParams = new CimMethodParametersCollection
                {
                    CimMethodParameter.Create("Flags", flags, CimType.SInt32, CimFlags.In),
                };

                using CimMethodResult result = session.InvokeMethod(@"root\cimv2", os, "Win32Shutdown", inParams, cimOptions);
                object? rv = result.ReturnValue?.Value;
                uint code = rv is null ? 0 : Convert.ToUInt32(rv);
                if (code != 0)
                {
                    // Non-zero = the OS refused the shutdown (privilege, or a blocking app on a graceful
                    // reboot). Surface it so the wave can escalate / flag the box rather than hang waiting.
                    throw new Win32Exception((int)code,
                        $"Win32Shutdown on {host} returned {code} ({(forced ? "forced" : "graceful")} reboot).");
                }
            }

            return; // single OS instance
        }

        throw new InvalidOperationException($"Couldn't reach Win32_OperatingSystem on {host} to issue the reboot.");
    }
}
