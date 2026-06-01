using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management.Automation;
using Vivre.Core.PowerShell;
using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// PatchService serializes CBS/DISM operations (install / uninstall / Installed-scope scan) per
/// host — two must never hit the same box's servicing stack at once, including across tabs.
/// </summary>
public class PatchServiceTests
{
    [Fact]
    public async Task Installed_scan_on_a_host_already_in_flight_is_skipped_without_touching_it()
    {
        var gate = new TaskCompletionSource<bool>();
        var entered = new ConcurrentBag<string>();
        var host = new BlockingHost(gate, entered);
        var service = new PatchService(host);
        var installed = new PatchOptions { Scope = UpdateScope.Installed };

        // First Installed-scope scan claims HOSTA and blocks inside the host.
        Task<HostPatchStatus> first = service.ScanAsync("HOSTA", installed, credential: null);
        await WaitUntil(() => entered.Contains("HOSTA"));

        // A second Installed-scope scan on the SAME host while the first is in flight is skipped —
        // it must NOT re-enter the host.
        HostPatchStatus skip = await service.ScanAsync("HOSTA", installed, credential: null);

        Assert.Equal(PatchPhase.Idle, skip.Phase);
        Assert.Contains("already in progress", skip.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(entered, h => h == "HOSTA");

        gate.SetResult(true);
        await first;
    }

    [Fact]
    public async Task Installed_scan_on_a_different_host_is_allowed_concurrently()
    {
        var gate = new TaskCompletionSource<bool>();
        var entered = new ConcurrentBag<string>();
        var host = new BlockingHost(gate, entered);
        var service = new PatchService(host);
        var installed = new PatchOptions { Scope = UpdateScope.Installed };

        Task<HostPatchStatus> a = service.ScanAsync("HOSTA", installed, credential: null);
        await WaitUntil(() => entered.Contains("HOSTA"));

        // Different host → not serialized; it enters its own host call.
        Task<HostPatchStatus> b = service.ScanAsync("HOSTB", installed, credential: null);
        await WaitUntil(() => entered.Contains("HOSTB"));

        Assert.Contains("HOSTB", entered);

        gate.SetResult(true);
        await Task.WhenAll(a, b);
    }

    [Fact]
    public async Task Applicable_scan_is_never_serialized()
    {
        var gate = new TaskCompletionSource<bool>();
        var entered = new ConcurrentBag<string>();
        var host = new BlockingHost(gate, entered);
        var service = new PatchService(host);
        var applicable = new PatchOptions { Scope = UpdateScope.Applicable };

        Task<HostPatchStatus> first = service.ScanAsync("HOSTA", applicable, credential: null);
        await WaitUntil(() => entered.Count(h => h == "HOSTA") == 1);

        // A read-only Applicable scan is not a CBS/DISM op — a concurrent one enters the host too.
        Task<HostPatchStatus> second = service.ScanAsync("HOSTA", applicable, credential: null);
        await WaitUntil(() => entered.Count(h => h == "HOSTA") == 2);

        Assert.Equal(2, entered.Count(h => h == "HOSTA"));

        gate.SetResult(true);
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task Host_claim_is_case_insensitive_so_the_same_box_in_different_casing_is_serialized()
    {
        // Windows host names are case-insensitive, so HOSTA and hosta are the same physical box — the
        // CBS/DISM serialization guard must treat them as one (a comparer regression would let two
        // installs collide on the same machine).
        var gate = new TaskCompletionSource<bool>();
        var entered = new ConcurrentBag<string>();
        var host = new BlockingHost(gate, entered);
        var service = new PatchService(host);
        var installed = new PatchOptions { Scope = UpdateScope.Installed };

        Task<HostPatchStatus> first = service.ScanAsync("HOSTA", installed, credential: null);
        await WaitUntil(() => entered.Contains("HOSTA"));

        HostPatchStatus skip = await service.ScanAsync("hosta", installed, credential: null);

        Assert.Equal(PatchPhase.Idle, skip.Phase);
        Assert.Contains("already in progress", skip.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hosta", entered); // the lower-cased call was skipped, never re-entered the host

        gate.SetResult(true);
        await first;
    }

    [Fact]
    public async Task A_faulted_installed_scan_releases_the_host_so_it_can_be_claimed_again()
    {
        // The guard's whole value is that a crashed op must NOT wedge a host as permanently
        // "in progress" — Release runs in the finally even when the in-flight op throws.
        var host = new FaultingHost(() => new InvalidOperationException("boom"), failTimes: 1);
        var service = new PatchService(host);
        var installed = new PatchOptions { Scope = UpdateScope.Installed };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ScanAsync("HOSTA", installed, credential: null));

        // Released → a later op on the SAME host re-enters; it is NOT skipped as already-in-progress.
        HostPatchStatus second = await service.ScanAsync("HOSTA", installed, credential: null);

        Assert.NotEqual(PatchPhase.Idle, second.Phase);
        Assert.DoesNotContain("already in progress", second.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(host.Entered >= 2);
    }

    [Fact]
    public async Task A_faulted_install_releases_the_host_so_a_later_install_can_claim_it()
    {
        // The test environment has no bundled agent EXE, so an install faults early and InstallAsync
        // throws — the point is that PatchService's finally still RELEASES the per-host claim on the
        // throw path. (failTimes:0 → the host itself never faults; it only serves the cleanup call.)
        var host = new FaultingHost(() => new InvalidOperationException("unused"), failTimes: 0);
        var service = new PatchService(host);
        var options = new PatchOptions();
        var progress = new Progress<HostPatchStatus>();

        await Assert.ThrowsAnyAsync<Exception>(
            () => service.InstallAsync("HOSTA", options, credential: null, progress));

        // If the claim had NOT been released, this second call would be skipped (return Idle) without
        // re-entering. That it faults again proves it re-claimed the host — i.e. Release ran.
        await Assert.ThrowsAnyAsync<Exception>(
            () => service.InstallAsync("HOSTA", options, credential: null, progress));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException("Condition not met within 5s.");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>A host whose remote calls record the host name then block on a shared gate, so a
    /// scan can be held "in flight" while the test issues a concurrent one.</summary>
    private sealed class BlockingHost(TaskCompletionSource<bool> gate, ConcurrentBag<string> entered) : IPowerShellHost
    {
        private static readonly PSExecutionResult Empty = new([], [], [], HadErrors: false);

        public Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken cancellationToken = default) =>
            Task.FromResult(Empty);

        public async Task<PSExecutionResult> RunRemoteAsync(
            string host, string script, PSCredential? credential = null, int port = 5985, bool useSsl = false,
            CancellationToken cancellationToken = default)
        {
            entered.Add(host);
            await gate.Task;
            return Empty;
        }

        public async Task<PSExecutionResult> RunRemoteStreamingAsync(
            string host, string script, Action<PSObject> onOutput, PSCredential? credential = null, int port = 5985,
            bool useSsl = false, CancellationToken cancellationToken = default)
        {
            entered.Add(host);
            await gate.Task;
            return Empty;
        }
    }

    /// <summary>A host whose remote calls throw the given exception the first <c>failTimes</c> times,
    /// then succeed — so a test can fault an in-flight op and then prove the host is claimable again.</summary>
    private sealed class FaultingHost(Func<Exception> fault, int failTimes) : IPowerShellHost
    {
        private static readonly PSExecutionResult Empty = new([], [], [], HadErrors: false);
        private int _failsRemaining = failTimes;
        private int _entered;

        public int Entered => _entered;

        private PSExecutionResult Handle()
        {
            Interlocked.Increment(ref _entered);
            if (Interlocked.Decrement(ref _failsRemaining) >= 0)
            {
                throw fault();
            }

            return Empty;
        }

        // Local path isn't exercised (tests use a remote host name), but a leftover cleanup call must
        // not fault — keep it benign.
        public Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken cancellationToken = default) =>
            Task.FromResult(Empty);

        public Task<PSExecutionResult> RunRemoteAsync(
            string host, string script, PSCredential? credential = null, int port = 5985, bool useSsl = false,
            CancellationToken cancellationToken = default) => Task.FromResult(Handle());

        public Task<PSExecutionResult> RunRemoteStreamingAsync(
            string host, string script, Action<PSObject> onOutput, PSCredential? credential = null, int port = 5985,
            bool useSsl = false, CancellationToken cancellationToken = default) => Task.FromResult(Handle());
    }
}
