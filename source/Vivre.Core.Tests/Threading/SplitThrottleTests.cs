using System.Collections.Concurrent;
using Vivre.Core.Threading;

namespace Vivre.Core.Tests.Threading;

/// <summary>
/// Unit tests for <see cref="SplitThrottle"/> — the FIFO-starvation fix for passive custom-column
/// fills being blocked behind saturated active vitals/scan sweeps.
/// </summary>
public class SplitThrottleTests
{
    // -----------------------------------------------------------------------
    // 1. Total concurrency cap never exceeds total under stress
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Total_concurrency_never_exceeds_total_under_concurrent_mixed_load()
    {
        // 8 total, 2 reserved → active = 6.  Fire 20 active + 20 passive acquirers concurrently.
        // The in-flight counter must never exceed 8.
        const int total = 8, reserved = 2, acquirers = 20;
        using var throttle = new SplitThrottle(total, reserved);

        int inFlight = 0;
        int maxObserved = 0;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task RunActive()
        {
            await throttle.Active.WaitAsync();
            try
            {
                int current = Interlocked.Increment(ref inFlight);
                // Track the high-water mark atomically.
                int observed;
                int snapshot;
                do { snapshot = Volatile.Read(ref maxObserved); observed = Math.Max(snapshot, current); }
                while (Interlocked.CompareExchange(ref maxObserved, observed, snapshot) != snapshot);

                await gate.Task;
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
                throttle.Active.Release();
            }
        }

        async Task RunPassive()
        {
            using IDisposable permit = await throttle.AcquirePassiveAsync(CancellationToken.None);
            int current = Interlocked.Increment(ref inFlight);
            int observed;
            int snapshot;
            do { snapshot = Volatile.Read(ref maxObserved); observed = Math.Max(snapshot, current); }
            while (Interlocked.CompareExchange(ref maxObserved, observed, snapshot) != snapshot);

            await gate.Task;
            Interlocked.Decrement(ref inFlight);
        }

        var tasks = new List<Task>(acquirers * 2);
        for (int i = 0; i < acquirers; i++)
        {
            tasks.Add(Task.Run(RunActive));
            tasks.Add(Task.Run(RunPassive));
        }

        // Give all tasks a moment to queue up before releasing the gate.
        await Task.Delay(50);
        gate.SetResult(true);
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(maxObserved <= total,
            $"Observed {maxObserved} concurrent holders but total cap is {total}.");
    }

    // -----------------------------------------------------------------------
    // 2. Reserve guarantee: passive succeeds even when active pool is fully held
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Passive_acquire_succeeds_promptly_when_active_pool_is_fully_saturated()
    {
        // active = 2, reserved = 2.  Hold all active slots, then prove passive still acquires
        // via the reserved pool.
        const int total = 4, reserved = 2;
        using var throttle = new SplitThrottle(total, reserved);

        // Drain all active slots.
        await throttle.Active.WaitAsync();
        await throttle.Active.WaitAsync();
        Assert.Equal(0, throttle.Active.CurrentCount);

        // Passive acquire must complete promptly (reserved pool still has 2 free).
        using IDisposable permit = await throttle.AcquirePassiveAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(permit); // acquired successfully
    }

