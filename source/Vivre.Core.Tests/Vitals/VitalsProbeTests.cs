using System.Management.Automation;
using Vivre.Core.PowerShell;
using Vivre.Core.Vitals;
using Xunit;

namespace Vivre.Core.Tests.Vitals;

public class VitalsProbeTests
{
    [Fact]
    public async Task Parses_scalar_signals()
    {
        var probe = new VitalsProbe(new FakeHost(ResultFrom(VitalsObject(
            sysFreePct: 42.5, memUsedPct: 71, cpu: 18, stopped: 2, rebootPending: true))));

        MachineVitals v = await probe.GetVitalsAsync("localhost");

        Assert.Equal(42.5, v.SystemDriveFreePercent);
        Assert.Equal(71, v.MemoryUsedPercent);
        Assert.Equal(18, v.CpuLoadPercent);
        Assert.Equal(2, v.StoppedAutoServiceCount);
        Assert.True(v.RebootPending);
        Assert.False(v.IsEmpty);
    }

    [Fact]
    public async Task Parses_the_drives_array()
    {
        var obj = VitalsObject(sysFreePct: 30);
        obj.Properties.Add(new PSNoteProperty("Drives", new object[]
        {
            Drive("C:", 30, 60, 200),
            Drive("D:", 80, 800, 1000),
        }));

        var probe = new VitalsProbe(new FakeHost(ResultFrom(obj)));

        MachineVitals v = await probe.GetVitalsAsync("localhost");

        Assert.Equal(2, v.Drives.Count);
        Assert.Equal("C:", v.Drives[0].Letter);
        Assert.Equal(60, v.Drives[0].FreeGb);
        Assert.Equal("D:", v.Drives[1].Letter);
    }

    [Fact]
    public async Task Parses_stopped_service_names()
    {
        var obj = VitalsObject(stopped: 2);
        obj.Properties.Add(new PSNoteProperty("StoppedAutoServices", new object[] { "Print Spooler", "Windows Update" }));

        var probe = new VitalsProbe(new FakeHost(ResultFrom(obj)));

        MachineVitals v = await probe.GetVitalsAsync("localhost");

        Assert.Equal(new[] { "Print Spooler", "Windows Update" }, v.StoppedAutoServices);
    }

    [Fact]
    public async Task Parses_operating_system()
    {
        var obj = VitalsObject(sysFreePct: 50);
        obj.Properties.Add(new PSNoteProperty("OperatingSystem", "Windows Server 2016 Standard — 10.0.14393"));

        var probe = new VitalsProbe(new FakeHost(ResultFrom(obj)));

        MachineVitals v = await probe.GetVitalsAsync("NYC-FP1");

        Assert.Equal("Windows Server 2016 Standard — 10.0.14393", v.OperatingSystem);
    }

    [Fact]
    public async Task Parses_arrays_wrapped_by_remoting()
    {
        // Over WinRM the array properties arrive wrapped in a PSObject (BaseObject = the real array),
        // not as the bare object[] seen locally. The probe must unwrap them, or the detail lists come
        // back empty on remote hosts even though the counts are right (the bug seen on NYC-FP1).
        var obj = VitalsObject(stopped: 2);
        obj.Properties.Add(new PSNoteProperty("Drives", new PSObject(new object[] { Drive("C:", 30, 60, 200) })));
        obj.Properties.Add(new PSNoteProperty("StoppedAutoServices", new PSObject(new object[] { "Print Spooler", "Windows Update" })));

        var probe = new VitalsProbe(new FakeHost(ResultFrom(obj)));

        MachineVitals v = await probe.GetVitalsAsync("NYC-FP1");

        Assert.Single(v.Drives);
        Assert.Equal("C:", v.Drives[0].Letter);
        Assert.Equal(new[] { "Print Spooler", "Windows Update" }, v.StoppedAutoServices);
    }

