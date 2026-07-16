namespace Vivre.Core.Net;

/// <summary>Outcome of a single ICMP echo attempt against a host.</summary>
/// <param name="IsOnline">True when the host replied successfully.</param>
/// <param name="RoundtripMs">Round-trip time in milliseconds when online; null otherwise.</param>
/// <param name="Error">Human-readable reason when offline (timeout, DNS failure, …); null when online.</param>
/// <param name="ErrorKind">Typed shape of the failure (name-resolution vs everything else); <see cref="PingErrorKind.Other"/> when online or unclassified.</param>
public sealed record PingResult(bool IsOnline, long? RoundtripMs, string? Error, PingErrorKind ErrorKind = PingErrorKind.Other)
{
    public static PingResult Online(long roundtripMs) => new(true, roundtripMs, null);

    public static PingResult Offline(string error, PingErrorKind kind = PingErrorKind.Other) => new(false, null, error, kind);
}
