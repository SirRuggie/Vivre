using System.Management.Automation;

namespace Vivre.Core.PowerShell;

/// <summary>
/// Hosts a PowerShell engine for the app and its plugins (the §5 plugin contract
/// hands this to every action). It is the native replacement for the legacy
/// <c>sccmclictr.automation</c> library / <c>System.Management.Automation 3.0</c>.
///
/// <see cref="RunRemoteAsync"/> is the real remoting replacement; it is implemented
/// but still pending live verification against a reachable target (REBUILD_PLAN.md
/// §0 / Spike #1 <c>RunRemote()</c>) — drive it with the <c>tools/RemoteRun</c> runner.
/// </summary>
public interface IPowerShellHost
{
    /// <summary>
    /// Runs <paramref name="script"/> in a local runspace and returns its output
    /// plus the captured error/warning streams. Pipeline failures (e.g. a cmdlet
    /// writing to the error stream) are reported in the result, not thrown; only
    /// cancellation throws <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="script"/> on <paramref name="host"/> over WinRM and returns
    /// the same shape of result as <see cref="RunLocalAsync"/>.
    /// </summary>
    /// <param name="credential">
    /// Account to authenticate as; <see langword="null"/> uses the current Windows
    /// identity (Negotiate/Kerberos). For NTLM/by-IP/workgroup targets the client may
    /// need the host in WinRM <c>TrustedHosts</c>, or use <paramref name="useSsl"/>.
    /// </param>
    /// <param name="port">WinRM port (5985 HTTP / 5986 HTTPS).</param>
    /// <param name="useSsl">Connect over HTTPS.</param>
    Task<PSExecutionResult> RunRemoteAsync(
        string host,
        string script,
        PSCredential? credential = null,
        int port = 5985,
        bool useSsl = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="script"/> on <paramref name="host"/> over WinRM and invokes
    /// <paramref name="onOutput"/> for each <see cref="PSObject"/> emitted by the script
    /// as it arrives — not buffered until completion. The caller uses this for live
    /// progress streams (the install/uninstall controller tails a per-update log on the
    /// target and re-emits each new line). Errors/warnings are still aggregated into the
    /// returned <see cref="PSExecutionResult"/>; only the output stream is delivered live.
    /// </summary>
    /// <remarks>
    /// Backed by <see cref="PSDataCollection{T}.DataAdded"/> on a pre-allocated output
    /// collection. The script is responsible for ending; if the consumer cancels via
    /// <paramref name="cancellationToken"/>, the pipeline is stopped (<c>ps.Stop()</c>)
    /// which lets any server-side <c>finally</c> block run (so the streaming controller
    /// can unregister the SYSTEM task even on a client-side cancel).
    /// </remarks>
    Task<PSExecutionResult> RunRemoteStreamingAsync(
        string host,
        string script,
        Action<PSObject> onOutput,
        PSCredential? credential = null,
        int port = 5985,
        bool useSsl = false,
        CancellationToken cancellationToken = default);
}