    [Fact]
    public async Task Missing_optional_signals_parse_to_null()
    {
        // An object with only disk read — everything else absent should be null, not throw.
        var obj = new PSObject();
        obj.Properties.Add(new PSNoteProperty("SystemDriveFreePercent", 55.0));

        var probe = new VitalsProbe(new FakeHost(ResultFrom(obj)));

        MachineVitals v = await probe.GetVitalsAsync("localhost");

        Assert.Equal(55.0, v.SystemDriveFreePercent);
        Assert.Null(v.MemoryUsedPercent);
        Assert.Null(v.RebootPending);
        Assert.Empty(v.Drives);
    }

    [Fact]
    public async Task Throws_when_no_data_returned()
    {
        var empty = new PSExecutionResult([], ["Connecting to remote server failed"], [], HadErrors: true);
        var probe = new VitalsProbe(new FakeHost(empty));

        VitalsProbeException ex = await Assert.ThrowsAsync<VitalsProbeException>(
            () => probe.GetVitalsAsync("NYC-FP1"));

        Assert.Contains("Connecting to remote server failed", ex.Message);
    }

    [Fact]
    public async Task Local_host_runs_locally()
    {
        var host = new FakeHost(ResultFrom(VitalsObject()));
        var probe = new VitalsProbe(host);

        await probe.GetVitalsAsync("localhost");

        Assert.True(host.LocalCalled);
        Assert.False(host.RemoteCalled);
    }

    [Fact]
    public async Task Remote_host_runs_over_winrm()
    {
        var host = new FakeHost(ResultFrom(VitalsObject()));
        var probe = new VitalsProbe(host);

        await probe.GetVitalsAsync("NYC-FP1");

        Assert.True(host.RemoteCalled);
        Assert.False(host.LocalCalled);
    }

    private static PSExecutionResult ResultFrom(PSObject row) => new([row], [], [], HadErrors: false);

    private static PSObject Drive(string letter, double freePct, double freeGb, double sizeGb)
    {
        var o = new PSObject();
        o.Properties.Add(new PSNoteProperty("Letter", letter));
        o.Properties.Add(new PSNoteProperty("FreePercent", freePct));
        o.Properties.Add(new PSNoteProperty("FreeGb", freeGb));
        o.Properties.Add(new PSNoteProperty("SizeGb", sizeGb));
        return o;
    }

    private static PSObject VitalsObject(
        double? sysFreePct = 50,
        double? memUsedPct = 40,
        double? cpu = 10,
        int? stopped = 0,
        bool? rebootPending = false)
    {
        var o = new PSObject();
        o.Properties.Add(new PSNoteProperty("SystemDriveFreePercent", sysFreePct));
        o.Properties.Add(new PSNoteProperty("MemoryUsedPercent", memUsedPct));
        o.Properties.Add(new PSNoteProperty("CpuLoadPercent", cpu));
        o.Properties.Add(new PSNoteProperty("StoppedAutoServiceCount", stopped));
        o.Properties.Add(new PSNoteProperty("RebootPending", rebootPending));
        return o;
    }

    private sealed class FakeHost : IPowerShellHost
    {
        private readonly PSExecutionResult _result;

        public FakeHost(PSExecutionResult result) => _result = result;

        public bool LocalCalled { get; private set; }

        public bool RemoteCalled { get; private set; }

        public Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken cancellationToken = default)
        {
            LocalCalled = true;
            return Task.FromResult(_result);
        }

        public Task<PSExecutionResult> RunRemoteAsync(
            string host,
            string script,
            PSCredential? credential = null,
            int port = 5985,
            bool useSsl = false,
            CancellationToken cancellationToken = default)
        {
            RemoteCalled = true;
            return Task.FromResult(_result);
        }

        public Task<PSExecutionResult> RunRemoteStreamingAsync(
            string host,
            string script,
            Action<PSObject> onOutput,
            PSCredential? credential = null,
            int port = 5985,
            bool useSsl = false,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Vitals tests don't exercise the streaming path.");
    }

