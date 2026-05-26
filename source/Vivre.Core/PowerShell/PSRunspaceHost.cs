using System.Management.Automation;
using System.Management.Automation.Runspaces;
using SmaPowerShell = System.Management.Automation.PowerShell;

namespace Vivre.Core.PowerShell;

/// <summary>
/// <see cref="IPowerShellHost"/> backed by Microsoft.PowerShell.SDK, mirroring the
/// pattern proven in Spike #1. Each call uses its own runspace so concurrent sweeps
/// don't share engine state; a runspace pool can replace this if throughput demands it.
/// </summary>
public sealed class PSRunspaceHost : IPowerShellHost
{
    /// <summary>How long to wait for a remote WinRM connection before giving up (ms).</summary>
    private const int RemoteOpenTimeoutMs = 20_000;

    public async Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        cancellationToken.ThrowIfCancellationRequested();

        using Runspace runspace = RunspaceFactory.CreateRunspace();
        runspace.Open();

        return await RunInRunspaceAsync(runspace, script, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PSExecutionResult> RunRemoteAsync(
        string host,
        string script,
        PSCredential? credential = null,
        int port = 5985,
        bool useSsl = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        cancellationToken.ThrowIfCancellationRequested();

        // null credential => connect as the current Windows identity (Negotiate/Kerberos).
        var connectionInfo = new WSManConnectionInfo(
            useSsl,
            host,
            port,
            "/wsman",
            "http://schemas.microsoft.com/powershell/Microsoft.PowerShell",
            credential)
        {
            // Bound the connect so an unreachable host fails fast instead of hanging.
            OpenTimeout = RemoteOpenTimeoutMs,
        };

        using Runspace runspace = RunspaceFactory.CreateRunspace(connectionInfo);

        // Runspace.Open() blocks doing the network connect and does NOT observe the token once
        // it's running — so a rebooting/unreachable host would hang it for the full OpenTimeout.
        // Run it on the pool and stop *waiting* the instant the token trips (WaitAsync); the
        // using-dispose then tears down the half-open runspace, aborting the connect.
        Task openTask = Task.Run(runspace.Open);
        try
        {
            await openTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Keep the abandoned Open() from surfacing as an unobserved fault on dispose.
            _ = openTask.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
            throw;
        }

        return await RunInRunspaceAsync(runspace, script, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared execution path for local and remote runspaces: invoke the script,
    /// capture the error/warning streams, and translate a cancellation-driven stop
    /// into <see cref="OperationCanceledException"/>.
    /// </summary>
    private static async Task<PSExecutionResult> RunInRunspaceAsync(
        Runspace runspace,
        string script,
        CancellationToken cancellationToken)
    {
        using var ps = SmaPowerShell.Create();
        ps.Runspace = runspace;
        ps.AddScript(script);

        // Cancellation stops the running pipeline; the SDK then surfaces a
        // PipelineStoppedException, which we translate to OperationCanceledException.
        using CancellationTokenRegistration registration =
            cancellationToken.Register(static state => ((SmaPowerShell)state!).Stop(), ps);

        try
        {
            PSDataCollection<PSObject> output = await ps.InvokeAsync().ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            return new PSExecutionResult(
                Output: [.. output],
                Errors: [.. ps.Streams.Error.Select(static e => e.ToString())],
                Warnings: [.. ps.Streams.Warning.Select(static w => w.ToString())],
                HadErrors: ps.HadErrors);
        }
        catch (PipelineStoppedException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }
}
