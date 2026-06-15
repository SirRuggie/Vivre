using Vivre.Core.PowerShell;
using Xunit;

namespace Vivre.Core.Tests.PowerShell;

/// <summary>
/// <see cref="HostWinRmGate"/> is pure and host-free, so these tests assert its concurrency contract
/// deterministically — no real time delays. The pattern throughout: acquire to fill a tier, then take
/// the next acquire WITHOUT awaiting it and assert <see cref="Task.IsCompleted"/> is <c>false</c> (it
/// is genuinely blocked), then release a slot and assert it completes. Small limits keep the intent
/// obvious.
/// </summary>
public class HostWinRmGateTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Operator_is_not_blocked_when_background_pool_is_full()
    {
        // maxTotal: 2, maxBackground: 1 — one background slot held, one total slot still free for an operator.
        var gate = new HostWinRmGate(maxTotal: 2, maxBackground: 1);

        IDisposable bg = await gate.AcquireAsync("HOST", background: true, None);

        // The background sub-pool is now full (1/1). A second BACKGROUND acquire must block...
        Task<IDisposable> blockedBackground = gate.AcquireAsync("HOST", background: true, None);
        Assert.False(blockedBackground.IsCompleted);

        // ...but an OPERATOR acquire only competes for a total slot (1 of 2 still free), so it completes now.
        Task<IDisposable> operatorAcquire = gate.AcquireAsync("HOST", background: false, None);
        Assert.True(operatorAcquire.IsCompleted);

        // Cleanup: release everything and let the blocked background acquire drain.
        (await operatorAcquire).Dispose();
        bg.Dispose();
        (await blockedBackground).Dispose();
    }

    [Fact]
    public async Task Background_acquires_beyond_max_background_wait_then_proceed_on_release()
    {
        var gate = new HostWinRmGate(maxTotal: 2, maxBackground: 1);

        IDisposable first = await gate.AcquireAsync("HOST", background: true, None);

        // Second background acquire exceeds maxBackground (1) → blocked.
        Task<IDisposable> second = gate.AcquireAsync("HOST", background: true, None);
        Assert.False(second.IsCompleted);

        // Releasing the first background slot lets the queued one proceed.
        first.Dispose();
        IDisposable now = await second;
        Assert.NotNull(now);

        now.Dispose();
    }

    [Fact]
    public async Task Total_never_exceeds_max_total()
    {
        var gate = new HostWinRmGate(maxTotal: 2, maxBackground: 1);

        // Fill both total slots with operator acquires (operators only consume total slots).
        IDisposable a = await gate.AcquireAsync("HOST", background: false, None);
        IDisposable b = await gate.AcquireAsync("HOST", background: false, None);

        // A third acquire of EITHER tier must block — total is at capacity (2/2).
        Task<IDisposable> thirdOperator = gate.AcquireAsync("HOST", background: false, None);
        Assert.False(thirdOperator.IsCompleted);

        Task<IDisposable> thirdBackground = gate.AcquireAsync("HOST", background: true, None);
        Assert.False(thirdBackground.IsCompleted);

        // Free one total slot — exactly one of the waiters proceeds; total stays at 2.
        a.Dispose();
        IDisposable proceeded = await thirdOperator;
        Assert.NotNull(proceeded);

        // The background waiter is still blocked (the freed slot went to the operator; total is 2/2 again).
        Assert.False(thirdBackground.IsCompleted);

        // Drain the rest.
        b.Dispose();
        proceeded.Dispose();
        (await thirdBackground).Dispose();
    }

    [Fact]
    public async Task Releaser_frees_the_slot_for_the_next_acquire()
    {
        var gate = new HostWinRmGate(maxTotal: 1, maxBackground: 1);

        IDisposable held = await gate.AcquireAsync("HOST", background: false, None);

        Task<IDisposable> waiter = gate.AcquireAsync("HOST", background: false, None);
        Assert.False(waiter.IsCompleted);

        held.Dispose();
        IDisposable next = await waiter;
        Assert.NotNull(next);
        next.Dispose();
    }

    [Fact]
    public async Task Double_dispose_does_not_over_release()
    {
        var gate = new HostWinRmGate(maxTotal: 1, maxBackground: 1);

        IDisposable first = await gate.AcquireAsync("HOST", background: false, None);
        first.Dispose();
        first.Dispose(); // second dispose must be a no-op — NOT a second release

        // If the double-dispose had over-released, total would be 2 and BOTH of these would complete.
        // The cap is 1, so the first completes and the second must block.
        IDisposable a = await gate.AcquireAsync("HOST", background: false, None);
        Task<IDisposable> b = gate.AcquireAsync("HOST", background: false, None);
        Assert.False(b.IsCompleted);

        a.Dispose();
        (await b).Dispose();
    }

    [Fact]
    public async Task Different_hosts_are_independent()
    {
        var gate = new HostWinRmGate(maxTotal: 1, maxBackground: 1);

        // Saturate HOST-A's single total slot.
        IDisposable a = await gate.AcquireAsync("HOST-A", background: false, None);

        Task<IDisposable> aBlocked = gate.AcquireAsync("HOST-A", background: false, None);
        Assert.False(aBlocked.IsCompleted);

        // A DIFFERENT host has its own slots — this completes immediately despite HOST-A being full.
        Task<IDisposable> bAcquire = gate.AcquireAsync("HOST-B", background: false, None);
        Assert.True(bAcquire.IsCompleted);

        (await bAcquire).Dispose();
        a.Dispose();
        (await aBlocked).Dispose();
    }

    [Fact]
    public async Task Host_key_is_case_insensitive()
    {
        var gate = new HostWinRmGate(maxTotal: 1, maxBackground: 1);

        IDisposable held = await gate.AcquireAsync("host", background: false, None);

        // Same host in different casing shares the same per-host state — so this blocks.
        Task<IDisposable> sameHostUpper = gate.AcquireAsync("HOST", background: false, None);
        Assert.False(sameHostUpper.IsCompleted);

        held.Dispose();
        (await sameHostUpper).Dispose();
    }
}
