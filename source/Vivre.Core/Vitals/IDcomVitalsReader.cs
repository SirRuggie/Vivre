namespace Vivre.Core.Vitals;

/// <summary>
/// Reads OS vitals from a remote host over DCOM/WMI on the current Windows login (no credential
/// prompt). The primary consumer is <see cref="VitalsProbe"/>, which calls this when WinRM is
/// rejected with Kerberos error 0x80090322 so the row receives real numbers even though the fast
/// Kerberos path is broken. Keeping it behind an interface makes the routing unit-testable with a fake.
/// </summary>
public interface IDcomVitalsReader
{
    /// <summary>
    /// Reads vitals from <paramref name="host"/> over a DCOM CimSession using the caller's ambient
    /// Windows identity. Each underlying CIM query degrades to a null signal rather than throwing, so
    /// a partially-readable box still returns a usefully-populated snapshot.
    /// </summary>
    Task<MachineVitals> ReadAsync(string host, CancellationToken cancellationToken = default);
}
