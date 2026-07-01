using Vivre.Core.Vitals;
using Xunit;

namespace Vivre.Core.Tests.Vitals;

public class MachineVitalsTests
{
    [Fact]
    public void IsGenuineReach_is_false_for_a_blank_kerberos_rejected_snapshot()
    {
        // The exact snapshot VitalsProbe returns when WinRM rejects Kerberos AND the DCOM fallback also
        // fails: all OS data null, only transport metadata set → IsEmpty, so NOT a genuine reach. This is
        // the case that must NOT mark the row online/managed (it would re-trigger "Offline since… waiting").
        var blank = new MachineVitals { WinRmHealth = WinRmHealth.KerberosRejected, WinRmFailureDetail = "cannot connect" };

        Assert.True(blank.IsEmpty);
        Assert.False(blank.IsGenuineReach);
    }

    [Fact]
    public void IsGenuineReach_is_false_when_only_transport_metadata_is_present()
    {
        // Transport metadata (WinRmHealth/WinRmFailureDetail) is deliberately excluded from IsEmpty, so a
        // snapshot carrying ONLY those (both transports failed, no OS reading) is not a reach.
        var flaggedOnly = new MachineVitals { WinRmHealth = WinRmHealth.WinRmUnavailable };

        Assert.True(flaggedOnly.IsEmpty);
        Assert.False(flaggedOnly.IsGenuineReach);
    }

    [Fact]
    public void IsGenuineReach_is_true_for_a_partial_dcom_read_even_when_flagged()
    {
        // A DCOM read that got SOME data (e.g. memory + boot) but still carries the KerberosRejected flag
        // IS a real reach — it must keep the "genuinely managed" credit so the reboot-wave "waiting"
        // tracking survives when that box is later rebooted for patching.
        var partial = new MachineVitals(MemoryUsedPercent: 42, LastBootTime: new DateTime(2026, 7, 1, 8, 0, 0))
        {
            WinRmHealth = WinRmHealth.KerberosRejected,
        };

        Assert.False(partial.IsEmpty);
        Assert.True(partial.IsGenuineReach);
    }

    [Fact]
    public void IsGenuineReach_is_true_for_a_full_healthy_read()
    {
        var full = new MachineVitals(
            SystemDriveFreePercent: 55, MemoryUsedPercent: 40, CpuLoadPercent: 5,
            LastBootTime: new DateTime(2026, 6, 30, 9, 0, 0), StoppedAutoServiceCount: 0,
            RebootPending: false, UserLoggedOn: false)
        {
            WinRmHealth = WinRmHealth.Healthy,
        };

        Assert.False(full.IsEmpty);
        Assert.True(full.IsGenuineReach);
    }
}
