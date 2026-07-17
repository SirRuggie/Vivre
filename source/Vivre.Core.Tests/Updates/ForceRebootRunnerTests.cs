using System.Management.Automation;
using Vivre.Core.PowerShell;
using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Locks the Force-reboot channel decision: WinRM normally, with a fallback onto the injected
/// <see cref="IRebootTrigger"/> for EXACTLY ONE failure class — a WinRM Kerberos auth rejection, where
/// the command provably never reached the box. Every other WinRM failure (a mid-command session loss, a
/// timeout/cancel, an unknown error, or the box itself refusing the command) surfaces with NO fallback,
/// so an ambiguous failure can never double-reboot a box.
/// </summary>
public class ForceRebootRunnerTests
{
    private const string Host = "NYC-SRV1";

    [Fact]
    public async Task WinRm_success_reboots_over_winrm_and_never_touches_the_trigger()
    {
        var host = new FakeHost { RemoteResult = Clean() };
        var trigger = new FakeTrigger();
        var runner = new ForceRebootRunner(host, trigger);

        ForceRebootResult result = await runner.RebootAsync(Host, credential: null, CancellationToken.None);

        Assert.Equal(ForceRebootChannel.WinRm, result.Channel);
        Assert.Equal(RebootDispatch.Issued, result.Dispatch);
        Assert.Null(result.Error);
        Assert.Equal(0, trigger.CallCount);
        Assert.True(host.RanRemote);
        Assert.False(host.RanLocal);
    }

    [Fact]
    public async Task Kerberos_auth_rejection_falls_back_to_the_existing_dcom_trigger_exactly_once()
    {
        var host = new FakeHost { RemoteThrow = new KerberosWrongPrincipalException("HOST", new Exception("0x80090322")) };
        var trigger = new FakeTrigger { Result = RebootDispatch.Issued };
        var runner = new ForceRebootRunner(host, trigger);

        ForceRebootResult result = await runner.RebootAsync(Host, credential: null, CancellationToken.None);

        Assert.Equal(ForceRebootChannel.Dcom, result.Channel);
        Assert.Equal(RebootDispatch.Issued, result.Dispatch);
        Assert.Null(result.Error);
        Assert.Equal(1, trigger.CallCount);
        Assert.Equal(Host, trigger.LastHost);      // the SAME host, not the exception's host arg
        Assert.True(trigger.LastForced);           // completed as a forced reboot
    }

    [Fact]
    public async Task Kerberos_fallback_reports_already_in_progress_without_refiring()
    {
        var host = new FakeHost { RemoteThrow = new KerberosWrongPrincipalException("HOST", new Exception("0x80090322")) };
        var trigger = new FakeTrigger { Result = RebootDispatch.AlreadyInProgress };
        var runner = new ForceRebootRunner(host, trigger);

        ForceRebootResult result = await runner.RebootAsync(Host, credential: null, CancellationToken.None);

        Assert.Equal(ForceRebootChannel.Dcom, result.Channel);
        Assert.Equal(RebootDispatch.AlreadyInProgress, result.Dispatch);
        Assert.Equal(1, trigger.CallCount); // the box is going down on its own — nothing re-fired
    }

    [Fact]
    public async Task Kerberos_fallback_failure_propagates_after_a_single_invoke()
    {
        var host = new FakeHost { RemoteThrow = new KerberosWrongPrincipalException("HOST", new Exception("0x80090322")) };
        var trigger = new FakeTrigger { Throw = new InvalidOperationException("DCOM: 1191. SMB/SCM fallback: access denied") };
        var runner = new ForceRebootRunner(host, trigger);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RebootAsync(Host, credential: null, CancellationToken.None));

