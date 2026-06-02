using System.Management.Automation;

namespace Vivre.Core.Vitals;

/// <summary>
/// Reads a machine's OS-level health (disk / memory / CPU / uptime / stopped auto-services /
/// recent error events) over remoting and returns it as a typed <see cref="MachineVitals"/>.
/// The diagnostic counterpart to <see cref="Sccm.IConfigMgrClient"/> — same one-script-per-host,
/// read-only shape, so it stays a one-click action with no production-side effect.
/// </summary>
public interface IVitalsProbe
{
    /// <summary>
    /// Pulls vitals from <paramref name="host"/> (locally when it's this machine, otherwise over
    /// WinRM with <paramref name="credential"/>). Read-only. Each underlying probe degrades to a
    /// null signal rather than failing the whole pull, so a partially-readable box still returns a
    /// usefully-populated snapshot. Throws <see cref="VitalsProbeException"/> only when nothing at
    /// all comes back (host unreachable / no data).
    /// </summary>
    Task<MachineVitals> GetVitalsAsync(
        string host,
        PSCredential? credential = null,
        CancellationToken cancellationToken = default);
}
