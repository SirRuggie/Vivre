using Vivre.Core.Updates;

namespace Vivre.Desktop.ViewModels;

/// <summary>
/// <see cref="IRebootGate"/> implementation that bounds the burst rate of reboot issuance across
/// a fleet wave. Wraps a caller-supplied <see cref="SemaphoreSlim"/> so the concurrency width is
/// set once at the VM level and shared across all per-box tasks in the same wave. An optional jitter
/// delay (applied while the semaphore is held) spreads DC/DNS/auth load when many boxes become
/// reboot-eligible at the same moment.
///
/// <para>The gate is acquired only around the reboot call itself and released the instant the
/// reboot is issued — it is never held through the offline watch, so it never serializes the
/// per-box verify step.</para>
/// </summary>
internal sealed class RebootTriggerGate : IRebootGate
{
    private readonly SemaphoreSlim _semaphore;
    private readonly int _jitterMs;

    /// <param name="semaphore">The concurrency limiter (shared across all per-box wave tasks).</param>
    /// <param name="jitterMs">Maximum random jitter in milliseconds added after acquiring the
    /// semaphore and before issuing the reboot. Pass 0 to disable.</param>
    public RebootTriggerGate(SemaphoreSlim semaphore, int jitterMs = 0)
    {
        _semaphore = semaphore ?? throw new ArgumentNullException(nameof(semaphore));
        _jitterMs = Math.Max(0, jitterMs);
    }

    /// <inheritdoc/>
    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (_jitterMs > 0)
        {
            int delay = Random.Shared.Next(_jitterMs + 1);
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Delay cancelled — release the semaphore before propagating so no slot leaks.
                _semaphore.Release();
                throw;
            }
        }

        return new Releaser(_semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
