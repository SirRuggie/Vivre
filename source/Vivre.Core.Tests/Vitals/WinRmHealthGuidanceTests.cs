using Vivre.Core.Vitals;
using Xunit;

namespace Vivre.Core.Tests.Vitals;

/// <summary>
/// The single source of the operator-facing "what happened / how to fix" wording for a degraded WinRM
/// transport, shared by the scorer's reason and the Machine Details Connection callout.
/// </summary>
public class WinRmHealthGuidanceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(WinRmHealth.Healthy)]
    public void Healthy_or_unknown_has_no_guidance(WinRmHealth? health)
    {
        Assert.Null(WinRmHealthGuidance.Caption(health));
        Assert.Null(WinRmHealthGuidance.FixBullets(health));
        Assert.Null(WinRmHealthGuidance.Reason(health));
    }

    [Fact]
    public void Kerberos_guidance_names_both_codes_and_the_spn_cause()
    {
        Assert.Contains("Kerberos", WinRmHealthGuidance.Caption(WinRmHealth.KerberosRejected));

        string fix = string.Join(" ", WinRmHealthGuidance.FixBullets(WinRmHealth.KerberosRejected)!);
        Assert.Contains("SPN", fix);
        Assert.Contains("0x80090322", fix);          // app-server / wrong-principal case
        Assert.Contains("0x80090303", fix);          // not-domain-joined / no-SPN case
        Assert.Contains("setspn -Q", fix);           // the command that tells the two apart

        // The concise reason (the "why this score" line) carries the code the scorer asserts on.
        Assert.Contains("0x80090322", WinRmHealthGuidance.Reason(WinRmHealth.KerberosRejected));
    }

    [Fact]
    public void Winrm_unavailable_guidance_names_the_service_fix_not_kerberos()
    {
        Assert.Contains("WinRM", WinRmHealthGuidance.Caption(WinRmHealth.WinRmUnavailable));

        string fix = string.Join(" ", WinRmHealthGuidance.FixBullets(WinRmHealth.WinRmUnavailable)!);
        Assert.Contains("winrm quickconfig", fix);
        Assert.DoesNotContain("0x80090322", fix);
        Assert.DoesNotContain("0x80090322", WinRmHealthGuidance.Reason(WinRmHealth.WinRmUnavailable));
    }
}