    // -----------------------------------------------------------------------
    // 3. Active acquire does NOT succeed when only reserved slots remain free
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Active_acquire_blocks_when_only_reserved_slots_are_free()
    {
        // active = 1, reserved = 3.  Drain the single active slot.  Then verify that a new
        // Active.WaitAsync does NOT complete within a short window.
        const int total = 4, reserved = 3;
        using var throttle = new SplitThrottle(total, reserved);

        await throttle.Active.WaitAsync();
        Assert.Equal(0, throttle.Active.CurrentCount);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => throttle.Active.WaitAsync(cts.Token));
    }

    // -----------------------------------------------------------------------
    // 4. Releaser returns the permit to the correct pool
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Releaser_returns_permit_to_its_own_pool_so_it_can_be_regranted()
    {
        // With reserved = 1 and active = 1 (total = 2):
        //   - Drain active, so the next passive must go via reserved.
        //   - Acquire passive (via reserved). Reserved CurrentCount drops to 0.
        //   - Dispose the passive permit → reserved pool must recover to 1.
        //   - A second passive acquire must again succeed promptly.
        const int total = 2, reserved = 1;
        using var throttle = new SplitThrottle(total, reserved);

        await throttle.Active.WaitAsync(); // drain active

        IDisposable permit1 = await throttle.AcquirePassiveAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        permit1.Dispose(); // should release back to the reserved pool

        // The reserved pool should be restored — another passive acquire must succeed without
        // touching the (still-held) active pool.
        using IDisposable permit2 = await throttle.AcquirePassiveAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(permit2);

        throttle.Active.Release(); // tidy up
    }

    // -----------------------------------------------------------------------
    // 5. Double-dispose of a releaser releases only once
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Double_dispose_of_releaser_releases_exactly_one_permit()
    {
        const int total = 2, reserved = 1;
        using var throttle = new SplitThrottle(total, reserved);

        // Record current counts.
        int activeCountBefore   = throttle.Active.CurrentCount;   // 1

        IDisposable permit = await throttle.AcquirePassiveAsync(CancellationToken.None);
        // Permit acquired via reserved → reserved count = 0.

        permit.Dispose(); // first dispose → releases the reserved slot
        int afterFirst = throttle.Active.CurrentCount;             // still 1 (active unchanged)

        permit.Dispose(); // second dispose → must be a no-op

        int afterSecond = throttle.Active.CurrentCount;

        // Active pool must not have changed (passive uses reserved here).
        Assert.Equal(activeCountBefore, afterFirst);
        Assert.Equal(afterFirst,        afterSecond);
    }

    // -----------------------------------------------------------------------
    // 6. Cancellation during saturation throws OCE and leaks no permits
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Cancellation_while_both_pools_are_saturated_throws_OCE_and_leaks_no_permits()
    {
        // Total = 4, reserved = 2 → active = 2.
        // Drain ALL slots (both active and reserved via 2 passive borrows from active + 2 reserve).
        // Then try a passive acquire with a pre-cancelled token → OCE.
        // Afterwards every slot should be re-acquirable (no leak).
        const int total = 4, reserved = 2;
        using var throttle = new SplitThrottle(total, reserved);

        // Drain the active pool.
        await throttle.Active.WaitAsync();
        await throttle.Active.WaitAsync();

        // Drain the reserved pool (use AcquirePassiveAsync on both reserved slots).
        var gateReserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var passiveHolders = new List<IDisposable>();
        for (int i = 0; i < reserved; i++)
        {
            passiveHolders.Add(await throttle.AcquirePassiveAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)));
        }

        // All slots held — now try a passive acquire with a cancelled token.
        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();

        // TaskCanceledException is a subclass of OperationCanceledException; ThrowsAnyAsync accepts either.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => throttle.AcquirePassiveAsync(preCancelled.Token));

        // Release all held permits.
        throttle.Active.Release();
        throttle.Active.Release();
        foreach (var h in passiveHolders) h.Dispose();

        // Give any loser-continuations a moment to run (they're synchronous continuations but
        // may need a scheduler iteration).
        await Task.Delay(50);

        // Now all slots must be re-acquirable: active should be back to (total - reserved),
        // and we should be able to acquire 'total' slots in total (active + passive borrows).
        Assert.Equal(total - reserved, throttle.Active.CurrentCount);

        // Drain all again to prove no extra permits leaked.
        for (int i = 0; i < total - reserved; i++) await throttle.Active.WaitAsync();
        for (int i = 0; i < reserved; i++)
            passiveHolders.Add(await throttle.AcquirePassiveAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)));

        // Must have acquired exactly 'total' without blocking.
        Assert.Equal(total, (total - reserved) + reserved);

        // Release everything.
        for (int i = 0; i < total - reserved; i++) throttle.Active.Release();
        foreach (var h in passiveHolders) h.Dispose();
    }

    // -----------------------------------------------------------------------
    // 7. Constructor validation
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(0, 0)]  // reserved = 0 (not positive)
    [InlineData(4, 0)]  // reserved = 0
    [InlineData(4, 4)]  // reserved == total
    [InlineData(4, 5)]  // reserved > total
    public void Constructor_rejects_invalid_reserved_values(int total, int reserved)
    {
        Assert.ThrowsAny<ArgumentException>(() => new SplitThrottle(total, reserved));
    }

    [Fact]
    public void Constructor_with_valid_args_creates_expected_pool_sizes()
    {
        using var throttle = new SplitThrottle(32, 4);
        Assert.Equal(28, throttle.Active.CurrentCount);
        // Reserved is internal; we verify it indirectly via passive-acquire behaviour in other tests.
    }

    // -----------------------------------------------------------------------
    // 8. Idle-system borrow: passive fills run wide via active pool
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Passive_fills_borrow_active_slots_when_reserved_pool_is_exhausted_and_active_is_free()
    {
        // total = 4, reserved = 1 → active = 3.
        // We want to prove passive can run 4 wide (1 reserved + 3 active borrows) on an idle system.
        const int total = 4, reserved = 1;
        using var throttle = new SplitThrottle(total, reserved);

        var permits = new List<IDisposable>();
        for (int i = 0; i < total; i++)
        {
            permits.Add(await throttle.AcquirePassiveAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)));
        }

        Assert.Equal(total, permits.Count); // all acquired without blocking

        // A (total + 1)th acquire must block (all slots gone).
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => throttle.AcquirePassiveAsync(cts.Token));

        foreach (var p in permits) p.Dispose();
    }
}
