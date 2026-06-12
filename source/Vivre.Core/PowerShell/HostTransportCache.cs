using System.Collections.Concurrent;

namespace Vivre.Core.PowerShell;

/// <summary>Which transport Vivre should use to reach a host this session.</summary>
public enum HostTransport
{
    /// <summary>Not yet determined — try the WinRM/Kerberos fast path first.</summary>
    Unknown = 0,

    /// <summary>WinRM/Kerberos works (the fast primary; ~one round-trip).</summary>
    WinRm,

    /// <summary>WinRM rejected Kerberos (0x80090322); use the SMB/DCOM path on the ambient login.</summary>
    SmbDcom,
}

/// <summary>
/// Session-scoped, thread-safe record of which transport reaches each host, so the doomed WinRM
/// connect is attempted at most once per host before the decision flips to SMB/DCOM. Same in-memory,
/// session-only lifetime as the credential model (<see cref="Credentials.ConnectionCredential"/>) —
/// nothing is persisted, so an app relaunch re-probes WinRM first and an AD-side Kerberos repair is
/// picked up automatically with no further action. Host keys compare case-insensitively (hostnames
/// are not case-sensitive). A separate Kerberos-degraded flag feeds the Vitals health finding.
/// </summary>
public sealed class HostTransportCache
{
    private readonly ConcurrentDictionary<string, HostTransport> _transport = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The decided transport for <paramref name="host"/>, or <see cref="HostTransport.Unknown"/>.</summary>
    public HostTransport Get(string host) =>
        _transport.TryGetValue(host, out HostTransport t) ? t : HostTransport.Unknown;

    /// <summary>
    /// Record that WinRM/Kerberos works for <paramref name="host"/> (the fast path). Set-if-absent
    /// (atomic <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/>): a late-arriving success must
    /// never clobber a concurrent <see cref="HostTransport.SmbDcom"/> rejection back to WinRm (which
    /// would re-pay the doomed connect) — the Kerberos rejection always wins.
    /// </summary>
    public void MarkWinRm(string host) => _transport.TryAdd(host, HostTransport.WinRm);

    /// <summary>
    /// Record that <paramref name="host"/> rejected Kerberos (0x80090322): flip it to the SMB/DCOM
    /// transport for the rest of the session. This single value is also the Kerberos-degraded signal the
    /// Vitals finding reads (<see cref="IsKerberosDegraded"/>), so the two can never disagree.
    /// </summary>
    public void MarkKerberosRejected(string host) => _transport[host] = HostTransport.SmbDcom;

    /// <summary>
    /// True if <paramref name="host"/> is routed over SMB/DCOM because Kerberos was rejected — the
    /// signal the Vitals scorer uses to dock the score and name the recommended fix. Derived from the
    /// single transport value (no second store) so transport and this flag can never disagree. (Never
    /// surfaced on an operation result; the health channel only.)
    /// </summary>
    public bool IsKerberosDegraded(string host) => Get(host) == HostTransport.SmbDcom;
}
