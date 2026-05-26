namespace Vivre.Core.Remoting;

/// <summary>Outcome of an authenticated WMI/DCOM reachability probe.</summary>
/// <param name="Reachable">True when the host answered the WMI query (reached + authenticated).</param>
/// <param name="Error">Human-readable reason when not reachable (RPC unavailable, access denied, …); null when reachable.</param>
public sealed record ProbeResult(bool Reachable, string? Error)
{
    public static ProbeResult Online() => new(true, null);

    public static ProbeResult Unreachable(string? error) => new(false, error);
}
