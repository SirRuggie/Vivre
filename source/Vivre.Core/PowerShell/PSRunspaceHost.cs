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

        return await RunInRunspaceAsync(runspace, script, onOutput: null, cancellationToken).ConfigureAwait(false);
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

        return await RunInRunspaceAsync(runspace, script, onOutput: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PSExecutionResult> RunRemoteStreamingAsync(
        string host,
        string script,
        Action<PSObject> onOutput,
        PSCredential? credential = null,
        int port = 5985,
        bool useSsl = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        ArgumentNullException.ThrowIfNull(onOutput);
        cancellationToken.ThrowIfCancellationRequested();

        var connectionInfo = new WSManConnectionInfo(
            useSsl,
            host,
            port,
            "/wsman",
            "http://schemas.microsoft.com/powershell/Microsoft.PowerShell",
            credential)
        {
            OpenTimeout = RemoteOpenTimeoutMs,
        };

        using Runspace runspace = RunspaceFactory.CreateRunspace(connectionInfo);

        Task openTask = Task.Run(runspace.Open);
        try
        {
            await openTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ = openTask.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
            throw;
        }

        return await RunInRunspaceAsync(runspace, script, onOutput, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared execution path for local and remote runspaces: invoke the script,
    /// capture the error/warning streams, and translate a cancellation-driven stop
    /// into <see cref="OperationCanceledException"/>. When <paramref name="onOutput"/>
    /// is non-null, the output stream is delivered live via
    /// <see cref="PSDataCollection{T}.DataAdded"/> as the script emits each object —
    /// this is what the streaming install/uninstall controller uses to forward per-line
    /// progress JSON back to the UI as it arrives rather than at end-of-script.
    /// </summary>
    private static async Task<PSExecutionResult> RunInRunspaceAsync(
        Runspace runspace,
        string script,
        Action<PSObject>? onOutput,
        CancellationToken cancellationToken)
    {
        using var ps = SmaPowerShell.Create();
        ps.Runspace = runspace;
        ps.AddScript(script);

        // Cancellation stops the running pipeline; the SDK then surfaces a
        // PipelineStoppedException, which we translate to OperationCanceledException.
        //
        // BeginStop, NOT Stop: CancellationTokenSource.Cancel() runs this callback
        // synchronously on the *caller's* thread — which is the UI thread for the Stop
        // button. PowerShell.Stop() BLOCKS until the pipeline has actually stopped, and
        // for a remote pipeline whose target is rebooting/unreachable that can take the
        // full WSMan timeout (minutes) — freezing the whole UI. BeginStop initiates the
        // stop and returns immediately: the awaited InvokeAsync below still throws
        // PipelineStoppedException once the stop lands, the runspace's using-dispose tears
        // the half-dead connection down, and the sweep's cancellation race has already
        // freed the UI. A cancellation callback must never throw, so swallow the benign
        // races (pipeline already completed/stopped/disposed — nothing left to stop).
        using CancellationTokenRegistration registration =
            cancellationToken.Register(static state =>
            {
                try
                {
                    ((SmaPowerShell)state!).BeginStop(null, null);
                }
                catch (Exception)
                {
                    // Pipeline already finished or was disposed between the token tripping
                    // and this callback — there is nothing left to cancel.
                }
            }, ps);

        // Pre-allocate the output collection so streaming-mode handlers can subscribe
        // before the pipeline starts producing items. In non-streaming mode this is the
        // same end-state collection that the synchronous overload would return.
        var output = new PSDataCollection<PSObject>();
        if (onOutput is not null)
        {
            output.DataAdded += (sender, args) =>
            {
                // Snapshot the new index off the collection — the handler may be invoked
                // after additional items have already been appended.
                PSObject? added = ((PSDataCollection<PSObject>)sender!)[args.Index];
                if (added is not null)
                {
                    try
                    {
                        onOutput(added);
                    }
                    catch
                    {
                        // A faulty consumer callback must not tear the pipeline down.
                    }
                }
            };
        }

        try
        {
            await ps.InvokeAsync<PSObject, PSObject>(input: null, output).ConfigureAwait(false);

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
