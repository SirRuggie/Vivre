using Vivre.UpdateAgent;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// <see cref="DismHelper.DescribeDismExit"/> turns a DISM failure exit code into the plain-English
/// reason the uninstall flow surfaces. It's a pure function (compiled in here via the linked agent
/// source), and 0x800F0825 in particular is load-bearing — it's the by-design "permanent cumulative
/// / servicing-stack update can't be removed" case the whole uninstall-reason surfacing exists for.
/// </summary>
public class DismHelperTests
{
    [Fact]
    public void Permanent_package_code_is_explained_as_by_design()
    {
        // DISM returns the HRESULT as its exit code; as a signed int 0x800F0825 is negative, which is
        // exactly the case the unsigned-cast formatting must survive.
        int exit = unchecked((int)0x800F0825);
        Assert.True(exit < 0);

        string reason = DismHelper.DescribeDismExit(exit);

        Assert.Contains("0x800F0825", reason);
        Assert.Contains("permanent", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cumulative", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Access_denied_code_is_named()
    {
        string reason = DismHelper.DescribeDismExit(unchecked((int)0x80070005));

        Assert.Contains("0x80070005", reason);
        Assert.Contains("access denied", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_code_falls_back_to_8_digit_hex()
    {
        string reason = DismHelper.DescribeDismExit(unchecked((int)0x80004005));

        Assert.Contains("0x80004005", reason);
    }

    [Fact]
    public void Zero_is_formatted_as_padded_hex()
    {
        // Not a real failure code (0 = success), but proves the X8 padding rather than a bare "0x0".
        Assert.Contains("0x00000000", DismHelper.DescribeDismExit(0));
    }
}
