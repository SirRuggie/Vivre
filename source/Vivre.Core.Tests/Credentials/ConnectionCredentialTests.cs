using System.Security;
using Vivre.Core.Credentials;
using Xunit;

namespace Vivre.Core.Tests.Credentials;

public class ConnectionCredentialTests
{
    [Fact]
    public void DisplayName_combines_domain_and_user()
    {
        var cred = new ConnectionCredential("EMPLOYEES", "admin_sbridges", Secure("pw"));

        Assert.Equal(@"EMPLOYEES\admin_sbridges", cred.DisplayName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_domain_yields_bare_user(string? domain)
    {
        var cred = new ConnectionCredential(domain, "svc", Secure("pw"));

        Assert.Null(cred.Domain);
        Assert.Equal("svc", cred.DisplayName);
    }

    [Fact]
    public void ToPowerShellCredential_uses_display_name()
    {
        var cred = new ConnectionCredential("EMPLOYEES", "admin_sbridges", Secure("pw"));

        var ps = cred.ToPowerShellCredential();

        Assert.Equal(@"EMPLOYEES\admin_sbridges", ps.UserName);
    }

    [Fact]
    public void Store_defaults_to_no_explicit_credential()
    {
        var store = new CredentialStore();

        Assert.Null(store.Current);
        Assert.False(store.HasExplicitCredential);
    }

    private static SecureString Secure(string s)
    {
        var ss = new SecureString();
        foreach (char c in s)
        {
            ss.AppendChar(c);
        }

        ss.MakeReadOnly();
        return ss;
    }
}
