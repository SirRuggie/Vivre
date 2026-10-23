namespace Vivre.Core.Updates;

/// <summary>
/// A shared, adjustable limit for CU file work across tabs and lane instances in this process.
/// Only preparation/copy/verification uses this limit; remote installation releases its slot.
/// </summary>
public static class CuPackageIo
{
    public const int DefaultConcurrency = 2;
    public const int MaximumConcurrency = 200;
    private static readonly object Sync = new();
    private static int _limit = DefaultConcurrency;
    private static int _active;
    private static TaskCompletionSource _changed = NewSignal();

    public static int Limit { get { lock (Sync) return _limit; } }
    public static int ClampConcurrency(int value) => Math.Clamp(value, 1, MaximumConcurrency);

    /// <summary>Adjust admission without replacing the gate or interrupting existing holders.
    /// A decrease takes effect as active file jobs finish; an increase wakes current waiters.</summary>
    public static void ConfigureLimit(int value)
    {
        lock (Sync)
        {
            int limit = ClampConcurrency(value);
            if (_limit == limit) return;
            _limit = limit;
            SignalChanged();
        }
    }

    internal static async Task<T> RunAsync<T>(Func<T> work, CancellationToken token)
    {
        await AcquireAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            // Even an immediately available slot must never run file I/O on the caller's dispatcher.
            return await Task.Run(work, token).ConfigureAwait(false);
        }
        finally
        {
            lock (Sync)
            {
                _active--;
                SignalChanged();
            }
        }
    }

    private static async Task AcquireAsync(CancellationToken token)
    {
        while (true)
        {
            Task changed;
            lock (Sync)
            {
                token.ThrowIfCancellationRequested();
                if (_active < _limit) { _active++; return; }
                changed = _changed.Task;
            }
            // A canceled waiter owns no slot and does not cancel the shared signal.
            await changed.WaitAsync(token).ConfigureAwait(false);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Caller holds Sync; asynchronous continuations cannot run inside this critical section.
    private static void SignalChanged()
    {
        TaskCompletionSource previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }
}
