using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Tests for <see cref="ReadyConfirmation"/> via the injected boot-time-query seam. The rule: a reboot is
/// confirmed ONLY when the box returns with a NEWER LastBootUpTime than it had before the reboot — a brief
/// reachability flicker that comes back on the SAME boot is NOT a reboot (the bug this fixes), and it never
/// returns Failed.
/// </summary>
public class ReadyConfirmationTests
{
    private static readonly DateTime BootBefore = new(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BootAfter = BootBefore.AddMinutes(5); // a real reboot → later boot time

    // Returns the queued boot times in order (CaptureBaseline reads the first, then each ConfirmAsync the
    // next), clamping to the last once exhausted.
    private static Func<string, CancellationToken, Task<DateTime?>> Sequence(params DateTime?[] values)
    {
        int i = 0;
        return (_, _) => Task.FromResult(i < values.Length ? values[i++] : values[^1]);
    }

    [Fact]
    public async Task A_newer_boot_time_is_Confirmed()
    {
        var sut = new ReadyConfirmation(Sequence(BootBefore, BootAfter));
        await sut.CaptureBaselineAsync("BOX", CancellationToken.None); // baseline = BootBefore

        RebootConfirmationResult result = await sut.ConfirmAsync("BOX", CancellationToken.None); // BootAfter

        Assert.Equal(RebootConfirmationOutcome.Confirmed, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task A_flicker_same_boot_time_is_NOT_accepted_as_the_reboot()
    {
        // The box dropped off the network briefly during reboot-prep and answered again on the SAME boot —
        // it has NOT rebooted. This is the bug: it must be NotReady (keep watching), never Confirmed.
        var sut = new ReadyConfirmation(Sequence(BootBefore, BootBefore));
        await sut.CaptureBaselineAsync("BOX", CancellationToken.None);

        RebootConfirmationResult result = await sut.ConfirmAsync("BOX", CancellationToken.None);

        Assert.Equal(RebootConfirmationOutcome.NotReady, result.Outcome);
    }

    [Fact]
    public async Task Keeps_watching_after_an_unchanged_check_then_confirms_on_the_real_reboot()
    {
        // Flicker first (same boot → NotReady, keep watching), then the real reboot (newer boot → Confirmed).
        var sut = new ReadyConfirmation(Sequence(BootBefore, BootBefore, BootAfter));
        await sut.CaptureBaselineAsync("BOX", CancellationToken.None);

        Assert.Equal(RebootConfirmationOutcome.NotReady, (await sut.ConfirmAsync("BOX", CancellationToken.None)).Outcome);
        Assert.Equal(RebootConfirmationOutcome.Confirmed, (await sut.ConfirmAsync("BOX", CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task An_unreadable_boot_time_is_NotReady()
    {
        var sut = new ReadyConfirmation(Sequence(BootBefore, null));
        await sut.CaptureBaselineAsync("BOX", CancellationToken.None);

        RebootConfirmationResult result = await sut.ConfirmAsync("BOX", CancellationToken.None);

        Assert.Equal(RebootConfirmationOutcome.NotReady, result.Outcome);
        Assert.Contains("coming up", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task No_baseline_never_confirms_on_OS_answered_alone()
    {
        // CaptureBaselineAsync was never called (or its read failed) → we can't prove a reboot, so even a
        // readable boot time must NOT confirm (that was the flicker bug); keep watching.
        var sut = new ReadyConfirmation(Sequence(BootAfter));

        RebootConfirmationResult result = await sut.ConfirmAsync("BOX", CancellationToken.None);

        Assert.Equal(RebootConfirmationOutcome.NotReady, result.Outcome);
    }

    [Fact]
    public async Task A_query_that_throws_returns_NotReady_never_Failed()
    {
        var sut = new ReadyConfirmation((_, _) => throw new InvalidOperationException("DCOM blew up"));
        await sut.CaptureBaselineAsync("BOX", CancellationToken.None); // swallows the throw → baseline null

        RebootConfirmationResult result = await sut.ConfirmAsync("BOX", CancellationToken.None);

        Assert.Equal(RebootConfirmationOutcome.NotReady, result.Outcome);
    }

    [Fact]
    public async Task Never_returns_Failed()
    {
        // Every scenario (newer / same / unreadable / no-baseline) must avoid Failed — updates "taking" is
        // decided later by the WUA rescan, not here.
        var confirmed = new ReadyConfirmation(Sequence(BootBefore, BootAfter));
        await confirmed.CaptureBaselineAsync("BOX", CancellationToken.None);
        var flicker = new ReadyConfirmation(Sequence(BootBefore, BootBefore));
        await flicker.CaptureBaselineAsync("BOX", CancellationToken.None);

        Assert.NotEqual(RebootConfirmationOutcome.Failed, (await confirmed.ConfirmAsync("BOX", CancellationToken.None)).Outcome);
        Assert.NotEqual(RebootConfirmationOutcome.Failed, (await flicker.ConfirmAsync("BOX", CancellationToken.None)).Outcome);
    }
}