    [Fact]
    public async Task Kerberos_rejection_returns_a_blank_snapshot_flagged_for_attention()
    {
        // When the host rejects Kerberos (the routing host throws KerberosWrongPrincipalException),
        // GetVitalsAsync returns a blank snapshot FLAGGED KerberosRejected — NOT a throw — so the scorer
        // surfaces the recommended fix and flags the box for attention rather than a bare "vitals unavailable".
        var probe = new VitalsProbe(new ThrowingHost(
            new KerberosWrongPrincipalException("BADBOX", new Exception("errorcode 0x80090322"))));

        MachineVitals v = await probe.GetVitalsAsync("BADBOX");

        Assert.Equal(WinRmHealth.KerberosRejected, v.WinRmHealth);
        Assert.True(v.IsEmpty); // no readings, but flagged

        VitalityResult r = VitalityScorer.Score(v, isOnline: true);
        Assert.True(r.NeedsAttention);
        Assert.Contains(r.Reasons, reason => reason.Contains("0x80090322"));
    }

    [Fact]
    public async Task Kerberos_rejection_with_dcom_reader_returns_real_vitals_and_flag()
    {
        // When WinRM throws KerberosWrongPrincipalException AND a DCOM reader is wired in, GetVitalsAsync
        // should return the real vitals from DCOM PLUS WinRmHealth=KerberosRejected so the row shows
        // real numbers AND stays visibly flagged on the fallback path.
        var realVitals = new MachineVitals(
            SystemDriveFreePercent: 55,
            MemoryUsedPercent: 60,
            CpuLoadPercent: 15,
            RebootPending: false);

        var probe = new VitalsProbe(
            new ThrowingHost(new KerberosWrongPrincipalException("FALLBOX", new Exception("0x80090322"))),
            new FakeDcomReader(realVitals));

        MachineVitals v = await probe.GetVitalsAsync("FALLBOX");

        // Real vitals must be preserved.
        Assert.Equal(55, v.SystemDriveFreePercent);
        Assert.Equal(60, v.MemoryUsedPercent);
        Assert.Equal(15, v.CpuLoadPercent);
        Assert.False(v.RebootPending);
        Assert.False(v.IsEmpty); // real readings present

        // Flag must still be set — the Kerberos channel must not be silenced.
        Assert.Equal(WinRmHealth.KerberosRejected, v.WinRmHealth);

        // The actual WinRM error is captured so Machine Details can show WHAT failed.
        Assert.NotNull(v.WinRmFailureDetail);
        Assert.Contains("0x80090322", v.WinRmFailureDetail);

        // Scorer confirms attention is needed despite real (healthy-looking) vitals.
        VitalityResult r = VitalityScorer.Score(v, isOnline: true);
        Assert.True(r.NeedsAttention);
        Assert.Contains(r.Reasons, reason => reason.Contains("0x80090322"));
    }

    [Fact]
    public async Task Kerberos_rejection_with_failing_dcom_reader_returns_blank_flagged_snapshot()
    {
        // When WinRM rejects Kerberos AND the DCOM reader also throws, GetVitalsAsync must fall back
        // gracefully to the blank KerberosRejected snapshot — no exception should propagate to the caller.
        var probe = new VitalsProbe(
            new ThrowingHost(new KerberosWrongPrincipalException("DEADBOX", new Exception("0x80090322"))),
            new ThrowingDcomReader(new InvalidOperationException("DCOM also dead")));

        MachineVitals v = await probe.GetVitalsAsync("DEADBOX");

        Assert.Equal(WinRmHealth.KerberosRejected, v.WinRmHealth);
        Assert.True(v.IsEmpty); // no readings — both paths failed

        VitalityResult r = VitalityScorer.Score(v, isOnline: true);
        Assert.True(r.NeedsAttention);
        Assert.Contains(r.Reasons, reason => reason.Contains("0x80090322"));
    }

