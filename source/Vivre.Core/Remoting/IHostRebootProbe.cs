using System.Management.Automation;

namespace Vivre.Core.Remoting;

/// <summary>
/// Lightweight per-host pending-reboot probe used to keep the Windows Update view's
/// "Pending reboot" column live without requiring a full Check All / SCCM health pass.
/// </summary>
public interface IHostRebootProbe
{
    /// <summary>
    /// Checks the reliable reboot-pending markers on the target — Component Based Servicing,
    /// Windows Update Auto Update, a pending computer rename, and the SCCM client (if installed).
    /// PendingFileRenameOperations is deliberately excluded — it over-reports on long-uptime servers
    /// (benign AV/installer file ops). Returns <c>true</c>/<c>false</c> when the probe ran,
    /// <c>null</c> when the call returned no usable data (e.g. unreachable).
    /// </summary>
    /// <param name="background">
    /// <see langword="true"/> for a low-priority background poll (the monitor's reboot-pending tick) so
    /// it yields to operator actions on the per-host WinRM shell gate; an operator-triggered verify
    /// leaves it <see langword="false"/>.
    /// </param>
    Task<bool?> IsRebootPendingAsync(
        string host,
        PSCredential? credential = null,
        CancellationToken cancellationToken = default,
        bool background = false);
}
