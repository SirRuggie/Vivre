using System.Diagnostics;

namespace Vivre.Core.IO;

/// <summary>
/// Executes accepted save snapshots one at a time, in FIFO order, on the thread pool.
/// Callers should capture immutable state in each write action before enqueueing it.
/// </summary>
public sealed class OrderedSaveQueue
{
    private readonly object _gate = new();
    private readonly Queue<(long Sequence, Action Write)> _pending = new();
    private readonly Dictionary<long, FlushTarget> _flushTargets = new();
    private readonly Action<Exception>? _onWriteFailed;
    private long _acceptedSequence;
    private long _completedSequence;
    private Exception? _latestFailure;
    private Exception? _latestCallbackFailure;
    private bool _workerScheduled;

    internal Exception? LatestCallbackFailure => Volatile.Read(ref _latestCallbackFailure);

    public OrderedSaveQueue(Action<Exception>? onWriteFailed = null)
    {
        _onWriteFailed = onWriteFailed;
    }

    /// <summary>Accepts a fully captured save and returns its monotonic queue sequence.</summary>
    public long Enqueue(Action write)
    {
        ArgumentNullException.ThrowIfNull(write);

        lock (_gate)
        {
            long sequence = checked(++_acceptedSequence);
            _pending.Enqueue((sequence, write));
            if (!_workerScheduled)
            {
                _workerScheduled = true;
                ThreadPool.QueueUserWorkItem(static state => ((OrderedSaveQueue)state!).RunWorker(), this);
            }

            return sequence;
        }
    }

    /// <summary>
    /// Waits synchronously for the last save accepted when this call began. Saves accepted later do
    /// not change this result. A successful newer captured save supersedes an older failure.
    /// </summary>
    public SaveFlushResult Flush(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        lock (_gate)
        {
            long targetSequence = _acceptedSequence;
            if (targetSequence == 0)
            {
                return SaveFlushResult.Success;
            }

            if (_completedSequence >= targetSequence)
            {
                return ResultFor(_latestFailure);
            }

            FlushTarget target = RegisterFlushTarget(targetSequence);
            long started = Stopwatch.GetTimestamp();
            try
            {
                while (!target.Completed)
                {
                    TimeSpan remaining = Remaining(timeout, started);
                    if (remaining == TimeSpan.Zero || !Monitor.Wait(_gate, remaining))
                    {
                        return new SaveFlushResult(SaveFlushStatus.TimedOut, _latestFailure);
                    }
                }

                return ResultFor(target.Failure);
            }
            finally
            {
                UnregisterFlushTarget(targetSequence, target);
            }
        }
    }

    private void RunWorker()
    {
        while (true)
        {
            (long Sequence, Action Write) item;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    _workerScheduled = false;
                    return;
                }

                item = _pending.Dequeue();
            }

            Exception? failure = null;
            try
            {
                item.Write();
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            lock (_gate)
            {
                _completedSequence = item.Sequence;
                _latestFailure = failure;
                if (_flushTargets.TryGetValue(item.Sequence, out FlushTarget? target))
                {
                    target.Failure = failure;
                    target.Completed = true;
                }

                Monitor.PulseAll(_gate);
            }

            if (failure is not null && _onWriteFailed is not null)
            {
                QueueFailureCallback(failure);
            }
        }
    }

    private void QueueFailureCallback(Exception failure)
    {
        ThreadPool.QueueUserWorkItem(static state =>
        {
            var (owner, callback, error) = ((OrderedSaveQueue, Action<Exception>, Exception))state!;
            try
            {
                callback(error);
            }
            catch (Exception callbackFailure)
            {
                Volatile.Write(ref owner._latestCallbackFailure, callbackFailure);
            }
        }, (this, _onWriteFailed!, failure));
    }

    private FlushTarget RegisterFlushTarget(long sequence)
    {
        if (!_flushTargets.TryGetValue(sequence, out FlushTarget? target))
        {
            target = new FlushTarget();
            _flushTargets.Add(sequence, target);
        }

        target.WaiterCount++;
        return target;
    }

    private void UnregisterFlushTarget(long sequence, FlushTarget target)
    {
        target.WaiterCount--;
        if (target.WaiterCount == 0)
        {
            _flushTargets.Remove(sequence);
        }
    }

    private static SaveFlushResult ResultFor(Exception? failure) => failure is null
        ? SaveFlushResult.Success
        : new SaveFlushResult(SaveFlushStatus.Failed, failure);

    private static TimeSpan Remaining(TimeSpan timeout, long started)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            return Timeout.InfiniteTimeSpan;
        }

        TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(started);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private sealed class FlushTarget
    {
        public int WaiterCount { get; set; }
        public bool Completed { get; set; }
        public Exception? Failure { get; set; }
    }
}

public enum SaveFlushStatus
{
    Success,
    Failed,
    TimedOut,
}

public readonly record struct SaveFlushResult(SaveFlushStatus Status, Exception? Failure)
{
    public static SaveFlushResult Success { get; } = new(SaveFlushStatus.Success, null);
}