    [Fact]
    public async Task Non_kerberos_winrm_failure_with_dcom_reader_returns_real_vitals_flagged_unavailable()
    {
        // A NON-Kerberos WinRM failure (the WinRM service isn't listening, or the session dropped): with a
        // DCOM reader wired in, GetVitalsAsync should still return the real DCOM vitals, flagged
        // WinRmUnavailable (NOT KerberosRejected) so the row shows numbers but stays visibly distinct.
        var realVitals = new MachineVitals(
            SystemDriveFreePercent: 70, MemoryUsedPercent: 40, CpuLoadPercent: 10, RebootPending: false);

        var probe = new VitalsProbe(
            new ThrowingHost(new RemoteSessionLostException("AZRBOX", new Exception("the remote session ended"))),
            new FakeDcomReader(realVitals));

        MachineVitals v = await probe.GetVitalsAsync("AZRBOX");

        Assert.Equal(WinRmHealth.WinRmUnavailable, v.WinRmHealth);
        Assert.Equal(70, v.SystemDriveFreePercent);
        Assert.False(v.IsEmpty);

        // The actual WinRM error is captured (the "what" the Connection callout shows).
        // The message no longer names the host (the grid row does); check the actionable wording.
        Assert.NotNull(v.WinRmFailureDetail);
        Assert.Contains("may have rebooted", v.WinRmFailureDetail);

        VitalityResult r = VitalityScorer.Score(v, isOnline: true);
        Assert.True(r.NeedsAttention);
        Assert.Contains(r.Reasons, reason => reason.Contains("WinRM", StringComparison.OrdinalIgnoreCase));
        // Must NOT be mislabeled as the Kerberos finding.
        Assert.DoesNotContain(r.Reasons, reason => reason.Contains("0x80090322"));
    }

    [Fact]
    public async Task Non_kerberos_winrm_failure_without_dcom_reader_still_throws()
    {
        // No DCOM reader: a non-Kerberos WinRM failure must still propagate (surfaced as "Vitals failed"
        // by the caller) exactly as before — the broad fallback only engages when a reader is available.
        var probe = new VitalsProbe(
            new ThrowingHost(new RemoteSessionLostException("AZRBOX", new Exception("the remote session ended"))));

        await Assert.ThrowsAsync<RemoteSessionLostException>(() => probe.GetVitalsAsync("AZRBOX"));
    }

    [Fact]
    public async Task Successful_read_is_flagged_winrm_healthy()
    {
        var probe = new VitalsProbe(new FakeHost(ResultFrom(VitalsObject(sysFreePct: 50))));

        MachineVitals v = await probe.GetVitalsAsync("localhost");

        Assert.Equal(WinRmHealth.Healthy, v.WinRmHealth);
    }

    // A DCOM reader that returns a pre-built vitals snapshot — used to verify the fallback routing path.
    private sealed class FakeDcomReader(MachineVitals vitals) : IDcomVitalsReader
    {
        public Task<MachineVitals> ReadAsync(string host, CancellationToken cancellationToken = default) =>
            Task.FromResult(vitals);
    }

    // A DCOM reader that always throws — used to verify the double-failure / graceful-fallback path.
    private sealed class ThrowingDcomReader(Exception toThrow) : IDcomVitalsReader
    {
        public Task<MachineVitals> ReadAsync(string host, CancellationToken cancellationToken = default) =>
            throw toThrow;
    }

    // A host whose every call throws — models the routing host rejecting Kerberos for a degraded box.
    private sealed class ThrowingHost(Exception toThrow) : IPowerShellHost
    {
        public Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken ct = default) => throw toThrow;

        public Task<PSExecutionResult> RunRemoteAsync(
            string host, string script, PSCredential? credential = null, int port = 5985, bool useSsl = false,
            CancellationToken ct = default) => throw toThrow;

        public Task<PSExecutionResult> RunRemoteStreamingAsync(
            string host, string script, Action<PSObject> onOutput, PSCredential? credential = null, int port = 5985,
            bool useSsl = false, CancellationToken ct = default) => throw toThrow;
    }
}
