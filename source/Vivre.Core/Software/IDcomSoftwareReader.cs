namespace Vivre.Core.Software;

/// <summary>
/// Reads whether a named product (and optionally a service) is present on a remote host over DCOM/WMI on
/// the current Windows login (no credential prompt). The primary consumer is <see cref="SoftwareProbe"/>,
/// which calls this when WinRM is rejected with Kerberos error 0x80090322 so the Software column shows a
/// real answer even though the fast Kerberos path is broken. Keeping it behind an interface makes the
/// routing unit-testable with a fake.
/// </summary>
public interface IDcomSoftwareReader
{
    /// <summary>
    /// Checks <paramref name="host"/> for an installed product whose display name or publisher contains
    /// <paramref name="query"/>, over a DCOM CimSession using the caller's ambient Windows identity (an
    /// explicit alternate credential is deliberately NOT used on this path). When
    /// <paramref name="serviceName"/> is non-null, also reports the first matching service's state.
    /// Unlike the vitals DCOM reader this does NOT degrade a failure to a null verdict — a read it cannot
    /// complete throws <see cref="SoftwareProbeException"/> so a genuine "installed" is never painted
    /// "missing".
    /// </summary>
    Task<SoftwareCheckResult> CheckAsync(string host, string query, string? serviceName, CancellationToken ct);
}
