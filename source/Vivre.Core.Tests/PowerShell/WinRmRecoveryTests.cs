using System.Collections.Concurrent;
using System.Management.Automation;
using System.Security;
using Vivre.Core.PowerShell;
using Xunit;

namespace Vivre.Core.Tests.PowerShell;

public class WinRmRecoveryTests
{
    private static readonly PSExecutionResult Empty = new([], [], [], false);
    private static PSExecutionResult Output(string value) => new([PSObject.AsPSObject(value)], [], [], false);

    private sealed record Call(string Host, string Script, PSCredential? Credential, int Port, bool UseSsl, bool Streaming);

    private sealed class ScriptedHost(Func<Call, CancellationToken, Task<PSExecutionResult>> run) : IPowerShellHost
    {
        public int Calls;
        public Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken cancellationToken = default) =>
            Task.FromResult(Empty);

        public Task<PSExecutionResult> RunRemoteAsync(
            string host, string script, PSCredential? credential = null, int port = 5985,
            bool useSsl = false, CancellationToken cancellationToken = default, bool background = false)
        {
            Interlocked.Increment(ref Calls);
            return run(new Call(host, script, credential, port, useSsl, false), cancellationToken);
        }

        public Task<PSExecutionResult> RunRemoteStreamingAsync(
            string host, string script, Action<PSObject> onOutput, PSCredential? credential = null,
            int port = 5985, bool useSsl = false, CancellationToken cancellationToken = default,
            bool background = false)
        {
            Interlocked.Increment(ref Calls);
            return run(new Call(host, script, credential, port, useSsl, true), cancellationToken);
        }
    }

    private static PSCredential Credential(string user, SecureString? password = null)
    {
        password ??= new SecureString();
        if (password.Length == 0)
        {
            password.AppendChar('x');
        }
        return new PSCredential(user, password);
    }

    private static KerberosWrongPrincipalException Rejected(string host) =>
        new(host, new Exception("0x80090322"));

    [Fact]
    public async Task Retry_bypasses_cached_rejection_and_clean_sentinel_restores_route()
    {
        int attempt = 0;
        var inner = new ScriptedHost((call, _) => ++attempt == 1
            ? Task.FromException<PSExecutionResult>(Rejected(call.Host))
            : Task.FromResult(call.Script.Contains("VIVRE_WINRM_RECOVERY", StringComparison.Ordinal)
                ? Output("VIVRE_WINRM_RECOVERY_7F4B2C19") : Empty));
        var routing = new RoutingPowerShellHost(inner, new HostTransportCache());

        await Assert.ThrowsAsync<KerberosWrongPrincipalException>(() => routing.RunRemoteAsync("H", "one"));
        await Assert.ThrowsAsync<KerberosWrongPrincipalException>(() => routing.RunRemoteAsync("H", "suppressed"));
        WinRmRetryResult retry = await routing.RetryWinRmAsync("H");
        await routing.RunRemoteAsync("H", "after");

        Assert.Equal(WinRmRetryOutcome.Succeeded, retry.Outcome);
        Assert.Equal(3, inner.Calls);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Script_errors_or_missing_sentinel_never_clear_rejection(bool errors, bool missingSentinel)
    {
        int attempt = 0;
        var inner = new ScriptedHost((call, _) =>
        {
            if (++attempt == 1) return Task.FromException<PSExecutionResult>(Rejected(call.Host));
            return Task.FromResult(errors
                ? new PSExecutionResult([], ["script failed"], [], true)
                : Output(missingSentinel ? "wrong" : "VIVRE_WINRM_RECOVERY_7F4B2C19"));
        });
        var routing = new RoutingPowerShellHost(inner, new HostTransportCache());

        await Assert.ThrowsAsync<KerberosWrongPrincipalException>(() => routing.RunRemoteAsync("H", "one"));
        WinRmRetryResult retry = await routing.RetryWinRmAsync("H");
        await Assert.ThrowsAsync<KerberosWrongPrincipalException>(() => routing.RunRemoteAsync("H", "suppressed"));

        Assert.Equal(WinRmRetryOutcome.Failed, retry.Outcome);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Timeout_returns_bounded_and_late_success_cannot_clear_rejection()
    {
        var never = new TaskCompletionSource<PSExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempt = 0;
        var inner = new ScriptedHost((call, _) => ++attempt == 1
            ? Task.FromException<PSExecutionResult>(Rejected(call.Host)) : never.Task);
        var routing = new RoutingPowerShellHost(inner, new HostTransportCache(), TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAsync<KerberosWrongPrincipalException>(() => routing.RunRemoteAsync("H", "one"));
        WinRmRetryResult retry = await routing.RetryWinRmAsync("H");
        never.SetResult(Output("VIVRE_WINRM_RECOVERY_7F4B2C19"));
        await Assert.ThrowsAsync<KerberosWrongPrincipalException>(() => routing.RunRemoteAsync("H", "suppressed"));

        Assert.Equal(WinRmRetryOutcome.TimedOut, retry.Outcome);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Cancellation_returns_cancelled_and_does_not_clear_rejection()
    {
        int attempt = 0;
        var inner = new ScriptedHost((call, _) => ++attempt == 1
            ? Task.FromException<PSExecutionResult>(Rejected(call.Host))
            : new TaskCompletionSource<PSExecutionResult>().Task);
        var routing = new RoutingPowerShellHost(inner, new HostTransportCache(), TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<KerberosWrongPrincipalException>(() => routing.RunRemoteAsync("H", "one"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        WinRmRetryResult retry = await routing.RetryWinRmAsync("H", cancellationToken: cts.Token);
        await Assert.ThrowsAsync<KerberosWrongPrincipalException>(() => routing.RunRemoteAsync("H", "suppressed"));

        Assert.Equal(WinRmRetryOutcome.Cancelled, retry.Outcome);
        Assert.Equal(1, inner.Calls); // pre-cancelled: no connection is dispatched
    }

    [Fact]
    public async Task Superseded_retry_cannot_report_success_or_overwrite_newer_rejection()
    {
        var first = new TaskCompletionSource<PSExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int call = 0;
        var inner = new ScriptedHost((c, _) =>
        {
            if (++call != 1) return Task.FromException<PSExecutionResult>(Rejected(c.Host));
            entered.SetResult();
            return first.Task;
        });
        var routing = new RoutingPowerShellHost(inner, new HostTransportCache());

        Task<WinRmRetryResult> oldRetry = routing.RetryWinRmAsync("H");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        WinRmRetryResult newerRetry = await routing.RetryWinRmAsync("H");
        first.SetResult(Output("VIVRE_WINRM_RECOVERY_7F4B2C19"));
        WinRmRetryResult olderResult = await oldRetry;
        await Assert.ThrowsAsync<KerberosWrongPrincipalException>(() => routing.RunRemoteAsync("H", "suppressed"));

        Assert.Equal(WinRmRetryOutcome.Failed, newerRetry.Outcome);
        Assert.Equal(WinRmRetryOutcome.Failed, olderResult.Outcome);
        Assert.Contains("newer", olderResult.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Same_username_new_password_identity_gets_fresh_attempt()
    {
        PSCredential oldCredential = Credential("DOMAIN\\user");
        PSCredential newCredential = Credential("DOMAIN\\user");
        var inner = new ScriptedHost((call, _) => ReferenceEquals(call.Credential?.Password, oldCredential.Password)
            ? Task.FromException<PSExecutionResult>(Rejected(call.Host)) : Task.FromResult(Empty));
        var routing = new RoutingPowerShellHost(inner, new HostTransportCache());

        await Assert.ThrowsAsync<KerberosWrongPrincipalException>(() => routing.RunRemoteAsync("H", "one", oldCredential));
        await Assert.ThrowsAsync<KerberosWrongPrincipalException>(() =>
            routing.RunRemoteAsync("H", "same password wrapper", new PSCredential("DOMAIN\\user", oldCredential.Password)));
        await routing.RunRemoteAsync("H", "new password", newCredential);

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Host_port_ssl_username_and_password_identity_are_isolated()
    {
        SecureString shared = Credential("seed").Password;
        PSCredential rejected = Credential("user-a", shared);
        var inner = new ScriptedHost((call, _) => call.Host == "H" && call.Port == 5985 && !call.UseSsl && call.Credential?.UserName == "user-a"
            ? Task.FromException<PSExecutionResult>(Rejected(call.Host)) : Task.FromResult(Empty));
        var routing = new RoutingPowerShellHost(inner, new HostTransportCache());

        await Assert.ThrowsAsync<KerberosWrongPrincipalException>(() => routing.RunRemoteAsync("H", "x", rejected));
        await routing.RunRemoteAsync("OTHER", "x", rejected);
        await routing.RunRemoteAsync("H", "x", rejected, 5986, true);
        await routing.RunRemoteAsync("H", "x", Credential("user-b", shared));

        Assert.Equal(4, inner.Calls);
    }

    [Fact]
    public async Task Older_rejection_cannot_overwrite_newer_success()
    {
        var old = new TaskCompletionSource<PSExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int call = 0;
        var inner = new ScriptedHost((c, _) => ++call == 1
            ? old.Task
            : Task.FromResult(c.Script.Contains("VIVRE_WINRM_RECOVERY", StringComparison.Ordinal)
                ? Output("VIVRE_WINRM_RECOVERY_7F4B2C19") : Empty));
        var routing = new RoutingPowerShellHost(inner, new HostTransportCache());

        Task oldRun = routing.RunRemoteAsync("H", "old");
        WinRmRetryResult retry = await routing.RetryWinRmAsync("H");
        old.SetException(Rejected("H"));
        await Assert.ThrowsAsync<KerberosWrongPrincipalException>(() => oldRun);
        await routing.RunRemoteAsync("H", "after");

        Assert.Equal(WinRmRetryOutcome.Succeeded, retry.Outcome);
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task Older_success_cannot_overwrite_newer_rejection()
    {
        var old = new TaskCompletionSource<PSExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int call = 0;
        var inner = new ScriptedHost((c, _) => ++call == 1
            ? old.Task : Task.FromException<PSExecutionResult>(Rejected(c.Host)));
        var routing = new RoutingPowerShellHost(inner, new HostTransportCache());

        Task oldRun = routing.RunRemoteAsync("H", "old");
        WinRmRetryResult retry = await routing.RetryWinRmAsync("H");
        old.SetResult(Empty);
        await oldRun;
        await Assert.ThrowsAsync<KerberosWrongPrincipalException>(() => routing.RunRemoteAsync("H", "suppressed"));

        Assert.Equal(WinRmRetryOutcome.Failed, retry.Outcome);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Streaming_uses_the_same_connection_aware_rejection_route()
    {
        var inner = new ScriptedHost((call, _) => call.Port == 5985
            ? Task.FromException<PSExecutionResult>(Rejected(call.Host)) : Task.FromResult(Empty));
        var routing = new RoutingPowerShellHost(inner, new HostTransportCache());

        await Assert.ThrowsAsync<KerberosWrongPrincipalException>(() => routing.RunRemoteAsync("H", "x"));
        await Assert.ThrowsAsync<KerberosWrongPrincipalException>(() =>
            routing.RunRemoteStreamingAsync("H", "x", _ => { }));
        await routing.RunRemoteStreamingAsync("H", "x", _ => { }, port: 5986, useSsl: true);

        Assert.Equal(2, inner.Calls);
    }
    [Fact]
    public async Task Normal_attempt_started_during_retry_cannot_overwrite_verified_success()
    {
        var retry = new TaskCompletionSource<PSExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var normal = new TaskCompletionSource<PSExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int call = 0;
        var inner = new ScriptedHost((_, _) =>
        {
            int current = ++call;
            if (current == 1)
            {
                entered.SetResult();
                return retry.Task;
            }
            return current == 2 ? normal.Task : Task.FromResult(Empty);
        });
        var routing = new RoutingPowerShellHost(inner, new HostTransportCache());

        Task<WinRmRetryResult> retryTask = routing.RetryWinRmAsync("H");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task normalTask = routing.RunRemoteAsync("H", "ordinary");
        retry.SetResult(Output("VIVRE_WINRM_RECOVERY_7F4B2C19"));
        Assert.Equal(WinRmRetryOutcome.Succeeded, (await retryTask).Outcome);
        normal.SetException(Rejected("H"));
        await Assert.ThrowsAsync<KerberosWrongPrincipalException>(() => normalTask);
        await routing.RunRemoteAsync("H", "after");

        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task In_flight_cancellation_returns_bounded_and_late_success_does_not_clear_rejection()
    {
        var pending = new TaskCompletionSource<PSExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int call = 0;
        var inner = new ScriptedHost((c, _) =>
        {
            if (++call == 1) return Task.FromException<PSExecutionResult>(Rejected(c.Host));
            entered.SetResult();
            return pending.Task;
        });
        var routing = new RoutingPowerShellHost(inner, new HostTransportCache(), TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<KerberosWrongPrincipalException>(() => routing.RunRemoteAsync("H", "initial"));
        using var cts = new CancellationTokenSource();

        Task<WinRmRetryResult> retryTask = routing.RetryWinRmAsync("H", cancellationToken: cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        WinRmRetryResult result = await retryTask;
        pending.SetResult(Output("VIVRE_WINRM_RECOVERY_7F4B2C19"));
        await Assert.ThrowsAsync<KerberosWrongPrincipalException>(() => routing.RunRemoteAsync("H", "suppressed"));

        Assert.Equal(WinRmRetryOutcome.Cancelled, result.Outcome);
        Assert.Equal(2, inner.Calls);
    }

}
