using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// The pure StdRegProv-ReturnValue classifier behind the CBS RebootPending signal. It is the DCOM-free
/// half of <see cref="DcomRebootReadinessProbe"/>: the three-way read that stops an ACCESS-DENIED (rv=5)
/// from masquerading as "nothing staged". Only 0 (present) and 2 (absent) are benign verdicts; every other
/// code — and a null — is "couldn't confirm", never a settled answer.
/// </summary>
public class DcomRebootReadinessProbeTests
{
    [Theory]
    [InlineData(0u, null)]                              // present → no not-ready verdict (keep checking)
    [InlineData(2u, RebootReadinessKind.NothingStaged)] // ERROR_FILE_NOT_FOUND → definitively absent
    [InlineData(5u, RebootReadinessKind.CantConfirm)]   // ACCESS-DENIED → NOT "nothing staged"
    [InlineData(6u, RebootReadinessKind.CantConfirm)]   // any other non-benign code
    [InlineData(87u, RebootReadinessKind.CantConfirm)]  // ERROR_INVALID_PARAMETER, etc.
    public void ClassifyRebootPendingRv_maps_return_values(uint rv, RebootReadinessKind? expected)
    {
        Assert.Equal(expected, DcomRebootReadinessProbe.ClassifyRebootPendingRv(rv));
    }

    [Fact]
    public void ClassifyRebootPendingRv_null_return_value_is_cant_confirm()
    {
        // A missing/unconvertible ReturnValue is unknown — never mistaken for present (0) or absent (2).
        Assert.Equal(RebootReadinessKind.CantConfirm, DcomRebootReadinessProbe.ClassifyRebootPendingRv(null));
    }
}
