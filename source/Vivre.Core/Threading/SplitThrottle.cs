namespace Vivre.Core.Threading;

/// <summary>
/// A shared concurrency budget split into two pools: an <em>active</em> pool used by registered
/// sweeps (vitals, health, update scans, software checks) and a <em>reserved</em> pool guaranteed
/// for passive fills (custom-column sweeps and user-fired ConfigMgr client actions).
///
/// <para>
/// <b>Why this exists — FIFO starvation fix.</b>  Before this class, every operation drew from a
/// single <see cref="SemaphoreSlim"/>.  On a 319-machine paste, Task.WhenAll launches all 319
/// vitals-row tasks eagerly; they all enqueue on the semaphore's FIFO queue first.  The 319
/// passive custom-column waiters land behind them, so the first column permit only arrives after
/// roughly (319 − 32) ÷ 1 completions ≈ 2 minutes of starvation even though the column fill was
/// launched simultaneously.
/// </para>
///
/// <para>
/// <b>Invariants.</b>
/// <list type="bullet">
///   <item>
///     <description>
///       Total concurrency cap = <c>total</c> (unchanged from the single-semaphore baseline).
///       Active pool = <c>total − reserved</c>; reserved pool = <c>reserved</c>.
///       (total − reserved) + reserved = total. ✓
///     </description>
///   </item>
///   <item>
///     <description>
///       Active sweeps (<see cref="Active"/>) may ONLY borrow from the active pool — they never
///       touch the reserved pool, so the reserved slots are always available to passive fills even
///       when the active pool is saturated.
///     </description>
///   </item>
///   <item>
///     <description>
///       Passive fills (<see cref="AcquirePassiveAsync"/>) may borrow from EITHER pool — the
///       reserved pool (fast path / guarantee) OR the active pool when it has room.  On an
///       otherwise-idle system a passive fill can still run up to <c>total</c> wide by using all
///       active slots.
///     </description>
///   </item>
/// </list>
/// </para>
/// </summary>
public sealed class SplitThrottle : IDisposable
{
    private readonly SemaphoreSlim _reserved;
    private bool _disposed;

    /// <summary>
    /// Initialises the throttle.
    /// </summary>
    /// <param name="total">Total concurrency cap (must be ≥ 2).</param>
    /// <param name="reserved">Slots permanently reserved for passive fills
    ///   (must satisfy 0 &lt; reserved &lt; total).</param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///   Thrown when the constraints are violated.
    /// </exception>
    public SplitThrottle(int total, int reserved)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(reserved, 0, nameof(reserved));
        if (reserved >= total)
            throw new ArgumentOutOfRangeException(nameof(reserved),
                $"reserved ({reserved}) must be < total ({total}).");

        Active    = new SemaphoreSlim(total - reserved, total - reserved);
        _reserved = new SemaphoreSlim(reserved, reserved);
    }

    /// <summary>
    /// The active pool — sized <c>total − reserved</c>.  All active sweep call sites wait on this
    /// directly (same <see cref="SemaphoreSlim"/> shape as the old single semaphore, minimal
    /// call-site ripple).
    /// </summary>
    public SemaphoreSlim Active { get; }

    /// <summary>
    /// Acquires a permit for a passive fill, drawing from the reserved pool first or, if that is
    /// saturated, racing for an active-pool permit instead (borrow path).
    ///
    /// <para>
    /// Algorithm:
    /// <list type="number">
    ///   <item>
    ///     <description>
    ///       <b>Fast path:</b> try a synchronous zero-timeout acquire on the reserved pool.
    ///       If it succeeds, return a releaser immediately — no contention cost.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       <b>Race path:</b> start async waits on both pools and await whichever completes
    ///       first.  The LOSER task is handled via a fire-and-forget continuation: if it later
    ///       acquires a permit, that permit is released immediately back to its pool; if it faults
    ///       or is cancelled, nothing is released (there is nothing to release).  This prevents
    ///       any permit from leaking on any completion order.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       If <paramref name="ct"/> fires before any acquisition, an
    ///       <see cref="OperationCanceledException"/> is thrown and both outstanding waits are
    ///       handled by the same loser-continuation rule.
    ///     </description>
    ///   </item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// An <see cref="IDisposable"/> whose <c>Dispose</c> releases exactly the permit that was
    /// acquired (to the correct pool, once only).
    /// </returns>
    /// <exception cref="OperationCanceledException">
    ///   <paramref name="ct"/> was cancelled before a permit was acquired.
    /// </exception>
    public async Task<IDisposable> AcquirePassiveAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Fast path: reserved pool has a free slot right now — grab it without allocating tasks.
        if (_reserved.Wait(0))
        {
            return new Releaser(_reserved);
        }

        // Race path: both pools are contended (or reserved is full); race them.
        Task reservedTask = _reserved.WaitAsync(ct);
        Task activeTask   = Active.WaitAsync(ct);

        Task winner = await Task.WhenAny(reservedTask, activeTask).ConfigureAwait(false);

        // The winner may be faulted (cancelled) — propagate but ensure the loser is cleaned up.
        Task loser = ReferenceEquals(winner, reservedTask) ? activeTask : reservedTask;
        SemaphoreSlim winnerPool = ReferenceEquals(winner, reservedTask) ? _reserved : Active;
        SemaphoreSlim loserPool  = ReferenceEquals(winner, reservedTask) ? Active    : _reserved;

        // Attach the loser handler BEFORE awaiting the winner so we never miss a late acquisition.
        AttachLoserContinuation(loser, loserPool);

        // Now surface any cancellation / fault from the winner.
        await winner.ConfigureAwait(false);

        return new Releaser(winnerPool);
    }

    /// <summary>
    /// Attaches a fire-and-forget continuation to <paramref name="loserTask"/> that releases
    /// the acquired permit back to <paramref name="pool"/> if the task completes successfully
    /// (it managed to acquire a slot after losing the race), and observes (but swallows) any
    /// cancellation or fault.
    /// </summary>
    private static void AttachLoserContinuation(Task loserTask, SemaphoreSlim pool)
    {
        // ContinueWith avoids re-scheduling on the thread pool for the cancellation/fault paths
        // and avoids an unobserved-task-exception warning.
        _ = loserTask.ContinueWith(
            t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    pool.Release();
                }
                // Cancelled or faulted: no permit was acquired; nothing to release.
                // Observe the exception so the runtime does not raise UnobservedTaskException.
                _ = t.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Active.Dispose();
        _reserved.Dispose();
    }

    // -----------------------------------------------------------------------
    // Inner releaser
    // -----------------------------------------------------------------------

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _pool;

        internal Releaser(SemaphoreSlim pool) => _pool = pool;

        public void Dispose()
        {
            SemaphoreSlim? pool = Interlocked.Exchange(ref _pool, null);
            pool?.Release();
        }
    }
}
