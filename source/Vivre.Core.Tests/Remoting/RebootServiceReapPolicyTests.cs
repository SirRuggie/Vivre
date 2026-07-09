using Vivre.Core.Remoting;
using Xunit;

namespace Vivre.Core.Tests.Remoting;

/// <summary>
/// Locks the reboot-adjacent DELETE decision of <see cref="RebootServiceReapPolicy"/>. The SCM
/// P/Invoke sweeper (<c>OrphanRebootServiceReaper</c>) stays integration-only — there is no mock
/// boundary over advapi32, the same status as <c>RemoteServiceController</c> (zero tests today) — so
/// the pure classify/should-reap decision is what is locked here. For a deleter the dangerous
/// direction is a false POSITIVE, so every reject case below matters.
/// </summary>
public class RebootServiceReapPolicyTests
{
    // A canonical reapable KEY name: "Vivre_Reboot_" + 32 lowercase hex (the Guid "N" shape).
    private const string ValidName = "Vivre_Reboot_3fa85f6457174562b3fc2c963f66afa6";

    [Fact]
    public void IsReapableName_accepts_exact_lowercase_reboot_service_key()
    {
        // 32 lowercase hex — the Guid.ToString("N") shape DcomRebootTrigger emits.
        Assert.True(RebootServiceReapPolicy.IsReapableName("Vivre_Reboot_" + new string('0', 32)));
        Assert.True(RebootServiceReapPolicy.IsReapableName(ValidName));
    }

    [Theory]
    [InlineData("Vivre_Reboot_3FA85F6457174562B3FC2C963F66AFA6")] // 32 UPPERCASE hex — Vivre never emits this
    [InlineData("Vivre_Reboot")]                                   // the fixed-name scheduled task
    [InlineData("Vivre_Reboot_")]                                  // prefix only, no suffix
    [InlineData("Vivre_Reboot_xyz85f6457174562b3fc2c963f66afa6")]  // 32 chars but non-hex xyz
    [InlineData("Vivre_WUA_3fa85f6457174562b3fc2c963f66afa6")]     // the WUA agent service
    [InlineData("NotVivre_Reboot_3fa85f6457174562b3fc2c963f66afa6")] // decorated prefix
    [InlineData("Vivre_Reboot_3fa85f6457174562b3fc2c963f66afa6X")] // trailing non-hex char
    [InlineData("vivre_reboot_3fa85f6457174562b3fc2c963f66afa6")]  // lowercase (case-variant) prefix
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsReapableName_rejects_anything_not_an_exact_vivre_reboot_key(string? name)
    {
        Assert.False(RebootServiceReapPolicy.IsReapableName(name));
    }

    [Fact]
    public void IsReapableName_rejects_wrong_hex_length()
    {
        Assert.False(RebootServiceReapPolicy.IsReapableName("Vivre_Reboot_" + new string('a', 31))); // 31 hex
        Assert.False(RebootServiceReapPolicy.IsReapableName("Vivre_Reboot_" + new string('a', 33))); // 33 hex
    }

    [Fact]
    public void IsReapableName_rejects_fullwidth_digits()
    {
        // 32 fullwidth digits (U+FF10) — the same length, but the ASCII '0'-'9'/'a'-'f' check must
        // reject them, locking the classifier against a future char.IsDigit-based refactor.
        Assert.False(RebootServiceReapPolicy.IsReapableName("Vivre_Reboot_" + new string('０', 32)));
    }

    [Fact]
    public void ShouldReap_is_true_for_valid_name_and_stopped()
    {
        Assert.True(RebootServiceReapPolicy.ShouldReap(ValidName, RemoteServiceState.Stopped));
    }

    [Theory]
    [InlineData(RemoteServiceState.Running)]
    [InlineData(RemoteServiceState.StartPending)]
    [InlineData(RemoteServiceState.StopPending)]
    [InlineData(RemoteServiceState.Unknown)]
    public void ShouldReap_is_false_for_valid_name_but_non_stopped_state(RemoteServiceState state)
    {
        Assert.False(RebootServiceReapPolicy.ShouldReap(ValidName, state));
    }

    [Fact]
    public void ShouldReap_is_false_for_default_state()
    {
        Assert.False(RebootServiceReapPolicy.ShouldReap(ValidName, default));
    }

    [Fact]
    public void ShouldReap_is_false_for_invalid_name_even_when_stopped()
    {
        Assert.False(RebootServiceReapPolicy.ShouldReap("Vivre_Reboot", RemoteServiceState.Stopped));
    }
}
