using Vivre.Core.Threading;

namespace Vivre.Core.Tests.Threading;

public sealed class KeyedOperationGateTests
{
    [Fact]
    public async Task Cancellation_keeps_holder_entry_until_last_owner_and_double_dispose_is_safe()
    {
        var gate = new KeyedOperationGate();
        IDisposable first = await gate.AcquireAsync("DUMMY", default);
        using var cancel = new CancellationTokenSource();
        Task<IDisposable> pending = gate.AcquireAsync("dummy", cancel.Token);
        Assert.False(pending.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, gate.EntryCount);
        first.Dispose(); first.Dispose();
        Assert.Equal(0, gate.EntryCount);
    }

    [Fact]
    public async Task Contended_keys_never_overlap_and_unique_keys_retire()
    {
        var gate = new KeyedOperationGate();
        int active = 0;
        await Task.WhenAll(Enumerable.Range(0, 300).Select(async i =>
        {
            using (await gate.AcquireAsync(i % 2 == 0 ? "dummy" : "DUMMY", default))
            {
                Assert.Equal(1, Interlocked.Increment(ref active));
                await Task.Yield();
                Assert.Equal(0, Interlocked.Decrement(ref active));
            }
            using (await gate.AcquireAsync("unique-" + i, default)) { }
        }));
        Assert.Equal(0, gate.EntryCount);
    }
}
