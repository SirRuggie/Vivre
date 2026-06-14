using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Tests for <see cref="ReadyConfirmation"/> via the injected OS-query delegate seam.
/// Covers: queryable OS → Confirmed; unreachable/exception → NotReady; never Failed.
/// </summary>
public class ReadyConfirmationTests
{
    [Fact]
    public async Task Os_queryable_returns_Confirmed()
    {
        var sut = new ReadyConfirmation(osQuery: (_, _) => Task.FromResult(true));

        RebootConfirmationResult result = await sut.ConfirmAsync("BOX", CancellationToken.None);

        Assert.Equal(RebootConfirmationOutcome.Confirmed, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task Os_unreachable_returns_NotReady()
    {
        var sut = new ReadyConfirmation(osQuery: (_, _) => Task.FromResult(false));

        RebootConfirmationResult result = await sut.ConfirmAsync("BOX", CancellationToken.None);

        Assert.Equal(RebootConfirmationOutcome.NotReady, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task Os_query_throws_returns_NotReady_never_Failed()
    {
        // An actually-throwing delegate simulates DCOM blowing up mid-query.
        // ConfirmAsync must catch the exception, NOT propagate it, and return NotReady.
        var sut = new ReadyConfirmation(
            osQuery: (_, _) => throw new InvalidOperationException("DCOM blew up"));

        RebootConfirmationResult result = await sut.ConfirmAsync("BOX", CancellationToken.None);

        Assert.Equal(RebootConfirmationOutcome.NotReady, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task Confirmed_message_is_not_empty()
    {
        var sut = new ReadyConfirmation(osQuery: (_, _) => Task.FromResult(true));

        RebootConfirmationResult result = await sut.ConfirmAsync("MYBOX", CancellationToken.None);

        Assert.Equal(RebootConfirmationOutcome.Confirmed, result.Outcome);
        Assert.Equal("Back online.", result.Message);
    }

    [Fact]
    public async Task NotReady_message_indicates_still_coming_up()
    {
        var sut = new ReadyConfirmation(osQuery: (_, _) => Task.FromResult(false));

        RebootConfirmationResult result = await sut.ConfirmAsync("MYBOX", CancellationToken.None);

        Assert.Equal(RebootConfirmationOutcome.NotReady, result.Outcome);
        Assert.Contains("coming up", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadyConfirmation_never_returns_Failed()
    {
        // Both possible outcomes (reachable / unreachable) must never produce Failed.
        var reachable = new ReadyConfirmation(osQuery: (_, _) => Task.FromResult(true));
        var unreachable = new ReadyConfirmation(osQuery: (_, _) => Task.FromResult(false));

        RebootConfirmationResult r1 = await reachable.ConfirmAsync("BOX", CancellationToken.None);
        RebootConfirmationResult r2 = await unreachable.ConfirmAsync("BOX", CancellationToken.None);

        Assert.NotEqual(RebootConfirmationOutcome.Failed, r1.Outcome);
        Assert.NotEqual(RebootConfirmationOutcome.Failed, r2.Outcome);
    }
}
