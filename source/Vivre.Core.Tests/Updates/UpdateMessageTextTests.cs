using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

public class UpdateMessageTextTests
{
    // "·" is the middot the on-target agent writes (" · reboot required"); kept as an escape so the
    // test exercises the exact runtime character regardless of this file's encoding.
    [Theory]
    [InlineData("Installed 2 updates · reboot required", "Installed 2 updates")]   // current agent wording (middot)
    [InlineData("Installed 2 updates, reboot required", "Installed 2 updates")]         // older wording (comma)
    [InlineData("Installed 2, 1 failed · reboot required", "Installed 2, 1 failed")] // internal comma is kept
    [InlineData("Uninstalled 1 update · reboot required", "Uninstalled 1 update")]
    [InlineData("Installed 2 updates · Reboot Required", "Installed 2 updates")]    // case-insensitive
    public void Strips_a_trailing_reboot_required_clause_regardless_of_separator(string input, string expected) =>
        Assert.Equal(expected, UpdateMessageText.WithoutRebootRequiredTail(input));

    [Theory]
    [InlineData("Up to date")]
    [InlineData("Installed 2 updates")]
    [InlineData("Scanning")]
    [InlineData("reboot required before staging the CU")]   // phrase not at the end -> untouched
    public void Leaves_a_message_without_a_trailing_reboot_clause_unchanged(string input) =>
        Assert.Equal(input, UpdateMessageText.WithoutRebootRequiredTail(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Passes_null_or_empty_through(string? input) =>
        Assert.Equal(input, UpdateMessageText.WithoutRebootRequiredTail(input));
}
