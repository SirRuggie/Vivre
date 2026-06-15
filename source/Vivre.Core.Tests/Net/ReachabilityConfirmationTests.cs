using Vivre.Core.Net;
using Xunit;

namespace Vivre.Core.Tests.Net;

public class ReachabilityConfirmationTests
{
    [Fact]
    public void Online_probe_is_online_regardless_of_prior_failure_count()
    {
        Assert.True(ReachabilityConfirmation.ConfirmEffectiveOnline(previous: true, rawOnline: true, consecutiveFailures: 0, threshold: 2));
        Assert.True(ReachabilityConfirmation.ConfirmEffectiveOnline(previous: false, rawOnline: true, consecutiveFailures: 5, threshold: 2));
    }

    [Fact]
    public void Previously_online_box_survives_a_single_failure()
    {
        // One failed probe (count 1) under threshold 2 → still treated as online: no false blip.
        Assert.True(ReachabilityConfirmation.ConfirmEffectiveOnline(previous: true, rawOnline: false, consecutiveFailures: 1, threshold: 2));
    }

    [Fact]
    public void Previously_online_box_flips_offline_on_the_threshold_failure()
    {
        // Second consecutive failure (count 2) reaches threshold 2 → confirmed offline.
        Assert.False(ReachabilityConfirmation.ConfirmEffectiveOnline(previous: true, rawOnline: false, consecutiveFailures: 2, threshold: 2));
    }

    [Fact]
    public void Already_offline_box_flips_on_the_first_failure()
    {
        // No prior online state to protect — flip immediately; there is no blip to suppress.
        Assert.False(ReachabilityConfirmation.ConfirmEffectiveOnline(previous: false, rawOnline: false, consecutiveFailures: 1, threshold: 2));
    }

    [Fact]
    public void Never_seen_box_is_offline_on_the_first_failure()
    {
        Assert.False(ReachabilityConfirmation.ConfirmEffectiveOnline(previous: null, rawOnline: false, consecutiveFailures: 1, threshold: 2));
    }
}
