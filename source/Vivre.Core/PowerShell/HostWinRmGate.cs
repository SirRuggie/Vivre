using System.Collections.Concurrent;

namespace Vivre.Core.PowerShell;

/// <summary>
/// A thread-safe, per-host cap on concurrent WinRM shells. The fleet-wide op-type throttles bound how
/// many operations of a given kind run at once; they do NOT bound how many shells stack on the SAME
/// host. Without this gate, a slow background probe plus a couple of operator actions plus the
/// monitor's reboot-pending poll can stack several PSRP shells on one box at once — wasteful, and on a
/// hardened target it can trip <c>MaxShellsPerUser</c>.
/// <para>
/// Two limits per host: <c>maxTotal</c> (default 4) bounds all concurrent shells, and
/// <c>maxBackground</c> (default 2) bounds the shells held by low-priority background probes.
/// 4 total stays well under WinRM's default <c>MaxShellsPerUser</c> (30) and typical hardened caps.
/// Capping background at 2 reserves at least (maxTotal − maxBackground) slots for operator-initiated
/// work, so a user's action never waits behind a backlog of background probes — an operator acquire
/// only ever competes for a <c>_total</c> slot, never for a <c>_background</c> slot.
/// </para>
/// <para>
/// The per-host map grows with the number of distinct hosts the app touches in a session — bounded by
/// the fleet size, not unbounded. That is by design; do NOT add eviction (the entries are tiny and a
/// host the app touched once may be touched again).
/// </para>
/// </summary>
public sealed class HostWinRmGate
{
    private readonly int _maxTotal;
    private readonly int _maxBackground;
    private readonly ConcurrentDictionary<string, PerHost> _hosts =
        new(StringComparer.OrdinalIgnoreCase);

    public HostWinRmGate(int maxTotal = 4, int maxBackground = 2)
    {
        _maxTotal = maxTotal;
        _maxBackground = maxBackground;
    }

    /// <summary>
    /// Acquires a shell slot for <paramref name="host"/>, waiting if the host is at capacity. Dispose
    /// the returned token to release the slot (exactly once — a double-Dispose is a no-op).
    /// <paramref name="background"/> = a low-priority background probe (e.g. the monitor's
    /// reboot-pending poll), which competes for the smaller background sub-pool first; operator
    /// actions pass <see langword="false"/> so they take priority on the per-host gate.
    /// </summary>
    public Task<IDisposable> AcquireAsync(string host, bool background, CancellationToken ct) =>
        _hosts.GetOrAdd(host, _ => new PerHost(_maxTotal, _maxBackground)).AcquireAsync(background, ct);

    /// <summary>
    /// Per-host state: a <see cref="SemaphoreSlim"/> for total shells and a smaller one for the
    /// background sub-pool. A background acquire takes a background slot THEN a total slot (so it can
    /// never starve operators); an operator acquire takes only a total slot.
    /// </summary>
    private sealed class PerHost
    {
        private readonly SemaphoreSlim _total;
        private readonly SemaphoreSlim _background;

        public PerHost(int maxTotal, int maxBackground)
        {
            _total = new SemaphoreSlim(maxTotal, maxTotal);
            _background = new SemaphoreSlim(maxBackground, maxBackground);
        }

        public async Task<IDisposable> AcquireAsync(bool background, CancellationToken ct)
        {
            if (background)
            {
                // Take a background slot first so background work is capped independently, then a total
                // slot. If the total wait fails (cancelled), release the background slot we already hold
                // so we don't leak it.
                await _background.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await _total.WaitAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    _background.Release();
                    throw;
                }

                return new Releaser(_total, _background);
            }

            // Operator: only competes for a total slot — never blocked by the background sub-pool.
            await _total.WaitAsync(ct).ConfigureAwait(false);
            return new Releaser(_total, background: null);
        }

        /// <summary>
        /// Releases the held slot(s) exactly once. <see cref="Interlocked"/> guards the flip so a
        /// double-Dispose can't over-release the semaphores.
        /// </summary>
        private sealed class Releaser : IDisposable
        {
            private readonly SemaphoreSlim _total;
            private readonly SemaphoreSlim? _background;
            private int _released;

            public Releaser(SemaphoreSlim total, SemaphoreSlim? background)
            {
                _total = total;
                _background = background;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) != 0)
                {
                    return; // already released — double-Dispose is a no-op
                }

                _total.Release();
                _background?.Release();
            }
        }
    }
}
