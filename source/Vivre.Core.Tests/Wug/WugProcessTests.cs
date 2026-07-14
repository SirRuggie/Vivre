using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vivre.Core.Wug;
using Xunit;

namespace Vivre.Core.Tests.Wug;

/// <summary>
/// Real <c>powershell.exe</c> (Windows PowerShell 5.1) process tests for the streaming state-read
/// protocol: incremental per-device delivery, the stall watchdog, cancel-kills-the-child on both the
/// shared launcher and the SET path (Amendment 3), and the PSModulePath strip.
///
/// <para>ALL of these live in this ONE class ON PURPOSE: xUnit runs tests within a single class
/// sequentially, and several of them mutate PROCESS-level environment variables (PSModulePath,
/// VIVRE_TEST_FILE) — keeping them in one class avoids them stepping on each other. Synthetic scripts
/// are ASCII-only; the launcher / RunCoreAsync write them UTF-8-with-BOM internally. Time bounds are
/// deliberately generous — these assert ordering/behavior, not speed.</para>
/// </summary>
public class WugProcessTests
{
    private static string PsExePath =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private static Dictionary<string, string> EmptyEnv() => new(StringComparer.Ordinal);

    // Fail with a clear message (not a confusing launch error) if 5.1 is genuinely absent. It is always
    // present on the dev box, so this only guards a truly broken environment — same File.Exists check the
    // product code does.
    private static void RequirePowerShell51()
        => Assert.True(File.Exists(PsExePath),
            $"Windows PowerShell 5.1 was not found at {PsExePath}. These process tests require it (it is always present on the dev box).");

    // Loops forever, appending to a file whose path arrives via env var, so a test can prove the child
    // stopped mutating after a cancel-kill. Emits NO JSON — the SET-path variant is killed mid-run.
    private const string LoopScript =
        "while ($true) { Add-Content -LiteralPath $env:VIVRE_TEST_FILE 'tick'; Start-Sleep -Milliseconds 200 }";

    [Fact]
    public async Task DeviceLine_streams_before_return_and_is_kept_out_of_the_summary_buffer()
    {
        RequirePowerShell51();

        // One device line, then a 3s gap, then the summary: the callback must fire well before the method
        // returns (proving per-line streaming while the process still runs), the device line must NOT be
        // in the returned stdout buffer, and the summary MUST be.
        const string script = """
            Write-Output '__WUGDEV__{"name":"A","matched":true,"inMaintenance":true}'
            Start-Sleep -Seconds 3
            Write-Output '__WUGRESULT__{"ok":true,"devices":[],"unmatched":[],"error":null}'
            """;

        var sw = Stopwatch.StartNew();
        var firstDevice = new TaskCompletionSource<(TimeSpan At, string Line)>(TaskCreationOptions.RunContinuationsAsynchronously);

        var (stdout, _) = await WugMaintenance.RunPreflightProcessAsync(
            script, EmptyEnv(), TimeSpan.FromSeconds(60), CancellationToken.None,
            onDeviceLine: line => firstDevice.TrySetResult((sw.Elapsed, line)));

        TimeSpan returnedAt = sw.Elapsed;

        Assert.True(firstDevice.Task.IsCompletedSuccessfully, "A device line should have streamed to the callback before the method returned.");
        (TimeSpan deviceAt, string deviceLine) = await firstDevice.Task;

        Assert.True(returnedAt - deviceAt >= TimeSpan.FromSeconds(1.5),
            $"Device callback should fire well before return: device at {deviceAt.TotalSeconds:F2}s, returned at {returnedAt.TotalSeconds:F2}s.");

        // The callback got the marker-stripped payload.
        Assert.Contains("\"name\":\"A\"", deviceLine);
        Assert.DoesNotContain("__WUGDEV__", deviceLine);

        // The device line is routed OUT of the summary buffer; the summary line stays.
        Assert.DoesNotContain("__WUGDEV__", stdout);
        Assert.Contains("__WUGRESULT__", stdout);
    }

