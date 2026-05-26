using System.Management.Automation;

namespace Vivre.Core.Sccm;

/// <summary>
/// Queries a ConfigMgr (SCCM/MEMCM) client for its identity and health. The native
/// replacement for the legacy <c>sccmclictr.automation</c> wrapper — it runs a CIM
/// query through <see cref="PowerShell.IPowerShellHost"/> rather than talking to WMI
/// directly, so it reuses the verified local/remote runspace plumbing.
/// </summary>
public interface IConfigMgrClient
{
    /// <summary>
    /// Reads <paramref name="host"/>'s ConfigMgr client version, assigned site, and
    /// health signals. Runs locally when <paramref name="host"/> names the local
    /// machine, otherwise over WinRM.
    /// </summary>
    /// <param name="credential">Account for remote runs; null = current Windows identity. Ignored for local runs.</param>
    /// <exception cref="SccmQueryException">The target returned no client data (likely not a ConfigMgr client, or access denied).</exception>
    Task<SccmClientInfo> GetClientHealthAsync(string host, PSCredential? credential = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fires a ConfigMgr client schedule (e.g. machine policy, hardware inventory) on
    /// <paramref name="host"/> and returns the action's completion message. Runs locally
    /// when <paramref name="host"/> names the local machine, otherwise over WinRM.
    /// </summary>
    /// <remarks>TriggerSchedule requires elevation on the target; an unelevated caller gets "Access denied".</remarks>
    /// <exception cref="SccmQueryException">The trigger failed (access denied, not a ConfigMgr client, …).</exception>
    Task<string> TriggerScheduleAsync(string host, ScheduleAction action, PSCredential? credential = null, CancellationToken cancellationToken = default);
}
