using Vivre.Core;
using Xunit;

namespace Vivre.Core.Tests;

public class AppInfoTests
{
    [Fact]
    public void ProductName_is_Vivre()
    {
        Assert.Equal("Vivre", AppInfo.ProductName);
    }
}
