using Vivre.Core.Models;
using Xunit;

namespace Vivre.Core.Tests.Models;

public class CredentialTests
{
    [Fact]
    public void DisplayName_combines_domain_and_user()
    {
        var credential = new Credential { Domain = "CONTOSO", UserName = "admin" };

        Assert.Equal(@"CONTOSO\admin", credential.DisplayName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DisplayName_is_just_user_when_domain_is_blank(string? domain)
    {
        var credential = new Credential { Domain = domain, UserName = "admin" };

        Assert.Equal("admin", credential.DisplayName);
    }

    [Fact]
    public void Changing_Domain_notifies_DisplayName()
    {
        var credential = new Credential { UserName = "admin" };
        var raised = new List<string?>();
        credential.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        credential.Domain = "CONTOSO";

        Assert.Contains(nameof(Credential.DisplayName), raised);
    }

    [Fact]
    public void Changing_UserName_notifies_DisplayName()
    {
        var credential = new Credential { Domain = "CONTOSO", UserName = "admin" };
        var raised = new List<string?>();
        credential.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        credential.UserName = "svc-sccm";

        Assert.Contains(nameof(Credential.DisplayName), raised);
    }
}
