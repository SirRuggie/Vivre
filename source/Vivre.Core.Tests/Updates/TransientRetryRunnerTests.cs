using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vivre.Core.Models;
using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// The silent transient-retry loop: a transient WU reach hiccup re-dispatches the WHOLE operation up
/// to the budget; success / a terminal failure returns at once; an exhausted transient resolves to an
/// honest "couldn't reach Windows Update" status that must NEVER read as up-to-date / zero-applicable.
/// </summary>
public class TransientRetryRunnerTests
{
    private static readonly HostPatchStatus Transient = HostPatchStatus.Failed("Exception from HRESULT: 0x80072EE2");
    private static readonly HostPatchStatus Terminal = HostPatchStatus.Failed("Exception from HRESULT: 0x80240022"); // install failure
    private static readonly HostPatchStatus Success = new(PatchPhase.Done, "Installed 3 updates", InstalledCount: 3);

    private sealed class Counter { public int Value; }

    // An attempt that returns the queued statuses in order, clamping to the last once it runs out.
    private static Func<CancellationToken, Task<HostPatchStatus>> Sequence(IReadOnlyList<HostPatchStatus> results, Counter calls) =>
        _ =>
        {
            int i = calls.Value++;
            return Task.FromResult(results[Math.Min(i, results.Count - 1)]);
        };

    private static Func<int, CancellationToken, Task> NoDelay(Action? onCall = null) =>
        (_, _) => { onCall?.Invoke(); return Task.CompletedTask; };

    private static HostPatchStatus Exhausted(string lastMessage) =>
        HostPatchStatus.Unreachable($"Couldn't reach Windows Update ({TransientWuaError.FirstTransientToken(lastMessage)}) after 4 tries — try again.");

    [Fact]
    public async Task Success_on_first_try_returns_it_with_no_retry()
    {
        var calls = new Counter();
        int delays = 0, retries = 0;

        HostPatchStatus result = await TransientRetryRunner.RunAsync(
            Sequence([Success], calls), maxRetries: 3,
            NoDelay(() => delays++), _ => retries++, Exhausted, CancellationToken.None);

        Assert.Equal(Success, result);
        Assert.Equal(1, calls.Value);
        Assert.Equal(0, delays);
        Assert.Equal(0, retries);
    }

    [Fact]
    public async Task Terminal_failure_surfaces_immediately_with_no_retry()
    {
        var calls = new Counter();
        int delays = 0;

        HostPatchStatus result = await TransientRetryRunner.RunAsync(
            Sequence([Terminal], calls), maxRetries: 3,
            NoDelay(() => delays++), null, Exhausted, CancellationToken.None);

        Assert.Equal(Terminal, result);
        Assert.Equal(PatchPhase.Error, result.Phase);
        Assert.Equal(1, calls.Value);   // exactly one attempt — a real failure is never retried
        Assert.Equal(0, delays);
    }

    [Fact]
    public async Task Transient_then_success_retries_silently_and_surfaces_no_error()
    {
        var calls = new Counter();
        int delays = 0, retries = 0;

        HostPatchStatus result = await TransientRetryRunner.RunAsync(
            Sequence([Transient, Success], calls), maxRetries: 3,
            NoDelay(() => delays++), _ => retries++, Exhausted, CancellationToken.None);

        Assert.Equal(Success, result);                          // final outcome is the success...
        Assert.NotEqual(PatchPhase.Error, result.Phase);        // ...never an error ever surfaced
        Assert.NotEqual(PatchPhase.Unreachable, result.Phase);
        Assert.Equal(2, calls.Value);                           // one silent retry
        Assert.Equal(1, delays);
        Assert.Equal(1, retries);
    }

    [Fact]
    public async Task All_transient_exhausts_to_an_honest_unreachable_status()
    {
        var calls = new Counter();
        int retries = 0;

        HostPatchStatus result = await TransientRetryRunner.RunAsync(
            Sequence([Transient], calls), maxRetries: 3,        // always transient
            NoDelay(), _ => retries++, Exhausted, CancellationToken.None);

        Assert.Equal(PatchPhase.Unreachable, result.Phase);
        Assert.Equal(4, calls.Value);                           // 1 initial + 3 retries
        Assert.Equal(3, retries);
    }

    [Fact]
    public void An_exhausted_transient_NEVER_reads_as_up_to_date_or_zero_applicable()
    {
        HostPatchStatus result = HostPatchStatus.Unreachable(
            "Couldn't reach Windows Update (0x80072EE2) after 4 tries — likely a transient network issue, try again.");

        // The exact "up to date" rule from ApplyStatus is Available + 0 applicable. Unreachable is neither,
        // so a box that couldn't be scanned can never render as a clean / patched box.
        bool upToDate = result.Phase == PatchPhase.Available && result.AvailableCount == 0;
        Assert.False(upToDate);
        Assert.NotEqual(PatchPhase.Done, result.Phase);
        Assert.NotEqual(PatchPhase.Available, result.Phase);
        Assert.DoesNotContain("up to date", result.Message, StringComparison.OrdinalIgnoreCase);

        // And the glanceable display-state is the red Error — never green Done/Available.
        var row = new Computer("HOST") { UpdatePhase = result.Phase.ToString() };
        Assert.Equal(PatchState.Error, row.PatchState);
    }

    [Fact]
    public async Task A_cancel_during_backoff_propagates_and_stops_the_loop()
    {
        var calls = new Counter();
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TransientRetryRunner.RunAsync(
            Sequence([Transient], calls), maxRetries: 3,
            delay: (_, ct) => { cts.Cancel(); return Task.Delay(Timeout.Infinite, ct); },
            onRetrying: null, Exhausted, cts.Token));
    }
}
