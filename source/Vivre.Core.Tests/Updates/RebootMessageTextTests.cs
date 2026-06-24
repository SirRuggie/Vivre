using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Locks which reboot-message-column strings count as a transient PAST-EVENT notice that a new operation
/// clears. The set-site wording lives in WorkspaceViewModel (a different project), so these prefixes can
/// drift silently — this test is the regression lock. The two CURRENT-STATE notices must stay excluded so
/// their own condition-based clearers keep owning them.
/// </summary>
public class RebootMessageTextTests
{
    [Theory]
    // Past-event notices with no other clearer → cleared on the next operation.
    [InlineData("Reboot complete — back online 10:22")]
    [InlineData("Back online 10:22")]
    [InlineData("Back online 10:22 (down 5m 12s)")]
    [InlineData("Forced reboot sent 10:22")]
    public void Transient_past_event_notices_are_cleared(string message) =>
        Assert.True(RebootMessageText.IsTransientRebootNotice(message));

    [Theory]
    // Current-state notices keep their OWN condition-based clearers — must NOT be cleared by a new operation
    // (clearing "Offline since…" while the box is still offline would wrongly blank a valid live message).
    [InlineData("Offline since 10:00 — waiting for it to come back…")]
    [InlineData("WinRM temporarily unavailable on HOST — backing off, will retry.")]
    [InlineData("WinRM temporarily unavailable on HOST (reboot still pending) — backing off.")]
    // Nothing to clear.
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Some unrelated message")]
    public void Current_state_and_empty_messages_are_not_cleared(string? message) =>
        Assert.False(RebootMessageText.IsTransientRebootNotice(message));
}
