namespace Vivre.Core.Net;

/// <summary>Outcome of a single ICMP echo attempt against a host.</summary>
/// <param name="IsOnline">True when the host replied successfully.</param>
/// <param name="RoundtripMs">Round-trip time in milliseconds when online; null otherwise.</param>
/// <param name="Error">Human-readable reason when offline (timeout, DNS failure, …); null when online.</param>
public sealed record PingResult(bool IsOnline, long? RoundtripMs, string? Error)
{
    public static PingResult Online(long roundtripMs) => new(true, roundtripMs, null);

    public static PingResult Offline(string error) => new(false, null, error);
}
