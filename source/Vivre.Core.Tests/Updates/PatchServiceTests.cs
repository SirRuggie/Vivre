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
}
