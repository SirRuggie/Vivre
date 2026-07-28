using System.Collections.Concurrent;
using System.ComponentModel;
using Vivre.Core.Models;
using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Locks <see cref="BootTimeRefresh.RefreshAsync"/> — the monitor's offline→online "Last reboot" refresh.
/// Three properties matter and all three are easy to break by accident:
/// <list type="number">
///   <item>a good read REPLACES the old timestamp (the whole point — nothing on any reboot path used to
///     rewrite <see cref="Computer.LastBootTime"/> at all);</item>
///   <item>a failed read BLANKS it, rather than preserving it the way the vitals path does — a stale
///     timestamp on a box we just watched reboot is a lie, an empty cell is honest;</item>
///   <item>the write lands back on the caller's <see cref="SynchronizationContext"/>. Nothing asserts
///     this at runtime for <see cref="Computer.LastBootTime"/> (the DEBUG tripwire covers only the
///     live-filtered properties), so a stray <c>ConfigureAwait(false)</c> would fail silently in the app.
///     This file is the tripwire.</item>
/// </list>
/// Lives in Vivre.Core.Tests because that is the ONLY place it can: Vivre.Core.Tests is net10.0 and
/// cannot reference the net10.0-windows Vivre.Desktop, so the rule was extracted out of
/// <c>WorkspaceViewModel.MonitorRowAsync</c> into this Core helper precisely so it could be tested.
/// </summary>
public class BootTimeRefreshTests
{
    private static readonly DateTime FreshBoot = new(2026, 7, 27, 6, 15, 0, DateTimeKind.Unspecified);
    private static readonly DateTime StaleBoot = new(2026, 7, 1, 9, 0, 0, DateTimeKind.Unspecified);
    private static readonly BootTimeReading FreshReading = new(FreshBoot.AddHours(2), FreshBoot);

    // ── the refresh ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_successful_read_replaces_the_previous_boot_time()
    {
        var computer = new Computer("BOX01") { LastBootTime = StaleBoot };
        var reader = new StubReader(FreshReading);

        await BootTimeRefresh.RefreshAsync(reader, computer, CancellationToken.None);

        Assert.Equal(FreshBoot, computer.LastBootTime);
        Assert.Equal("BOX01", reader.LastHost);   // the row's own name is the target
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task A_null_read_BLANKS_the_boot_time_rather_than_preserving_it()
    {
        // THE operator verification item. The vitals path guards this assignment with `if (v.LastBootTime
        // is { } boot)` so a partial read can't wipe a known value; copying that guard here would leave the
        // pre-reboot timestamp standing on a box we have just watched go away and come back.
        var computer = new Computer("BOX01") { LastBootTime = StaleBoot };

        await BootTimeRefresh.RefreshAsync(new StubReader(null), computer, CancellationToken.None);

        Assert.Null(computer.LastBootTime);
    }

    [Fact]
    public async Task A_reader_that_throws_blanks_the_value_and_does_not_abort_the_caller()
    {
        // IBootTimeReader's contract is "any failure returns null", so a throw is a contract violation.
        // It must not escape into the monitor pass, and it must not leave a stale timestamp behind.
        var computer = new Computer("BOX01") { LastBootTime = StaleBoot };

        await BootTimeRefresh.RefreshAsync(new ThrowingReader(), computer, CancellationToken.None);

        Assert.Null(computer.LastBootTime);
    }

    [Fact]
    public async Task A_reader_that_throws_surfaces_the_error_instead_of_swallowing_it()
    {
        var computer = new Computer("BOX01") { LastBootTime = StaleBoot };
        var seen = new List<Exception>();

        await BootTimeRefresh.RefreshAsync(new ThrowingReader(), computer, CancellationToken.None, seen.Add);

        Exception surfaced = Assert.Single(seen);
        Assert.Equal("CIM blew up", surfaced.Message);
    }

    [Fact]
    public async Task Cancellation_propagates_and_leaves_the_previous_value_untouched()
    {
        // Monitoring was stopped mid-read: we learned nothing, so we must not blank anything. The monitor's
        // own OperationCanceledException handling (MonitorRowsAsync) ends the pass exactly as before.
        var computer = new Computer("BOX01") { LastBootTime = StaleBoot };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BootTimeRefresh.RefreshAsync(new StubReader(FreshReading), computer, cts.Token));

        Assert.Equal(StaleBoot, computer.LastBootTime);
    }

