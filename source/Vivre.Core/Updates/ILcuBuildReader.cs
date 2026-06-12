namespace Vivre.Core.Updates;

/// <summary>
/// Reads a host's OS build + UBR (e.g. 14393.9234) — the signal that decides whether a staged CU
/// actually committed. A seam so the verify logic is testable without a live box; the real
/// implementation reads it over DCOM on the operator's current login (the channel that works on the
/// Kerberos-broken Vision boxes).
/// </summary>
public interface ILcuBuildReader
{
    /// <summary>
    /// Reads <c>CurrentBuild</c> + <c>UBR</c> from the host's <c>CurrentVersion</c> key. Returns nulls
    /// when the host can't be read — offline, still booting, or DCOM/WMI not up yet. That is NOT a
    /// failure verdict: the caller must treat a null read as "try again", never as "rolled back".
    /// </summary>
    Task<(int? CurrentBuild, int? Ubr)> ReadAsync(string host, CancellationToken cancellationToken = default);
}
