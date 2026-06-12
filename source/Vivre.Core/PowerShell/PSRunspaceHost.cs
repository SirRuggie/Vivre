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

        // Ownership of the runspace transfers to RunInRunspaceAsync — it disposes on every path.
        // Local Open() is synchronous and cannot be abandoned; no connect-phase guard needed.
        Runspace runspace = RunspaceFactory.CreateRunspace();
        runspace.Open();

        return await RunInRunspaceAsync(runspace, script, onOutput: null, arguments: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="script"/> locally, binding <paramref name="arguments"/> to its
    /// <c>param()</c> block — used for runs that need a typed value passed in (e.g. a
    /// <see cref="PSCredential"/>) without baking it into the script text.
    /// </summary>
    public async Task<PSExecutionResult> RunLocalAsync(
        string script,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();

        // Ownership of the runspace transfers to RunInRunspaceAsync — see above.
        Runspace runspace = RunspaceFactory.CreateRunspace();
        runspace.Open();

        return await RunInRunspaceAsync(runspace, script, onOutput: null, arguments, cancellationToken).ConfigureAwait(false);
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

            // The SDK's default MaxConnectionRetryCount=5 schedules retries via
            // WSManClientSessionTransportManager.StartCreateRetry as an unguarded
            // ThreadPool.QueueUserWorkItem. When a per-host timeout causes us to abandon
            // Runspace.Open() and then dispose the runspace, the queued retry fires into
            // the torn-down session state → NullReferenceException on a raw pool thread →
            // process termination (no handler can catch raw-thread faults in modern .NET).
            // Setting 0 causes RetrySessionCreation to decline on the first transport error
            // so the work item is never scheduled; no retry race, no process kill.
            // Retry codes are refused/unavailable conditions (not slowness), so slow-but-alive
            // hosts are unaffected — OpenTimeout governs those independently. Our per-host
            // timeouts and re-run sweep supersede transport-level retry for a fleet tool.
            MaxConnectionRetryCount = 0,
        };

        Runspace runspace = RunspaceFactory.CreateRunspace(connectionInfo);

        // Runspace.Open() blocks doing the network connect and does NOT observe the token once
        // it's running — so a rebooting/unreachable host would hang it for the full OpenTimeout.
        // Run it on the pool and stop *waiting* the instant the token trips (WaitAsync).
        //
        // Connect-phase abandon-path disposal:
        //   We must NOT dispose the runspace synchronously while openTask is still live — that
        //   is the dispose-under-live-task race that causes the SDK to dereference torn-down
        //   transport state. Instead we attach a single continuation: after openTask settles it
        //   observes any exception and then disposes the runspace. Worst-case lifetime on abandon
        //   = OpenTimeout (20 s) — after which WSMan's own connect times out, the task settles,
        //   and the continuation disposes. On this path (OCE catch) we rethrow; the runspace is
        //   NOT disposed in any finally or catch other than the continuation.
        //
        // Connect-phase observed-fault disposal:
        //   openTask faulted and WaitAsync already observed it — the task is settled, so we
        //   dispose the runspace immediately (no race) and translate the exception.
        //
        // Connect success: ownership of the runspace transfers to RunInRunspaceAsync, which
        //   disposes it on every execute-phase path (success, observed fault, execute-abandon).
        Task openTask = Task.Run(runspace.Open);
        try
        {
            await openTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Defer disposal: continuation is the sole disposal path on connect-abandon.
            _ = openTask.ContinueWith(
                static (t, state) =>
                {
                    _ = t.Exception; // observe any fault (exactly once)
                    try { ((Runspace)state!).Dispose(); } catch { /* dispose races are benign */ }
                },
                runspace,
                TaskScheduler.Default);
            throw;
        }
        catch (Exception ex)
        {
            // openTask faulted; WaitAsync observed it — task is settled, safe to dispose now.
            runspace.Dispose();

            // Connect-phase failure (e.g. WinRM/PSRP shell init on a degraded target — the
            // InitialSessionState type-initializer / MaxShellsPerUser case). Translate into a
            // typed, actionable exception instead of letting the raw SDK string propagate.
            throw TranslateRemotingException(ex, host);
        }

        // Connect succeeded. Ownership of the runspace transfers to RunInRunspaceAsync.
        // (No using/finally here — RunInRunspaceAsync owns disposal from this point on.)
        try
        {
            return await RunInRunspaceAsync(runspace, script, onOutput: null, arguments: null, cancellationToken).ConfigureAwait(false);
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

            // See RunRemoteAsync for the full rationale. MaxConnectionRetryCount=0 prevents
            // WSManClientSessionTransportManager.StartCreateRetry from scheduling an unguarded
            // thread-pool work item that races disposal of the abandoned runspace → NRE →
            // process death. Retry codes are refused/unavailable (not slowness); OpenTimeout
            // governs slow-but-alive hosts independently.
            MaxConnectionRetryCount = 0,
        };

        Runspace runspace = RunspaceFactory.CreateRunspace(connectionInfo);

        // Same abandon-path deferral as RunRemoteAsync — see that method for full rationale.
        Task openTask = Task.Run(runspace.Open);
        try
        {
            await openTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ = openTask.ContinueWith(
                static (t, state) =>
                {
                    _ = t.Exception;
                    try { ((Runspace)state!).Dispose(); } catch { /* dispose races are benign */ }
                },
                runspace,
                TaskScheduler.Default);
            throw;
        }
        catch (Exception ex)
        {
            runspace.Dispose();
            throw TranslateRemotingException(ex, host);
        }

        // Connect succeeded. Ownership of the runspace transfers to RunInRunspaceAsync.
        try
        {
            return await RunInRunspaceAsync(runspace, script, onOutput, arguments: null, cancellationToken).ConfigureAwait(false);
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
    /// <remarks>
    /// <para>
    /// <strong>Ownership:</strong> This method takes ownership of <paramref name="runspace"/>
    /// and disposes it (together with its own <c>ps</c> and <c>output</c>) on every path —
    /// success, observed fault, and execute-phase abandonment. Callers must NOT dispose
    /// the runspace after calling this method.
    /// </para>
    /// <para>
    /// <strong>Execute-phase abandon:</strong> When the cancellation token fires while the
    /// invoke is still live, we stop <em>waiting</em> (via WaitAsync) but must not dispose
    /// the three disposables synchronously — that races the still-running task and causes the
    /// SDK to dereference torn-down state. A single continuation is attached that, after
    /// <c>invokeTask</c> settles, observes its exception (exactly once) and disposes
    /// <c>output</c> → <c>ps</c> → <c>runspace</c> in order. The <c>executeAbandoned</c>
    /// flag prevents the outer finally from also disposing (exactly-once guarantee; no
    /// reliance on SDK idempotence). Worst-case lifetime on execute-abandon: until BeginStop
    /// lands or WSMan times out internally (bounded by the host's WSMan timeout).
    /// </para>
    /// </remarks>
    private static async Task<PSExecutionResult> RunInRunspaceAsync(
        Runspace runspace,
        string script,
        Action<PSObject>? onOutput,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        // Declare before the try so they are visible in the finally; initialized to null so the
        // finally can null-check before disposing in the rare event construction itself throws.
        SmaPowerShell? ps = null;
        PSDataCollection<PSObject>? output = null;

        // executeAbandoned gates exactly-once disposal: when true the ContinueWith owns all
        // three disposables; the finally must not also dispose them.
        bool executeAbandoned = false;

        try
        {
            ps = SmaPowerShell.Create();
            output = new PSDataCollection<PSObject>();

            ps.Runspace = runspace;
            ps.AddScript(script);

            // Bind named arguments to the script's param() block. Passing values (e.g. a
            // PSCredential or a string[]) as real parameters keeps sensitive/awkward data out of
            // the script TEXT — no interpolation, no quoting hazards, nothing to leak if logged.
            if (arguments is not null)
            {
                foreach (KeyValuePair<string, object?> arg in arguments)
                {
                    ps.AddParameter(arg.Key, arg.Value);
                }
            }

            // Cancellation stops the running pipeline; the SDK then surfaces a
            // PipelineStoppedException, which we translate to OperationCanceledException.
            //
            // BeginStop, NOT Stop: CancellationTokenSource.Cancel() runs this callback
            // synchronously on the *caller's* thread — which is the UI thread for the Stop
            // button. PowerShell.Stop() BLOCKS until the pipeline has actually stopped, and
            // for a remote pipeline whose target is rebooting/unreachable that can take the
            // full WSMan timeout (minutes) — freezing the whole UI. BeginStop initiates the
            // stop and returns immediately: the awaited InvokeAsync below still throws
            // PipelineStoppedException once the stop lands. A cancellation callback must never
            // throw, so swallow the benign races (pipeline already completed/stopped/disposed).
            //
            // On the execute-phase abandon path (executeAbandoned=true) the token has by
            // definition already fired before we reach the finally, so the registration's
            // own disposal (which unregisters and completes synchronously) is safe even after
            // ps is queued for deferred disposal by the continuation — the registration holds
            // a reference to ps but does not call into it after Cancel() has returned.
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

            // Subscribe the streaming handler BEFORE starting the invoke so no early output
            // items are missed. In non-streaming mode output is a plain capture collection.
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

            Task invokeTask = ps.InvokeAsync<PSObject, PSObject>(input: null, output);
            try
            {
                // WaitAsync mirrors the connect-phase's abandon pattern: when the token trips we
                // stop waiting for InvokeAsync without synchronously disposing the live task's
                // resources. BeginStop (fired by the registration above) initiates the pipeline
                // stop asynchronously; the task will settle shortly after.
                await invokeTask.WaitAsync(cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();

                return new PSExecutionResult(
                    Output: [.. output],
                    Errors: [.. ps.Streams.Error.Select(static e => e.ToString())],
                    Warnings: [.. ps.Streams.Warning.Select(static w => w.ToString())],
                    HadErrors: ps.HadErrors);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Execute-phase abandon: do NOT dispose synchronously while invokeTask is live.
                // Transfer disposal to a single continuation that runs after invokeTask settles:
                // it observes the fault (exactly once via t.Exception) and disposes output → ps
                // → runspace in order, each wrapped in its own try/catch so that a failure in
                // one does not prevent the others from being disposed. The executeAbandoned flag
                // causes the outer finally to skip disposal (exactly-once; no SDK idempotence).
                executeAbandoned = true;
                SmaPowerShell capturedPs = ps;
                PSDataCollection<PSObject> capturedOutput = output;
                _ = invokeTask.ContinueWith(
                    static (t, state) =>
                    {
                        _ = t.Exception; // observe the fault exactly once
                        var (o, p, r) =
                            ((PSDataCollection<PSObject>, SmaPowerShell, Runspace))state!;
                        try { o.Dispose(); } catch { /* dispose races are benign */ }
                        try { p.Dispose(); } catch { /* dispose races are benign */ }
                        try { r.Dispose(); } catch { /* dispose races are benign */ }
                    },
                    (capturedOutput, capturedPs, runspace),
                    TaskScheduler.Default);
                throw new OperationCanceledException(cancellationToken);
            }
            catch (PipelineStoppedException) when (cancellationToken.IsCancellationRequested)
            {
                // InvokeAsync completed synchronously with a PipelineStoppedException before
                // WaitAsync could observe the token — invokeTask is settled, so the outer
                // finally's disposal is safe (no live-task race). Translate to OCE.
                throw new OperationCanceledException(cancellationToken);
            }
        }
        finally
        {
            // On success and observed-fault paths: invokeTask is settled, disposal is safe.
            // On PipelineStoppedException-as-cancel: invokeTask is settled, disposal is safe.
            // On execute-phase abandon (executeAbandoned=true): continuation owns disposal —
            // skip here to preserve exactly-once ownership without relying on SDK idempotence.
            // Null checks guard the rare case where construction of ps or output threw before
            // the try block fully initialised them.
            if (!executeAbandoned)
            {
                try { output?.Dispose(); } catch { }
                try { ps?.Dispose(); } catch { }
                try { runspace.Dispose(); } catch { }
            }
        }
    }
}
