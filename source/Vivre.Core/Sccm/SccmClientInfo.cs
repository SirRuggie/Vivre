namespace Vivre.Core.Sccm;

/// <summary>
/// Snapshot of a ConfigMgr client's identity + health, ported from the legacy
/// cm12 <c>HealthCheck.ps.txt</c> signals plus the <c>SMS_Client</c> identity
/// fields. Maps to the legacy <c>ComputerType</c> health columns (§11/§12):
/// RebootRequired ← HealthRebootStatus, MissingUpdates ← HealthUpdateMissingStatus,
/// RunningUpdates ← HealthInstallationRunningStatus, UserLoggedOn ← UsersLoggedOnStatus.
/// </summary>
/// <param name="ClientVersion">SMS_Client.ClientVersion (e.g. "5.00.9132.1000"); null if unknown.</param>
/// <param name="SiteCode">Assigned site code (e.g. "PS1"); null if unknown.</param>
/// <param name="RebootRequired">A reboot is pending (CCM, patch, or component-servicing).</param>
/// <param name="MissingUpdates">One or more required updates are non-compliant.</param>
/// <param name="RunningUpdates">An app/program/update install is in progress.</param>
/// <param name="UserLoggedOn">An interactive user session (explorer.exe) is present.</param>
/// <param name="LastBootTime">Last OS boot time (Win32_OperatingSystem.LastBootUpTime); null if unknown.</param>
/// <param name="ClientSdkFailed">The ROOT\ccm\ClientSDK namespace didn't answer, so
/// <see cref="MissingUpdates"/>/<see cref="RunningUpdates"/> are UNKNOWN — the false flags here are
/// fabricated from empty queries, not real compliance. The consumer must render them
/// indeterminate, never as a green "compliant".</param>
public sealed record SccmClientInfo(
    string? ClientVersion,
    string? SiteCode,
    bool RebootRequired,
    bool MissingUpdates,
    bool RunningUpdates,
    bool UserLoggedOn,
    DateTime? LastBootTime = null,
    bool ClientSdkFailed = false)
{
    /// <summary>True when nothing needs attention (no reboot, no missing updates, no running install).
    /// A ClientSDK failure is never healthy — the update state is unknown, not clean.</summary>
    public bool IsHealthy => !ClientSdkFailed && !(RebootRequired || MissingUpdates || RunningUpdates);
}
