using Vivre.Core.Credentials;

namespace Vivre.Core.Remoting;

/// <summary>
/// Turns on PowerShell Remoting (WinRM) on a target. Ported from the legacy
/// EnableWinRM plugin: because the target by definition can't be reached over WinRM
/// yet, this goes over <b>DCOM</b> (the old RPC/WMI channel) and runs
/// <c>Enable-PSRemoting</c> via <c>Win32_Process.Create</c>. Once it succeeds, the
/// machine becomes reachable through the normal <c>IPowerShellHost</c> path.
/// </summary>
public interface IWinRmEnabler
{
    /// <summary>
    /// Starts <c>Enable-PSRemoting -Force</c> on <paramref name="host"/> over DCOM
    /// using the current Windows identity. Returns a short status (with the spawned
    /// PID when available). Fire-and-forget on the target — it returns once the
    /// process is launched, not when remoting is fully configured.
    /// </summary>
    /// <param name="credential">Account for the DCOM connection; null = current Windows identity.</param>
    /// <exception cref="WinRmEnableException">The DCOM call failed or Win32_Process.Create returned non-zero.</exception>
    Task<string> EnableAsync(string host, ConnectionCredential? credential = null, CancellationToken cancellationToken = default);
}
