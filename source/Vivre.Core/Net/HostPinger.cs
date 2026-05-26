using System.Net.NetworkInformation;

namespace Vivre.Core.Net;

/// <summary>
/// ICMP implementation of <see cref="IHostPinger"/> over
/// <see cref="System.Net.NetworkInformation.Ping"/>. Uses the .NET 8+
/// <c>SendPingAsync</c> overload that takes a <see cref="TimeSpan"/> timeout and a
/// <see cref="CancellationToken"/>, so a grid-wide sweep can be torn down instantly
/// (the §8.1 "keeps running after cancel" bug is impossible by construction).
/// </summary>
public sealed class HostPinger : IHostPinger
{
    public async Task<PingResult> PingAsync(string host, int timeoutMs, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return PingResult.Offline("No host name.");
        }

        using var ping = new Ping();
        try
        {
            PingReply reply = await ping.SendPingAsync(
                host,
                TimeSpan.FromMilliseconds(timeoutMs),
                cancellationToken: cancellationToken);

            return reply.Status == IPStatus.Success
                ? PingResult.Online(reply.RoundtripTime)
                : PingResult.Offline(reply.Status.ToString());
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the caller's concern — let the sweep tear down.
            throw;
        }
        catch (PingException ex)
        {
            // Name-resolution / socket failures mean "not reachable", not a crash.
            return PingResult.Offline(ex.InnerException?.Message ?? ex.Message);
        }
    }
}
