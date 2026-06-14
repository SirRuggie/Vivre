using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;

namespace Vivre.Core.Updates;

/// <summary>
/// Post-reboot confirmation for non-2016 boxes: queries <c>Win32_OperatingSystem</c> over DCOM/CIM
/// (same 8-second timeout, ambient login — works on Kerberos-broken boxes) and returns
/// <see cref="RebootConfirmationOutcome.Confirmed"/> when the OS is queryable, or
/// <see cref="RebootConfirmationOutcome.NotReady"/> when it can't be reached yet.
///
/// <para>This strategy NEVER returns <see cref="RebootConfirmationOutcome.Failed"/>: whether
/// updates "took" on a non-2016 box is decided later by the WUA rescan after it returns online,
/// not here. The wave has already confirmed TCP-445 reachability before calling this; this probe
/// confirms the OS management stack is up (one step deeper than ping).</para>
/// </summary>
public sealed class ReadyConfirmation : IPostRebootConfirmation
{
    /// <summary>Injected so tests can simulate OS-queryable vs. unreachable without a live box.</summary>
    private readonly Func<string, CancellationToken, Task<bool>> _queryOs;

    /// <summary>Production constructor — uses a real DCOM CIM query.</summary>
    public ReadyConfirmation() : this(DcomOsQueryAsync) { }

    /// <summary>Test constructor — supply a delegate that simulates the OS query.</summary>
    internal ReadyConfirmation(Func<string, CancellationToken, Task<bool>> osQuery)
    {
        _queryOs = osQuery ?? throw new ArgumentNullException(nameof(osQuery));
    }

    /// <inheritdoc/>
    public async Task<RebootConfirmationResult> ConfirmAsync(string host, CancellationToken cancellationToken)
    {
        bool reachable = await _queryOs(host, cancellationToken).ConfigureAwait(false);
        return reachable
            ? new RebootConfirmationResult(RebootConfirmationOutcome.Confirmed, "Back online.")
            : new RebootConfirmationResult(RebootConfirmationOutcome.NotReady,
                "Back online — waiting for it to finish coming up…");
    }

    private static Task<bool> DcomOsQueryAsync(string host, CancellationToken cancellationToken) =>
        Task.Run(() => TryQueryOs(host, cancellationToken), cancellationToken);

    private static bool TryQueryOs(string host, CancellationToken cancellationToken)
    {
        try
        {
            var timeout = TimeSpan.FromSeconds(8);
            using var options = new DComSessionOptions { Timeout = timeout };
            using CimSession session = CimSession.Create(host, options);
            using var cimOptions = new CimOperationOptions
            {
                Timeout = timeout,
                CancellationToken = cancellationToken,
            };

            foreach (CimInstance instance in session.QueryInstances(
                @"root\cimv2", "WQL",
                "SELECT BuildNumber FROM Win32_OperatingSystem",
                cimOptions))
            {
                using (instance)
                {
                    return true; // OS stack answered — box is up
                }
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Offline, still booting, DCOM not up — not a failure; caller retries.
            return false;
        }
    }
}