    [Fact]
    public async Task A_row_with_no_prior_value_still_gets_the_fresh_read()
    {
        var computer = new Computer("BOX01");

        await BootTimeRefresh.RefreshAsync(new StubReader(FreshReading), computer, CancellationToken.None);

        Assert.Equal(FreshBoot, computer.LastBootTime);
    }

    // ── thread affinity (the silent-failure tripwire) ─────────────────────────

    [Fact]
    public void The_write_lands_back_on_the_callers_synchronization_context()
    {
        // The monitor loop runs on the WPF UI context and its safety is IMPLICIT — it depends on never
        // losing that context across an await. Computer.LastBootTime is NOT covered by the DEBUG
        // off-thread assert, so an added ConfigureAwait(false) (or a detached Task.Run) would push this
        // write onto a pool thread and break the grid with no test and no assert firing. Here we install a
        // real pumped SynchronizationContext on a dedicated thread, let the reader complete OFF that
        // thread, and assert the property change is raised back ON it.
        var computer = new Computer("BOX01");
        int writeThreadId = 0;
        computer.PropertyChanged += (object? _, PropertyChangedEventArgs e) =>
        {
            if (e.PropertyName == nameof(Computer.LastBootTime))
            {
                writeThreadId = Environment.CurrentManagedThreadId;
            }
        };

        int pumpThreadId = 0;
        Exception? failure = null;
        var pump = new Thread(() =>
        {
            try
            {
                pumpThreadId = Environment.CurrentManagedThreadId;
                var context = new PumpedContext();
                SynchronizationContext.SetSynchronizationContext(context);

                Task refresh = BootTimeRefresh.RefreshAsync(
                    new OffThreadReader(FreshReading), computer, CancellationToken.None);
                context.PumpUntilComplete(refresh, TimeSpan.FromSeconds(15));

                Assert.True(refresh.IsCompleted, "the refresh never completed on the pumped context");
                refresh.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        { IsBackground = true };

        pump.Start();
        Assert.True(pump.Join(TimeSpan.FromSeconds(30)), "the pumped context thread hung");
        Assert.True(failure is null, failure?.ToString() ?? string.Empty);

        Assert.Equal(FreshBoot, computer.LastBootTime);
        Assert.NotEqual(0, writeThreadId);
        Assert.Equal(pumpThreadId, writeThreadId);
    }

    // ── fakes ─────────────────────────────────────────────────────────────────

    /// <summary>Returns a fixed reading (or null) and records what it was asked for. Completes
    /// synchronously — fine for the value tests, which don't care about the context.</summary>
    private sealed class StubReader(BootTimeReading? result) : IBootTimeReader
    {
        public string? LastHost { get; private set; }

        public int Reads { get; private set; }

        public Task<BootTimeReading?> ReadAsync(string host, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastHost = host;
            Reads++;
            return Task.FromResult(result);
        }
    }

    /// <summary>Violates the interface contract by throwing instead of returning null.</summary>
    private sealed class ThrowingReader : IBootTimeReader
    {
        public Task<BootTimeReading?> ReadAsync(string host, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("CIM blew up");
    }

    /// <summary>Mirrors DcomBootTimeReader: the read completes on a POOL thread, so the await genuinely
    /// suspends and the continuation can only run on the caller's thread if it was posted back there.</summary>
    private sealed class OffThreadReader(BootTimeReading? result) : IBootTimeReader
    {
        public Task<BootTimeReading?> ReadAsync(string host, CancellationToken cancellationToken) =>
            Task.Run(
                async () =>
                {
                    await Task.Delay(25, cancellationToken);
                    return result;
                },
                cancellationToken);
    }

    /// <summary>A minimal single-threaded SynchronizationContext: posted continuations queue up and run on
    /// whichever thread pumps them — the stand-in for the WPF Dispatcher the monitor really runs on.</summary>
    private sealed class PumpedContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public void PumpUntilComplete(Task task, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                if (_queue.TryTake(out (SendOrPostCallback Callback, object? State) work, millisecondsTimeout: 10))
                {
                    work.Callback(work.State);
                }
            }
        }
    }
}
