namespace Vivre.Core.Updates;

/// <summary>
/// Ready-to-use format methods for the post-reboot verify flow outcome strings.
/// These methods are defined now but are NOT called anywhere yet — a future post-reboot rescan
/// will call them once that flow is implemented (see UPDATE_PLAN.md Phase 2). Do not wire them
/// up until then.
/// </summary>
public static class RebootOutcomeMessages
{
    /// <summary>The box came back online and the rescan shows it is fully up to date.</summary>
    /// <param name="installed">Number of updates confirmed installed.</param>
    public static string BackOnlineUpToDate(int installed) =>
        $"Back online · installed {installed} · up to date";

    /// <summary>The box came back online but more updates remain after the reboot.</summary>
    /// <param name="installed">Number of updates confirmed installed this pass.</param>
    /// <param name="remaining">Number of updates still applicable after the reboot.</param>
    public static string BackOnlineRemaining(int installed, int remaining) =>
        $"Back online · installed {installed} · {remaining} remaining";

    /// <summary>The box came back online but some updates failed; optionally some remain.</summary>
    /// <param name="installed">Number of updates successfully installed.</param>
    /// <param name="failed">Number of updates that failed to install.</param>
    /// <param name="remaining">Number of additional updates still applicable (0 if none).</param>
    public static string BackOnlineFailed(int installed, int failed, int remaining)
    {
        string msg = $"Back online · installed {installed} · {failed} failed";
        return remaining > 0 ? msg + $" · {remaining} remaining" : msg;
    }

    /// <summary>Updates installed without requiring a reboot — machine is up to date.</summary>
    /// <param name="installed">Number of updates installed.</param>
    public static string InstalledNoReboot(int installed) =>
        $"Installed {installed} · up to date";

    /// <summary>The box came back online but a reboot is still pending (e.g. another update's reboot).</summary>
    public static string RebootStillPending() =>
        "Back online · reboot still pending — re-check";

    /// <summary>The box is in the process of rebooting; waiting for it to return.</summary>
    public static string StillRebooting() =>
        "Rebooting · waiting for it to come back…";
}
