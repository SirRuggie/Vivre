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
            sysFreePct: 42.5, memUsedPct: 71, cpu: 18, stopped: 2, events: 4, rebootPending: true))));

        MachineVitals v = await probe.GetVitalsAsync("localhost");

        Assert.Equal(42.5, v.SystemDriveFreePercent);
        Assert.Equal(71, v.MemoryUsedPercent);
        Assert.Equal(18, v.CpuLoadPercent);
        Assert.Equal(2, v.StoppedAutoServiceCount);
        Assert.Equal(4, v.RecentErrorEventCount);
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
    public async Task Parses_the_recent_events_array()
    {
        var obj = VitalsObject(events: 1);
        var ev = new PSObject();
        ev.Properties.Add(new PSNoteProperty("Time", new DateTime(2026, 6, 1, 9, 0, 0)));
        ev.Properties.Add(new PSNoteProperty("Log", "System"));
        ev.Properties.Add(new PSNoteProperty("Provider", "Service Control Manager"));
        ev.Properties.Add(new PSNoteProperty("Id", 7001));
        ev.Properties.Add(new PSNoteProperty("Level", "Error"));
        ev.Properties.Add(new PSNoteProperty("Message", "The X service depends on Y which failed to start"));
        obj.Properties.Add(new PSNoteProperty("RecentErrorEvents", new object[] { ev }));

        var probe = new VitalsProbe(new FakeHost(ResultFrom(obj)));

        MachineVitals v = await probe.GetVitalsAsync("localhost");

        Assert.Single(v.RecentErrorEvents);
        Assert.Equal(7001, v.RecentErrorEvents[0].Id);
        Assert.Equal("System", v.RecentErrorEvents[0].Log);
        Assert.Equal("Error", v.RecentErrorEvents[0].Level);
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
        var obj = VitalsObject(stopped: 2, events: 1);
        obj.Properties.Add(new PSNoteProperty("Drives", new PSObject(new object[] { Drive("C:", 30, 60, 200) })));
        obj.Properties.Add(new PSNoteProperty("StoppedAutoServices", new PSObject(new object[] { "Print Spooler", "Windows Update" })));

        var ev = new PSObject();
        ev.Properties.Add(new PSNoteProperty("Id", 7001));
        ev.Properties.Add(new PSNoteProperty("Level", "Error"));
        obj.Properties.Add(new PSNoteProperty("RecentErrorEvents", new PSObject(new object[] { ev })));

        var probe = new VitalsProbe(new FakeHost(ResultFrom(obj)));

        MachineVitals v = await probe.GetVitalsAsync("NYC-FP1");

        Assert.Single(v.Drives);
        Assert.Equal("C:", v.Drives[0].Letter);
        Assert.Equal(new[] { "Print Spooler", "Windows Update" }, v.StoppedAutoServices);
        Assert.Single(v.RecentErrorEvents);
        Assert.Equal(7001, v.RecentErrorEvents[0].Id);
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
        Assert.Empty(v.RecentErrorEvents);
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
        int? events = 0,
        bool? rebootPending = false)
    {
        var o = new PSObject();
        o.Properties.Add(new PSNoteProperty("SystemDriveFreePercent", sysFreePct));
        o.Properties.Add(new PSNoteProperty("MemoryUsedPercent", memUsedPct));
        o.Properties.Add(new PSNoteProperty("CpuLoadPercent", cpu));
        o.Properties.Add(new PSNoteProperty("StoppedAutoServiceCount", stopped));
        o.Properties.Add(new PSNoteProperty("RecentErrorEventCount", events));
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
}
