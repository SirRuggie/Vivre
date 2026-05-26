using Vivre.Core.Credentials;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;

namespace Vivre.Core.Remoting;

/// <inheritdoc cref="IHostProbe"/>
public sealed class WmiHostProbe : IHostProbe
{
    // A down host shouldn't stall the sweep — cap each probe.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    public Task<ProbeResult> CanReachAsync(string host, ConnectionCredential? credential = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return Task.FromResult(ProbeResult.Unreachable("No host name."));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // The CIM calls are synchronous — run off the caller's thread so the sweep stays async.
        return Task.Run(() => Probe(host, credential, cancellationToken), cancellationToken);
    }

    private static ProbeResult Probe(string host, ConnectionCredential? credential, CancellationToken cancellationToken)
    {
        try
        {
            using var options = new DComSessionOptions { Timeout = ProbeTimeout };
            if (credential is not null)
            {
                options.AddDestinationCredentials(new CimCredential(
                    PasswordAuthenticationMechanism.Default,
                    credential.Domain,
                    credential.UserName,
                    credential.Password));
            }

            using CimSession session = CimSession.Create(host, options);
            using var operationOptions = new CimOperationOptions
            {
                Timeout = ProbeTimeout,
                CancellationToken = cancellationToken,
            };

            // Cheapest authenticated round-trip: one property of the Win32_ComputerSystem singleton.
            foreach (CimInstance instance in session.QueryInstances(
                         @"root\cimv2", "WQL", "SELECT Name FROM Win32_ComputerSystem", operationOptions))
            {
                instance.Dispose();
                return ProbeResult.Online(); // reached + authenticated
            }

            return ProbeResult.Unreachable("No response from WMI.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CimException ex)
        {
            // Unreachable, or the credential couldn't authenticate. For a probe this is the
            // answer (surfaced to the row's Last error), not an exception to propagate.
            return ProbeResult.Unreachable($"WMI: {ex.Message}");
        }
    }
}
