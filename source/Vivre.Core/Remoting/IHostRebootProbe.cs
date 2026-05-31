using System.Management.Automation;

namespace Vivre.Core.Remoting;

/// <summary>
/// Lightweight per-host pending-reboot probe used to keep the Windows Update view's
/// "Pending reboot" column live without requiring a full Check All / SCCM health pass.
/// </summary>
public interface IHostRebootProbe
{
    /// <summary>
    /// Checks the well-known reboot-pending markers on the target — Component Based Servicing,
    /// Windows Update Auto Update, PendingFileRenameOperations, a pending computer rename, and
    /// the SCCM client (if installed). Returns <c>true</c>/<c>false</c> when the probe ran,
    /// <c>null</c> when the call returned no usable data (e.g. unreachable).
    /// </summary>
    Task<bool?> IsRebootPendingAsync(
        string host,
        PSCredential? credential = null,
        CancellationToken cancellationToken = default);
}