    [Fact]
    public async Task StallWatchdog_kills_a_quiet_run_well_before_the_ceiling()
    {
        RequirePowerShell51();

        // One device line, then a 120s silence. A 2s stall window must abort long before that sleep — and
        // long before the 90s ceiling — with a WugStallException. The one streamed line must have arrived.
        const string script = """
            Write-Output '__WUGDEV__{"name":"A","matched":true,"inMaintenance":true}'
            Start-Sleep -Seconds 120
            Write-Output '__WUGRESULT__{"ok":true,"devices":[],"unmatched":[],"error":null}'
            """;

        var got = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sw = Stopwatch.StartNew();

        await Assert.ThrowsAsync<WugStallException>(async () =>
        {
            await WugMaintenance.RunPreflightProcessAsync(
                script, EmptyEnv(), TimeSpan.FromSeconds(90), CancellationToken.None,
                onDeviceLine: line => got.TrySetResult(line),
                stallTimeout: TimeSpan.FromSeconds(2));
        });

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(40), $"Stall abort took too long: {sw.Elapsed.TotalSeconds:F1}s.");
        Assert.True(got.Task.IsCompletedSuccessfully, "The streamed device line must have reached the callback before the stall fired.");
        string deviceLine = await got.Task;
        Assert.Contains("\"name\":\"A\"", deviceLine);
    }

    [Fact]
    public async Task Cancel_kills_the_child_shared_launcher()
    {
        RequirePowerShell51();

        string tickFile = Path.Combine(Path.GetTempPath(), $"Vivre_WugTick_{Guid.NewGuid():N}.txt");
        File.WriteAllText(tickFile, string.Empty);
        var env = new Dictionary<string, string>(StringComparer.Ordinal) { ["VIVRE_TEST_FILE"] = tickFile };

        try
        {
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(1.5));

            // Operator cancel must propagate an OperationCanceledException (TaskCanceledException derives
            // from it) AND kill the child — Amendment 3's cancel-kill contract on the shared launcher.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await WugMaintenance.RunPreflightProcessAsync(
                    LoopScript, env, TimeSpan.FromSeconds(90), cts.Token);
            });

            // The child should be dead: the file must stop growing. Proxy: length is stable across a wait.
            await Task.Delay(TimeSpan.FromSeconds(1));
            long len1 = new FileInfo(tickFile).Length;
            await Task.Delay(TimeSpan.FromSeconds(2));
            long len2 = new FileInfo(tickFile).Length;

            Assert.Equal(len1, len2);
        }
        finally
        {
            try { File.Delete(tickFile); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task Cancel_kills_the_child_set_path_via_RunCoreAsync()
    {
        RequirePowerShell51();

        // The Amendment-3 regression test on the SHIPPED set path: cancel → child killed → no further WUG
        // mutation. RunCoreAsync takes no env dict, so the loop file arrives via a process-level env var.
        string tickFile = Path.Combine(Path.GetTempPath(), $"Vivre_WugTickSet_{Guid.NewGuid():N}.txt");
        File.WriteAllText(tickFile, string.Empty);
        Environment.SetEnvironmentVariable("VIVRE_TEST_FILE", tickFile);

        try
        {
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(1.5));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await WugMaintenance.RunCoreAsync(
                    LoopScript,
                    new[] { "BOX1" },
                    enable: true,
                    server: "unused",
                    username: "u",
                    password: "p",
                    reason: "test",
                    timeout: TimeSpan.FromSeconds(90),
                    cancellationToken: cts.Token);
            });

            await Task.Delay(TimeSpan.FromSeconds(1));
            long len1 = new FileInfo(tickFile).Length;
            await Task.Delay(TimeSpan.FromSeconds(2));
            long len2 = new FileInfo(tickFile).Length;

            Assert.Equal(len1, len2);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VIVRE_TEST_FILE", null);
            try { File.Delete(tickFile); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task PSModulePath_is_stripped_for_the_child_shared_launcher()
    {
        RequirePowerShell51();

        // Poison this process's PSModulePath with a unique sentinel; the launcher's Remove("PSModulePath")
        // must mean the child rebuilds its NATIVE 5.1 path with no trace of the sentinel.
        string sentinel = "VIVRE_SENTINEL_" + Guid.NewGuid().ToString("N");
        string? original = Environment.GetEnvironmentVariable("PSModulePath");
        Environment.SetEnvironmentVariable("PSModulePath", (original ?? string.Empty) + ";C:\\" + sentinel);

        try
        {
            const string script = """
                Write-Output ("__WUGRESULT__" + (@{ psmp = "$env:PSModulePath" } | ConvertTo-Json -Compress))
                """;

            var (stdout, _) = await WugMaintenance.RunPreflightProcessAsync(
                script, EmptyEnv(), TimeSpan.FromSeconds(60), CancellationToken.None);

            Assert.Contains("__WUGRESULT__", stdout);        // sanity: we got the child's report
            Assert.DoesNotContain(sentinel, stdout);         // the strip held
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSModulePath", original);
        }
    }

    [Fact]
    public async Task PSModulePath_is_stripped_for_the_child_set_path_via_RunCoreAsync()
    {
        RequirePowerShell51();

        // Same strip, exercised through the SET path. RunCoreAsync's Parse reads the last braced line, so
        // the child reports its PSModulePath in the JSON "error" field where we can inspect it.
        string sentinel = "VIVRE_SENTINEL_" + Guid.NewGuid().ToString("N");
        string? original = Environment.GetEnvironmentVariable("PSModulePath");
        Environment.SetEnvironmentVariable("PSModulePath", (original ?? string.Empty) + ";C:\\" + sentinel);

        try
        {
            const string script = """
                $r = [ordered]@{ ok = $true; devicesSet = 0; unmatched = @(); error = "$env:PSModulePath" }
                $r | ConvertTo-Json -Compress
                """;

            WugMaintenanceResult r = await WugMaintenance.RunCoreAsync(
                script,
                new[] { "BOX1" },
                enable: false,
                server: "unused",
                username: "u",
                password: "p",
                reason: "test",
                timeout: TimeSpan.FromSeconds(60),
                cancellationToken: CancellationToken.None);

            Assert.NotNull(r.Error);                  // the child reported its PSModulePath here
            Assert.DoesNotContain(sentinel, r.Error!); // the strip held
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSModulePath", original);
        }
    }
}
