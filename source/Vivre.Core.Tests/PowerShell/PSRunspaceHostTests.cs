using System.Management.Automation;
using System.Management.Automation.Remoting;
using Vivre.Core.PowerShell;
using Xunit;

namespace Vivre.Core.Tests.PowerShell;

public class PSRunspaceHostTests
{
    private readonly PSRunspaceHost _host = new();

    [Fact]
    public async Task Runs_script_and_returns_pipeline_output()
    {
        PSExecutionResult result = await _host.RunLocalAsync("1 + 1");

        Assert.False(result.HadErrors);
        Assert.Single(result.Output);
        Assert.Equal(2, (int)result.Output[0].BaseObject);
    }

    [Fact]
    public async Task Get_Process_returns_objects()
    {
        // Local PowerShell host smoke test.
        PSExecutionResult result = await _host.RunLocalAsync("Get-Process | Select-Object -First 1");

        Assert.NotEmpty(result.Output);
        Assert.False(result.HadErrors);
    }

    [Fact]
    public async Task Captures_error_stream_without_throwing()
    {
        PSExecutionResult result = await _host.RunLocalAsync("Write-Error 'boom'");

        Assert.True(result.HadErrors);
        Assert.Contains(result.Errors, e => e.Contains("boom"));
    }

    [Fact]
    public async Task Captures_warning_stream()
    {
        PSExecutionResult result = await _host.RunLocalAsync("Write-Warning 'heads up'");

        Assert.Contains(result.Warnings, w => w.Contains("heads up"));
    }

    [Fact]
    public async Task Bounded_capture_drains_all_streams_and_reports_truncation()
    {
        PSScriptCaptureResult result = await _host.RunLocalScriptCaptureAsync(
            "$VerbosePreference='Continue'; 1..1000 | % { Write-Output \"o$_\"; Write-Warning \"w$_\"; Write-Verbose \"v$_\" }; Write-Error boom",
            new PSScriptCaptureLimits(3, 100));
        Assert.Equal(3, result.Output.Count);
        Assert.Equal(3, result.Warnings.Count);
        Assert.Equal(3, result.Verbose.Count);
        Assert.True(result.HadErrors);
        Assert.True(result.WasTruncated);
        Assert.Contains(result.Errors, e => e.Contains("boom"));
    }