        Assert.Equal(1, trigger.CallCount); // both-channels-failed surfaces; never retried
    }

    [Fact]
    public async Task Session_lost_after_auth_does_NOT_fall_back()
    {
        // The session can drop MID-command — the reboot may already have fired; issuing a DCOM reboot on
        // top of that is the double-reboot risk. This is the locked narrow-trigger decision: only a
        // Kerberos auth REJECTION (nothing ran) is safe to fall back on.
        var host = new FakeHost { RemoteThrow = new RemoteSessionLostException("HOST", new Exception()) };
        var trigger = new FakeTrigger();
        var runner = new ForceRebootRunner(host, trigger);

        await Assert.ThrowsAsync<RemoteSessionLostException>(
            () => runner.RebootAsync(Host, credential: null, CancellationToken.None));

        Assert.Equal(0, trigger.CallCount);
    }

    [Fact]
    public async Task Unknown_winrm_failure_does_NOT_fall_back()
    {
        var host = new FakeHost { RemoteThrow = new InvalidOperationException("something unexpected") };
        var trigger = new FakeTrigger();
        var runner = new ForceRebootRunner(host, trigger);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RebootAsync(Host, credential: null, CancellationToken.None));

        Assert.Equal(0, trigger.CallCount);
    }

    [Fact]
    public async Task Cancellation_propagates_without_fallback()
    {
        var host = new FakeHost { RemoteThrow = new OperationCanceledException() };
        var trigger = new FakeTrigger();
        var runner = new ForceRebootRunner(host, trigger);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => runner.RebootAsync(Host, credential: null, CancellationToken.None));

        Assert.Equal(0, trigger.CallCount);
    }

    [Fact]
    public async Task Command_ran_but_reported_errors_surfaces_without_fallback()
    {
        // WinRM WORKED and the box itself refused the command — falling back here is the double-act risk,
        // so the error is surfaced verbatim with no fallback.
        var host = new FakeHost { RemoteResult = new PSExecutionResult([], ["Access is denied.(5)"], [], HadErrors: true) };
        var trigger = new FakeTrigger();
        var runner = new ForceRebootRunner(host, trigger);

        ForceRebootResult result = await runner.RebootAsync(Host, credential: null, CancellationToken.None);

        Assert.Equal(ForceRebootChannel.WinRm, result.Channel);
        Assert.Null(result.Dispatch);
        Assert.Equal("Access is denied.(5)", result.Error);
        Assert.Equal(0, trigger.CallCount);
    }

    [Fact]
    public async Task Local_host_runs_the_local_lane()
    {
        var host = new FakeHost { LocalResult = Clean() };
        var trigger = new FakeTrigger();
        var runner = new ForceRebootRunner(host, trigger);

        ForceRebootResult result = await runner.RebootAsync("localhost", credential: null, CancellationToken.None);

        Assert.True(host.RanLocal);
        Assert.False(host.RanRemote);
        Assert.Equal(ForceRebootChannel.WinRm, result.Channel);
        Assert.Equal(RebootDispatch.Issued, result.Dispatch);
        Assert.Equal(0, trigger.CallCount);
    }

    private static PSExecutionResult Clean() => new([], [], [], HadErrors: false);

    private sealed class FakeHost : IPowerShellHost
    {
        public PSExecutionResult? LocalResult { get; init; }
        public PSExecutionResult? RemoteResult { get; init; }
        public Exception? LocalThrow { get; init; }
        public Exception? RemoteThrow { get; init; }
        public bool RanLocal { get; private set; }
        public bool RanRemote { get; private set; }

        public Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken cancellationToken = default)
        {
            RanLocal = true;
            if (LocalThrow is not null) throw LocalThrow;
            return Task.FromResult(LocalResult ?? throw new InvalidOperationException("FakeHost: no local result configured."));
        }

        public Task<PSExecutionResult> RunLocalAsync(
            string script,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("ForceRebootRunner tests don't exercise the parameterized local path.");

        public Task<PSExecutionResult> RunRemoteAsync(
            string host,
            string script,
            PSCredential? credential = null,
            int port = 5985,
            bool useSsl = false,
            CancellationToken cancellationToken = default,
            bool background = false)
        {
            RanRemote = true;
            if (RemoteThrow is not null) throw RemoteThrow;
            return Task.FromResult(RemoteResult ?? throw new InvalidOperationException("FakeHost: no remote result configured."));
        }

        public Task<PSExecutionResult> RunRemoteStreamingAsync(
            string host,
            string script,
            Action<PSObject> onOutput,
            PSCredential? credential = null,
            int port = 5985,
            bool useSsl = false,
            CancellationToken cancellationToken = default,
            bool background = false) =>
            throw new NotSupportedException("ForceRebootRunner tests don't exercise the streaming path.");
    }

    private sealed class FakeTrigger : IRebootTrigger
    {
        public RebootDispatch Result { get; init; } = RebootDispatch.Issued;
        public Exception? Throw { get; init; }
        public int CallCount { get; private set; }
        public string? LastHost { get; private set; }
        public bool LastForced { get; private set; }

        public Task<RebootDispatch> RebootAsync(string host, bool forced, CancellationToken cancellationToken)
        {
            CallCount++;
            LastHost = host;
            LastForced = forced;
            if (Throw is not null) throw Throw;
            return Task.FromResult(Result);
        }
    }
}
