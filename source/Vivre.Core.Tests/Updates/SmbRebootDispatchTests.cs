using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Vivre.Core.Logging;
using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

public class SmbRebootDispatchTests
{
    [Fact]
    public void Successful_start_is_accepted_and_cleanup_is_attempted()
    {
        var service = new FakeService();
        var trigger = Trigger(service, out _);

        SmbRebootStartOutcome result = trigger.RebootViaSmbScm("BOX", forced: false);

        Assert.Equal(SmbRebootStartOutcome.Accepted, result);
        Assert.Equal(1, service.StartCalls);
        Assert.Equal(1, service.DeleteCalls);
        Assert.Equal(1, service.DisposeCalls);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(2)]
    [InlineData(1069)]
    public void Definite_pre_execution_start_failure_throws_and_logs_its_code(int code)
    {
        var service = new FakeService { StartError = new Win32Exception(code, "start rejected") };
        var trigger = Trigger(service, out CapturingLog log);

        Win32Exception error = Assert.Throws<Win32Exception>(() => trigger.RebootViaSmbScm("BOX", forced: true));

        Assert.Equal(code, error.NativeErrorCode);
        Assert.Contains(log.Traces, line => line.Contains("dispatch failure") && line.Contains($"code={code}"));
        Assert.Equal(1, service.DeleteCalls);
    }

    [Theory]
    [InlineData(1053)]
    [InlineData(1722)]
    [InlineData(1726)]
    [InlineData(1727)]
    public void Ambiguous_start_error_is_uncertain_not_accepted_or_failed(int code)
    {
        var service = new FakeService { StartError = new Win32Exception(code, "ambiguous start") };
        var trigger = Trigger(service, out CapturingLog log);

        SmbRebootStartOutcome result = trigger.RebootViaSmbScm("BOX", forced: true);

        Assert.Equal(SmbRebootStartOutcome.Uncertain, result);
        Assert.Contains(log.Traces, line => line.Contains("dispatch uncertain") && line.Contains($"code={code}"));
    }

    [Fact]
    public void Unknown_non_win32_hresult_with_low_word_five_stays_uncertain()
    {
        var service = new FakeService { StartError = new FakeHResultException(unchecked((int)0x81230005)) };
        var trigger = Trigger(service, out _);

        Assert.Equal(SmbRebootStartOutcome.Uncertain, trigger.RebootViaSmbScm("BOX", forced: false));
    }

    [Fact]
    public void Hresult_from_win32_is_classified_by_its_win32_code()
    {
        var service = new FakeService { StartError = new COMException("denied", unchecked((int)0x80070005)) };
        var trigger = Trigger(service, out _);

        Assert.Throws<COMException>(() => trigger.RebootViaSmbScm("BOX", forced: false));
    }

    [Fact]
    public void Create_failure_is_logged_and_never_starts_a_service()
    {
        var log = new CapturingLog();
        var factory = new FakeFactory(new Win32Exception(5, "create denied"));
        var trigger = new DcomRebootTrigger(log, factory);

        Assert.Throws<Win32Exception>(() => trigger.RebootViaSmbScm("BOX", forced: true));
        Assert.Equal(1, factory.CreateCalls);
        Assert.Contains(log.Traces, line => line.Contains("CreateService") && line.Contains("code=5"));
    }

    [Theory]
    [InlineData(false, "/r /t 5")]
    [InlineData(true, "/r /f /t 5")]
    public void Factory_receives_the_same_host_and_force_switches_once(bool forced, string switches)
    {
        var service = new FakeService { StartError = new Win32Exception(1053) };
        var factory = new FakeFactory(service);
        var trigger = new DcomRebootTrigger(new CapturingLog(), factory);

        Assert.Equal(SmbRebootStartOutcome.Uncertain, trigger.RebootViaSmbScm("BOX", forced));
        Assert.Equal(1, factory.CreateCalls);
        Assert.Equal("BOX", factory.Host);
        Assert.Contains(switches, factory.BinPath, StringComparison.Ordinal);
        Assert.Equal(1, service.StartCalls);
    }

    [Fact]
    public void Shutdown_already_in_progress_is_not_flattened_to_uncertain()
    {
        var service = new FakeService { StartError = new Win32Exception(1115, "shutdown is already in progress") };
        var trigger = Trigger(service, out _);

        Win32Exception error = Assert.Throws<Win32Exception>(() => trigger.RebootViaSmbScm("BOX", forced: true));

        Assert.Equal(1115, error.NativeErrorCode);
    }

    [Fact]
    public void Cleanup_failure_is_logged_separately_without_changing_an_accepted_dispatch()
    {
        var service = new FakeService { DeleteError = new Win32Exception(5, "cleanup denied") };
        var trigger = Trigger(service, out CapturingLog log);

        Assert.Equal(SmbRebootStartOutcome.Accepted, trigger.RebootViaSmbScm("BOX", forced: false));
        Assert.Contains(log.Traces, line => line.Contains("cleanup failure") && line.Contains("code=5"));
        Assert.DoesNotContain(log.Traces, line => line.Contains("dispatch failure"));
    }

    [Fact]
    public void Uncertain_forced_escalation_stays_distinct_from_uncertain_requested_force()
    {
        Assert.Equal(RebootDispatch.EscalatedToForcedUncertain,
            DcomRebootTrigger.FallbackDispatch(requested: false, sentForced: true, SmbRebootStartOutcome.Uncertain));
        Assert.Equal(RebootDispatch.Uncertain,
            DcomRebootTrigger.FallbackDispatch(requested: true, sentForced: true, SmbRebootStartOutcome.Uncertain));
    }

    private static DcomRebootTrigger Trigger(FakeService service, out CapturingLog log)
    {
        log = new CapturingLog();
        return new DcomRebootTrigger(log, new FakeFactory(service));
    }

    private sealed class FakeFactory : ISmbRebootServiceFactory
    {
        private readonly FakeService? _service;
        private readonly Exception? _createError;
        public FakeFactory(FakeService service) => _service = service;
        public FakeFactory(Exception createError) => _createError = createError;
        public int CreateCalls { get; private set; }
        public string? Host { get; private set; }
        public string? BinPath { get; private set; }
        public ISmbRebootService Create(string host, string serviceName, string displayName, string binPath)
        {
            CreateCalls++;
            Host = host;
            BinPath = binPath;
            if (_createError is not null) throw _createError;
            return _service!;
        }
    }

    private sealed class FakeHResultException : Exception
    {
        public FakeHResultException(int hresult) => HResult = hresult;
    }

    private sealed class FakeService : ISmbRebootService
    {
        public Exception? StartError { get; init; }
        public Exception? DeleteError { get; init; }
        public int StartCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public void Start() { StartCalls++; if (StartError is not null) throw StartError; }
        public void Delete() { DeleteCalls++; if (DeleteError is not null) throw DeleteError; }
        public void Dispose() => DisposeCalls++;
    }

    private sealed class CapturingLog : IActivityLog
    {
        public ObservableCollection<LogEntry> Entries { get; } = [];
        public List<string> Traces { get; } = [];
        public void Info(string? machine, string message) { }
        public void Warn(string? machine, string message) { }
        public void Error(string? machine, string message) { }
        public void Trace(string? machine, string message) => Traces.Add(message);
        public void Clear() { }
    }
}