    [Fact]
    public async Task Bounded_capture_preserves_cancellation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _host.RunLocalScriptCaptureAsync("while ($true) { Write-Output x }", new(5, 100), cts.Token));
    }

    [Fact]
    public async Task Bounded_capture_keeps_error_signal_when_error_stream_overflows()
    {
        PSScriptCaptureResult result = await _host.RunLocalScriptCaptureAsync(
            "1..100 | % { Write-Error \"error-$_\" }", new(1, 5));
        Assert.True(result.HadErrors);
        Assert.True(result.WasTruncated);
        Assert.Single(result.Errors);
        Assert.Equal(5, result.Errors[0].Length);
    }

    [Fact]
    public async Task Concurrent_bounded_calls_do_not_leak_capture_state()
    {
        Task<PSScriptCaptureResult> first = _host.RunLocalScriptCaptureAsync("'first'", new(5, 100));
        Task<PSScriptCaptureResult> second = _host.RunLocalScriptCaptureAsync("'second'", new(5, 100));
        PSScriptCaptureResult[] results = await Task.WhenAll(first, second);
        Assert.Equal("first", Assert.Single(results[0].Output));
        Assert.Equal("second", Assert.Single(results[1].Output));
    }

    [Fact]
    public void Presentation_buffer_enforces_exact_bound()
    {
        var buffer = new BoundedTextBuffer(10);
        buffer.Append("123456789012345");
        Assert.Equal(10, buffer.ToString().Length);
        Assert.True(buffer.WasTruncated);
    }

    [Fact]
    public void Presentation_keeps_truncation_marker_and_errors_ahead_of_large_output()
    {
        var result = new PSScriptCaptureResult(
            [new string('x', 1000)], ["important error"], [], [], [], [], [], HadErrors: true, WasTruncated: true);
        string text = PSScriptCapturePresentation.Format(result, 80);
        Assert.True(text.Length <= 80);
        Assert.StartsWith("[output truncated]", text);
        Assert.Contains("important error", text);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(18)]
    public void Presentation_honors_tiny_exact_bounds(int maximum)
    {
        var result = new PSScriptCaptureResult(["payload"], ["error"], [], [], [], [], [], true, true);
        Assert.True(PSScriptCapturePresentation.Format(result, maximum).Length <= maximum);
    }

    [Fact]
    public async Task Cancellation_stops_a_long_running_script()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _host.RunLocalAsync("Start-Sleep -Seconds 30", cts.Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_script_throws(string? script)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _host.RunLocalAsync(script!));
    }

    // Remote argument validation runs before any network I/O, so these are safe to
    // assert without a reachable target. Live WinRM verification is manual via tools/RemoteRun.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunRemote_blank_host_throws(string? host)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _host.RunRemoteAsync(host!, "hostname"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunRemote_blank_script_throws(string? script)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _host.RunRemoteAsync("NYC-FP1", script!));
    }

    // --- remoting-failure translation (the DCVCOLUMBUS leak: a server-side session death or a
    //     degraded WinRM shell must NOT surface as a raw SDK string). Pure logic, no network. ---

    [Fact]
    public void Translate_pipeline_stopped_without_cancel_becomes_session_lost()
    {
        // PipelineStoppedException.Message is exactly "The pipeline has been stopped." — the string
        // that leaked into the update-message column.
        Exception translated = PSRunspaceHost.TranslateRemotingException(new PipelineStoppedException(), "DCVCOLUMBUS");

        RemoteSessionLostException lost = Assert.IsType<RemoteSessionLostException>(translated);
        Assert.Equal("DCVCOLUMBUS", lost.Host);
        // The message no longer includes the host name (the grid row already names the machine);
        // verify it contains the actionable mid-run-drop wording instead.
        Assert.Contains("may have rebooted", lost.Message);
    }

    [Fact]
    public void Translate_transport_exception_becomes_session_lost()
    {
        Exception translated = PSRunspaceHost.TranslateRemotingException(
            new PSRemotingTransportException("Connection to the remote server failed."), "HOST1");

        Assert.IsType<RemoteSessionLostException>(translated);
    }

    [Fact]
    public void Translate_initial_session_state_becomes_shell_init()
    {
        var raw = new Exception(
            "The type initializer for 'System.Management.Automation.Runspaces.InitialSessionState' threw an exception.");

        Exception translated = PSRunspaceHost.TranslateRemotingException(raw, "DCVCOLUMBUS");

        RemoteShellInitException shell = Assert.IsType<RemoteShellInitException>(translated);
        Assert.Equal("DCVCOLUMBUS", shell.Host);
        Assert.Contains("temporarily unavailable", shell.Message);
        Assert.DoesNotContain("Reboot the target", shell.Message); // no needless reboot advice — transient hiccup
    }

    [Fact]
    public void Translate_initial_session_state_in_inner_exception_becomes_shell_init()
    {
        var raw = new InvalidOperationException("wrapper", new Exception("boom: InitialSessionState ctor failed"));

        Assert.IsType<RemoteShellInitException>(PSRunspaceHost.TranslateRemotingException(raw, "HOST1"));
    }

    [Fact]
    public void Translate_shell_init_wins_over_transport_classification()
    {
        // A transport exception can itself carry the InitialSessionState message — that's the
        // degraded-shell case, not a generic lost connection, so it must map to shell-init.
        var raw = new PSRemotingTransportException(
            "Processing data from remote server failed: type initializer for 'InitialSessionState' threw an exception");

        Assert.IsType<RemoteShellInitException>(PSRunspaceHost.TranslateRemotingException(raw, "HOST1"));
    }

    [Fact]
    public void Translate_leaves_cancellation_untouched()
    {
        var oce = new OperationCanceledException();

        // A user Stop must still map to "Cancelled" upstream — never reclassified as a lost session.
        Assert.Same(oce, PSRunspaceHost.TranslateRemotingException(oce, "HOST1"));
    }

    [Fact]
    public void Translate_leaves_unrelated_exception_untouched()
    {
        // A genuine in-script error must not be mislabeled as a connection loss.
        var other = new InvalidOperationException("genuine script error");

        Assert.Same(other, PSRunspaceHost.TranslateRemotingException(other, "HOST1"));
    }

    // --- connect-time Kerberos wrong-principal (0x80090322): an AUTH rejection at login, NOT a
    //     mid-run session drop. Must classify distinctly (so callers switch to SMB/DCOM) and must
    //     never read "the remote session ended / the target may have rebooted". Pure logic. ---

    [Fact]
    public void Translate_kerberos_wrong_principal_in_message_becomes_kerberos_rejected()
    {
        // The live form: the SSPI code is carried in the transport exception's message text
        // ("...errorcode 0x80090322 occurred while using Kerberos authentication...").
        var raw = new PSRemotingTransportException(
            "Connecting to remote server APVVISIONF5 failed: WinRM cannot process the request. " +
            "The following error with errorcode 0x80090322 occurred while using Kerberos authentication.");

        Exception translated = PSRunspaceHost.TranslateRemotingException(raw, "APVVISIONF5");

        KerberosWrongPrincipalException k = Assert.IsType<KerberosWrongPrincipalException>(translated);
        Assert.Equal("APVVISIONF5", k.Host);
    }

    [Fact]
    public void Translate_kerberos_wrong_principal_in_inner_exception_becomes_kerberos_rejected()
    {
        var raw = new InvalidOperationException("wrapper",
            new Exception("An unknown security error occurred: SEC_E_WRONG_PRINCIPAL."));

        Assert.IsType<KerberosWrongPrincipalException>(
            PSRunspaceHost.TranslateRemotingException(raw, "HOST1"));
    }

    [Fact]
    public void Translate_generic_transport_without_kerberos_code_still_session_lost()
    {
        // A transport failure that is NOT 0x80090322 must still map to session-lost, not Kerberos.
        Exception translated = PSRunspaceHost.TranslateRemotingException(
            new PSRemotingTransportException("The WinRM client cannot complete the operation within the time specified."),
            "HOST1");

        Assert.IsType<RemoteSessionLostException>(translated);
    }

    [Fact]
    public void Translate_kerberos_wrong_principal_by_typed_errorcode_becomes_kerberos_rejected()
    {
        // The other stack form: the SSPI status arrives as the typed ErrorCode, NOT in the message text.
        // The message deliberately omits 0x80090322/SEC_E_WRONG_PRINCIPAL so this proves the typed path fired.
        var raw = new PSRemotingTransportException("Connecting to the remote server failed.")
        {
            ErrorCode = unchecked((int)0x80090322),
        };

        Assert.IsType<KerberosWrongPrincipalException>(PSRunspaceHost.TranslateRemotingException(raw, "HOST1"));
    }

    [Fact]
    public void Translate_kerberos_target_unknown_becomes_kerberos_rejected()
    {
        // The second Kerberos failure seen in the fleet (0x80090303 SEC_E_TARGET_UNKNOWN): the host has no
        // SPN (typically not domain-joined). This is the VERBATIM message a SCERIS test box returned — it
        // must classify as a Kerberos fallback (→ SMB/DCOM), not a session loss.
        var raw = new PSRemotingTransportException(
            "Connecting to remote server APVSCERISEPMTEST failed with the following error message : " +
            "WinRM cannot process the request. The following error occurred while using Kerberos " +
            "authentication: Cannot find the computer APVSCERISEPMTEST. Verify that the computer exists on " +
            "the network and that the name provided is spelled correctly.");

        Exception translated = PSRunspaceHost.TranslateRemotingException(raw, "APVSCERISEPMTEST");

        KerberosWrongPrincipalException k = Assert.IsType<KerberosWrongPrincipalException>(translated);
        Assert.Equal("APVSCERISEPMTEST", k.Host);
    }

    [Fact]
    public void Translate_kerberos_target_unknown_by_typed_errorcode_becomes_kerberos_rejected()
    {
        var raw = new PSRemotingTransportException("Connecting to the remote server failed.")
        {
            ErrorCode = unchecked((int)0x80090303),
        };

        Assert.IsType<KerberosWrongPrincipalException>(PSRunspaceHost.TranslateRemotingException(raw, "HOST1"));
    }

    [Fact]
    public void Translate_cannot_find_computer_without_kerberos_stays_session_lost()
    {
        // Guard the broadened matcher: a "cannot find the computer" failure that does NOT mention Kerberos
        // (an offline/unreachable host) must still be a session-loss, not a mis-flagged Kerberos fallback —
        // otherwise a transiently-down box would get wrongly routed to SMB/DCOM for the session.
        Exception translated = PSRunspaceHost.TranslateRemotingException(
            new PSRemotingTransportException(
                "WinRM cannot complete the operation. Verify that the specified computer name is valid, " +
                "that the computer is accessible over the network, and that a firewall exception for the " +
                "WinRM service is enabled."),
            "HOST1");

        Assert.IsType<RemoteSessionLostException>(translated);
    }

    [Fact]
    public void IsWinRmUnavailable_true_for_kerberos_and_session_lost()
    {
        // The two failures the no-fallback ops gate on (Kerberos rejection + connect-time/transport loss).
        Assert.True(new KerberosWrongPrincipalException("H", new Exception()).IsWinRmUnavailable());
        Assert.True(new RemoteSessionLostException("H", new Exception()).IsWinRmUnavailable());
        Assert.True(new RemoteSessionLostException("H", new Exception(), atConnect: true).IsWinRmUnavailable());
    }

    [Fact]
    public void IsWinRmUnavailable_false_for_shell_init_and_unrelated()
    {
        // Shell-init carries its own calm, retry-oriented message; unrelated errors aren't WinRM-unavailable.
        Assert.False(new RemoteShellInitException("H", new Exception()).IsWinRmUnavailable());
        Assert.False(new InvalidOperationException("boom").IsWinRmUnavailable());
        Assert.False(new OperationCanceledException().IsWinRmUnavailable());
    }

    // --- execute-phase abandon: no unobserved task exceptions (Fix 2 regression guard) ---

    /// <summary>
    /// Verifies the execute-phase abandon path: cancelling a long-running local script throws
    /// OperationCanceledException promptly and leaves no unobserved task exception behind.
    /// <para>
    /// RunLocalAsync flows through RunInRunspaceAsync, which is the shared execute path for both
    /// local and remote calls. On abandon, a ContinueWith disposes the resources after invokeTask
    /// settles; this test forces GC to trigger any pending finalizers that would fire the
    /// UnobservedTaskException event if that continuation were missing or incorrectly written.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Cancel_during_execute_phase_throws_OCE_and_leaves_no_unobserved_task_exception()
    {
        bool unobservedRaised = false;
        EventHandler<UnobservedTaskExceptionEventArgs> handler =
            (_, __) => unobservedRaised = true;

        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            // Cancel after ~1 second; generous outer bound of 10 s keeps CI flake-free.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                var task = _host.RunLocalAsync("Start-Sleep -Seconds 30", cts.Token);

                // Give the task at most 10 s to surface the OCE — far more than needed in
                // practice (~1 s cancel + BeginStop settle time), but robust against a loaded
                // CI runner. WaitAsync itself throws OCE if the inner task doesn't complete
                // in time, so the assertion still catches it either way.
                await task.WaitAsync(TimeSpan.FromSeconds(10));
            });

            // Allow the abandoned invokeTask's continuation to run and be GC-finalised.
            // Two rounds ensure that any Task finalizers (which fire the unobserved event)
            // complete, even on generational heaps where a single collect may not reach all
            // generations in one pass.
            await Task.Delay(200);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();

            Assert.False(unobservedRaised,
                "An unobserved task exception was raised after execute-phase cancellation. " +
                "The abandon-path continuation in RunInRunspaceAsync is missing or incorrect.");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }
}
