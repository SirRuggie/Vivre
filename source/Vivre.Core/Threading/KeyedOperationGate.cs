namespace Vivre.Core.Threading;

/// <summary>Per-key exclusion with coordinator-style ownership of holders and queued waiters.</summary>
internal sealed class KeyedOperationGate
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    internal int EntryCount { get { lock (_sync) return _entries.Count; } }

    internal async Task<IDisposable> AcquireAsync(string key, CancellationToken token)
    {
        Entry entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out entry!)) _entries.Add(key, entry = new());
            entry.Owners++;
        }
        try { await entry.Semaphore.WaitAsync(token).ConfigureAwait(false); }
        catch { Release(key, entry, acquired: false); throw; }
        return new Lease(this, key, entry);
    }

    private void Release(string key, Entry entry, bool acquired)
    {
        lock (_sync)
        {
            // All queued acquisitions already own a reference. New acquisitions cannot race removal.
            if (acquired) entry.Semaphore.Release();
            if (--entry.Owners == 0)
            {
                _entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        internal readonly SemaphoreSlim Semaphore = new(1, 1);
        internal int Owners;
    }

    private sealed class Lease(KeyedOperationGate owner, string key, Entry entry) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(key, entry, acquired: true);
        }
    }
}
