using Vivre.Core.Computers;
using Xunit;

namespace Vivre.Core.Tests.Computers;

public class ComputerNameInputTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ,; \t\r\n")]
    public void Empty_input_has_no_targets(string? input) => Assert.Empty(ComputerNameInput.Parse(input));

    [Fact]
    public void Mixed_separators_preserve_order_and_deduplicate_case_insensitively()
    {
        Assert.Equal(["DUMMY-A", "dummy-b.example.test", "192.0.2.1", "DUMMY-C"],
            ComputerNameInput.Parse(" DUMMY-A,dummy-b.example.test;192.0.2.1\tDUMMY-C\r\ndummy-a DUMMY-C"));
    }
}
