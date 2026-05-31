using System.Management.Automation;
using System.Management.Automation.Remoting;
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
        catch (Exception ex)
        {
            // Connect-phase failure (e.g. WinRM/PSRP shell init on a degraded target — the
            // InitialSessionState type-initializer / MaxShellsPerUser case). The open task faulted
            // and WaitAsync observed it, so no continuation is needed; translate it into a typed,
            // actionable exception instead of letting the raw SDK string propagate.
            throw TranslateRemotingException(ex, host);
        }

        try
        {
            return await RunInRunspaceAsync(runspace, script, onOutput: null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw TranslateRemotingException(ex, host);
        }
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
        catch (Exception ex)
        {
            throw TranslateRemotingException(ex, host);
        }

        try
        {
            return await RunInRunspaceAsync(runspace, script, onOutput, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw TranslateRemotingException(ex, host);
        }
    }

    /// <summary>
    /// Maps a raw remoting failure to a typed, actionable exception so callers don't surface the
    /// SDK's opaque strings — "The pipeline has been stopped." (a server-side session death) or
    /// "The type initializer for 'InitialSessionState' threw an exception" (a degraded WinRM/PSRP
    /// shell). A user-initiated cancel (<see cref="OperationCanceledException"/>) and anything we
    /// don't recognise are returned <em>unchanged</em>, so the existing "Cancelled" path and
    /// genuine in-script errors are untouched.
    /// </summary>
    internal static Exception TranslateRemotingException(Exception ex, string host)
    {
        if (ex is OperationCanceledException)
        {
            return ex;
        }

        // Shell/runspace init failure — pending-reboot corruption or MaxShellsPerUser exhaustion.
        // The text can arrive as a TypeInitializationException or wrapped in a remoting/runtime
        // exception, so scan the message chain culture-insensitively. Check this BEFORE the
        // session-death classification (a transport exception can carry this very message).
        if (MentionsInitialSessionState(ex))
        {
            return new RemoteShellInitException(host, ex);
        }

        // The remote session died for a non-cancellation reason (box rebooted / WinRM dropped /
        // the pipeline was stopped server-side). PipelineStoppedException's .Message is the
        // infamous "The pipeline has been stopped." that otherwise leaks into the UI. We translate
        // ONLY transport/pipeline-stopped — NOT RemoteException/RuntimeException, which can be a
        // genuine in-script error we must not mislabel as a lost connection.
        if (ex is PSRemotingTransportException or PipelineStoppedException)
        {
            return new RemoteSessionLostException(host, ex);
        }

        return ex;
    }

    private static bool MentionsInitialSessionState(Exception? ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e.Message.Contains("InitialSessionState", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
