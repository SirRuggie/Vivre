using System.Management.Automation;
using Vivre.Core.PowerShell;
using Vivre.Core.Remediation;
using Xunit;

namespace Vivre.Core.Tests.Remediation;

public class RemediationServiceTests
{
    [Fact]
    public async Task StartService_parses_ok_result()
    {
        var host = new FakeHost(ResultFrom(Obj(("ok", true), ("message", "Started 'Print Spooler' (now Running)"))));
        var svc = new RemediationService(host);

        RemediationResult r = await svc.StartServiceAsync("localhost", "Print Spooler");

        Assert.True(r.Ok);
        Assert.Contains("Print Spooler", r.Message);
    }

    [Fact]
    public async Task StartService_parses_failure()
    {
        var host = new FakeHost(ResultFrom(Obj(("ok", false), ("message", "Access is denied"))));
        var svc = new RemediationService(host);

        RemediationResult r = await svc.StartServiceAsync("NYC-FP1", "Some Service");

        Assert.False(r.Ok);
        Assert.Equal("Access is denied", r.Message);
    }

    [Fact]
    public async Task StartService_no_output_is_failure_with_error_detail()
    {
        var host = new FakeHost(new PSExecutionResult([], ["Connecting to remote server failed"], [], HadErrors: true));
        var svc = new RemediationService(host);

        RemediationResult r = await svc.StartServiceAsync("NYC-FP1", "Some Service");

        Assert.False(r.Ok);
        Assert.Contains("Connecting to remote server failed", r.Message);
    }

    [Fact]
    public async Task StartService_escapes_single_quotes_to_block_injection()
    {
        var host = new FakeHost(ResultFrom(Obj(("ok", true), ("message", "ok"))));
        var svc = new RemediationService(host);

        await svc.StartServiceAsync("localhost", "Bob's Service");

        // The display name must reach the script single-quote-escaped (doubled), inside a literal.
        Assert.Contains("'Bob''s Service'", host.LastScript);
    }

    [Fact]
    public async Task FreeDiskSpace_parses_reclaimed_and_percent()
    {
        var host = new FakeHost(ResultFrom(Obj(
            ("outcome", "Completed"), ("reclaimed", 1572864L), ("newFreePercent", 44.1), ("message", "Disk cleanup completed"))));
        var svc = new RemediationService(host);

        DiskCleanupResult r = await svc.FreeDiskSpaceAsync("localhost");

        Assert.True(r.Ok);
        Assert.Equal(DiskCleanupOutcome.Completed, r.Outcome);
        Assert.Equal(1572864L, r.ReclaimedBytes);
        Assert.Equal(44.1, r.NewFreePercent);
        Assert.Equal(CancellationToken.None, host.LastCancellationToken);
    }

    [Fact]
    public async Task FreeDiskSpace_preserves_partial_outcome_and_unavailable_measurement()
    {
        var host = new FakeHost(ResultFrom(Obj(
            ("outcome", "PartiallyCompleted"), ("reclaimed", null), ("newFreePercent", null),
            ("message", "Disk cleanup partially completed: recycle bin: access denied"))));

        DiskCleanupResult r = await new RemediationService(host).FreeDiskSpaceAsync("localhost");

        Assert.False(r.Ok);
        Assert.Equal(DiskCleanupOutcome.PartiallyCompleted, r.Outcome);
        Assert.Null(r.ReclaimedBytes);
        Assert.Null(r.NewFreePercent);
        Assert.Contains("access denied", r.Message);
    }

    [Fact]
    public async Task FreeDiskSpace_invalid_or_missing_output_is_failed_not_completed()
    {
        var invalid = new FakeHost(ResultFrom(Obj(("outcome", "Unexpected"), ("message", "bad shape"))));
        DiskCleanupResult invalidResult = await new RemediationService(invalid).FreeDiskSpaceAsync("localhost");
        Assert.Equal(DiskCleanupOutcome.Failed, invalidResult.Outcome);
        Assert.False(invalidResult.ExecutionConfirmed);

        var missing = new FakeHost(new PSExecutionResult([], ["transport failed"], [], HadErrors: true));
        DiskCleanupResult missingResult = await new RemediationService(missing).FreeDiskSpaceAsync("localhost");
        Assert.Equal(DiskCleanupOutcome.Failed, missingResult.Outcome);
        Assert.Null(missingResult.ReclaimedBytes);
        Assert.False(missingResult.ExecutionConfirmed);
        Assert.Contains("transport failed", missingResult.Message);
    }

    [Fact]
    public async Task FreeDiskSpace_engine_error_prevents_completed_claim()
    {
        var host = new FakeHost(new PSExecutionResult(
            [Obj(("outcome", "Completed"), ("reclaimed", 10L), ("message", "Disk cleanup completed"))],
            ["provider failed"], [], HadErrors: true));

        DiskCleanupResult result = await new RemediationService(host).FreeDiskSpaceAsync("localhost");

        Assert.Equal(DiskCleanupOutcome.PartiallyCompleted, result.Outcome);
        Assert.False(result.ExecutionConfirmed);
        Assert.Contains("provider failed", result.Message);
    }

    [Fact]
    public async Task FreeDiskSpace_preserves_engine_error_on_already_partial_result()
    {
        var host = new FakeHost(new PSExecutionResult(
            [Obj(("outcome", "PartiallyCompleted"), ("message", "Disk cleanup partially completed: recycle failed"))],
            ["provider also failed"], [], HadErrors: true));

        DiskCleanupResult result = await new RemediationService(host).FreeDiskSpaceAsync("localhost");

        Assert.Equal(DiskCleanupOutcome.PartiallyCompleted, result.Outcome);
        Assert.Contains("recycle failed", result.Message);
        Assert.Contains("provider also failed", result.Message);
    }

    [Fact]
    public async Task FreeDiskSpace_protects_built_in_and_caller_paths_and_escapes_literals()
    {
        var host = new FakeHost(ResultFrom(Obj(("outcome", "Completed"), ("message", "ok"))));

        await new RemediationService(host).FreeDiskSpaceAsync(
            "localhost", protectedPaths: [@"C:\Packages\Bob's App"]);

        Assert.Contains("Temp\\VivrePackages", host.LastScript);
        Assert.Contains("VivreStage_*.zip", host.LastScript);
        Assert.Contains("'C:\\Packages\\Bob''s App'", host.LastScript);
        Assert.Contains("$keep.StartsWith($full + '\\'", host.LastScript);
        Assert.Contains("ReparsePoint", host.LastScript);
    }

    [Fact]
    public async Task FreeDiskSpace_checks_cancellation_before_launch_then_runs_recovery_safe_script_uncancelled()
    {
        var host = new FakeHost(ResultFrom(Obj(("outcome", "Completed"), ("message", "ok"))));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new RemediationService(host).FreeDiskSpaceAsync("localhost", cancellationToken: cancelled.Token));

        Assert.False(host.LocalCalled);
    }

    [Fact]
    public void Cleanup_script_blocks_active_servicing_and_verifies_state_restoration()
    {
        string script = DiskCleanupScript.Build([]);

        Assert.Contains("'Vivre.UpdateAgent', 'dism', 'TiWorker'", script);
        Assert.Contains("active servicing operation", script);
        Assert.Contains("$wasRunning", script);
        Assert.Contains("Wait-ServiceState ([System.ServiceProcess.ServiceControllerStatus]::Running", script);
        Assert.Contains("Windows Update service recovery", script);
        Assert.DoesNotContain("-ErrorAction SilentlyContinue\n            Remove-Item", script);
        Assert.True(
            script.IndexOf("$serviceStoppedByCleanup = $true", StringComparison.Ordinal)
                < script.IndexOf("Stop-Service -Name wuauserv", StringComparison.Ordinal),
            "Recovery obligation must be recorded before attempting to stop wuauserv.");
        Assert.Contains("$code -eq 32 -or $code -eq 33", script);
        Assert.DoesNotContain("UnauthorizedAccessException", script);
    }

    [Fact]
    public void Cleanup_script_runs_only_in_disposable_fixture_and_preserves_nested_protected_path()
    {
        string root = Path.Combine(Path.GetTempPath(), $"VivreCleanupTest_{Guid.NewGuid():N}");
        string userTemp = Path.Combine(root, "user-temp");
        string windows = Path.Combine(root, "windows");
        string windowsTemp = Path.Combine(windows, "Temp");
        string protectedPackage = Path.Combine(userTemp, "parent", "VivrePackages", "active.msi");
        string removable = Path.Combine(userTemp, "remove-me.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(protectedPackage)!);
        Directory.CreateDirectory(windowsTemp);
        File.WriteAllText(protectedPackage, "keep");
        File.WriteAllText(removable, "delete");

        try
        {
            string prelude = $$"""
                $fixtureTemp = {{PsLiteral(userTemp)}}
                $fixtureWindows = {{PsLiteral(windows)}}
                $fixtureDrive = 'C:'
                $fixtureProgramData = {{PsLiteral(Path.Combine(root, "program-data"))}}
                function Get-Process { param([Parameter(ValueFromRemainingArguments=$true)] $rest) @() }
                function Get-Service { param([Parameter(ValueFromRemainingArguments=$true)] $rest) [pscustomobject]@{ Status = [System.ServiceProcess.ServiceControllerStatus]::Stopped } }
                function Clear-RecycleBin { param([Parameter(ValueFromRemainingArguments=$true)] $rest) }
                function Get-CimInstance { param([Parameter(ValueFromRemainingArguments=$true)] $rest) [pscustomobject]@{ FreeSpace = [int64]1000; Size = [int64]2000 } }
                """;
            string script = UseFixtureEnvironment(DiskCleanupScript.Build([protectedPackage], prelude));

            using var ps = System.Management.Automation.PowerShell.Create();
            IReadOnlyList<PSObject> output = ps.AddScript(script).Invoke();

            Assert.False(ps.HadErrors, string.Join(Environment.NewLine, ps.Streams.Error.Select(e => e.ToString())));
            PSObject row = Assert.Single(output);
            Assert.Equal("Completed", row.Properties["outcome"].Value?.ToString());
            Assert.True(File.Exists(protectedPackage));
            Assert.False(File.Exists(removable));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Cleanup_script_restores_service_that_fixture_reports_as_initially_running()
    {
        string root = Path.Combine(Path.GetTempPath(), $"VivreCleanupServiceTest_{Guid.NewGuid():N}");
        string windows = Path.Combine(root, "windows");
        Directory.CreateDirectory(Path.Combine(windows, "Temp"));
        Directory.CreateDirectory(Path.Combine(windows, "SoftwareDistribution", "Download"));

        try
        {
            string prelude = $$"""
                $fixtureTemp = {{PsLiteral(Path.Combine(root, "user-temp"))}}
                $fixtureWindows = {{PsLiteral(windows)}}
                $fixtureDrive = 'C:'
                $fixtureProgramData = {{PsLiteral(Path.Combine(root, "program-data"))}}
                $script:stopCount = 0
                $script:startCount = 0
                $script:fakeService = [pscustomobject]@{ Status = [System.ServiceProcess.ServiceControllerStatus]::Running }
                function Get-Process { param([Parameter(ValueFromRemainingArguments=$true)] $rest) @() }
                function Get-Service { param([Parameter(ValueFromRemainingArguments=$true)] $rest) $script:fakeService }
                function Stop-Service { param([Parameter(ValueFromRemainingArguments=$true)] $rest) $script:stopCount++; $script:fakeService.Status = [System.ServiceProcess.ServiceControllerStatus]::Stopped }
                function Start-Service { param([Parameter(ValueFromRemainingArguments=$true)] $rest) $script:startCount++; $script:fakeService.Status = [System.ServiceProcess.ServiceControllerStatus]::Running }
                function Clear-RecycleBin { param([Parameter(ValueFromRemainingArguments=$true)] $rest) }
                function Get-CimInstance { param([Parameter(ValueFromRemainingArguments=$true)] $rest) [pscustomobject]@{ FreeSpace = [int64]1000; Size = [int64]2000 } }
                """;
            string script = UseFixtureEnvironment(DiskCleanupScript.Build([], prelude)) + Environment.NewLine
                + "[PSCustomObject]@{ stopCount = $script:stopCount; startCount = $script:startCount; finalStatus = $script:fakeService.Status.ToString() }";

            using var ps = System.Management.Automation.PowerShell.Create();
            IReadOnlyList<PSObject> output = ps.AddScript(script).Invoke();

            Assert.False(ps.HadErrors, string.Join(Environment.NewLine, ps.Streams.Error.Select(e => e.ToString())));
            Assert.Equal("Completed", output[0].Properties["outcome"].Value?.ToString());
            Assert.Equal(1, Convert.ToInt32(output[1].Properties["stopCount"].Value));
            Assert.Equal(1, Convert.ToInt32(output[1].Properties["startCount"].Value));
            Assert.Equal("Running", output[1].Properties["finalStatus"].Value?.ToString());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Cleanup_script_marks_host_unsafe_when_fixture_service_recovery_fails()
    {
        string root = Path.Combine(Path.GetTempPath(), $"VivreCleanupRecoveryFail_{Guid.NewGuid():N}");
        string windows = Path.Combine(root, "windows");
        Directory.CreateDirectory(Path.Combine(root, "user-temp"));
        Directory.CreateDirectory(Path.Combine(windows, "Temp"));
        Directory.CreateDirectory(Path.Combine(windows, "SoftwareDistribution", "Download"));

        try
        {
            string prelude = $$"""
                $fixtureTemp = {{PsLiteral(Path.Combine(root, "user-temp"))}}
                $fixtureWindows = {{PsLiteral(windows)}}
                $fixtureDrive = 'C:'
                $fixtureProgramData = {{PsLiteral(Path.Combine(root, "program-data"))}}
                $script:fakeService = [pscustomobject]@{ Status = [System.ServiceProcess.ServiceControllerStatus]::Running }
                function Get-Process { param([Parameter(ValueFromRemainingArguments=$true)] $rest) @() }
                function Get-Service { param([Parameter(ValueFromRemainingArguments=$true)] $rest) $script:fakeService }
                function Stop-Service { param([Parameter(ValueFromRemainingArguments=$true)] $rest) $script:fakeService.Status = [System.ServiceProcess.ServiceControllerStatus]::Stopped }
                function Start-Service { param([Parameter(ValueFromRemainingArguments=$true)] $rest) throw 'fixture restart failure' }
                function Clear-RecycleBin { param([Parameter(ValueFromRemainingArguments=$true)] $rest) }
                function Get-CimInstance { param([Parameter(ValueFromRemainingArguments=$true)] $rest) [pscustomobject]@{ FreeSpace = [int64]1000; Size = [int64]2000 } }
                """;
            string script = UseFixtureEnvironment(DiskCleanupScript.Build([], prelude));

            using var ps = System.Management.Automation.PowerShell.Create();
            IReadOnlyList<PSObject> output = ps.AddScript(script).Invoke();

            PSObject row = Assert.Single(output);
            Assert.Equal("PartiallyCompleted", row.Properties["outcome"].Value?.ToString());
            Assert.False((bool)row.Properties["safeToReleaseHost"].Value);
            Assert.Contains("service recovery", row.Properties["message"].Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Cleanup_script_parses_and_runs_under_windows_powershell_51_fixture()
    {
        string root = Path.Combine(Path.GetTempPath(), $"VivreCleanupPs51_{Guid.NewGuid():N}");
        string windows = Path.Combine(root, "windows");
        string scriptPath = Path.Combine(root, "cleanup-fixture.ps1");
        Directory.CreateDirectory(Path.Combine(root, "user-temp"));
        Directory.CreateDirectory(Path.Combine(windows, "Temp"));
        Directory.CreateDirectory(Path.Combine(windows, "SoftwareDistribution", "Download"));

        try
        {
            string prelude = $$"""
                $fixtureTemp = {{PsLiteral(Path.Combine(root, "user-temp"))}}
                $fixtureWindows = {{PsLiteral(windows)}}
                $fixtureDrive = 'C:'
                $fixtureProgramData = {{PsLiteral(Path.Combine(root, "program-data"))}}
                function Get-Process { param([Parameter(ValueFromRemainingArguments=$true)] $rest) @() }
                function Get-Service { param([Parameter(ValueFromRemainingArguments=$true)] $rest) [pscustomobject]@{ Status = [System.ServiceProcess.ServiceControllerStatus]::Stopped } }
                function Clear-RecycleBin { param([Parameter(ValueFromRemainingArguments=$true)] $rest) }
                function Get-CimInstance { param([Parameter(ValueFromRemainingArguments=$true)] $rest) [pscustomobject]@{ FreeSpace = [int64]1000; Size = [int64]2000 } }
                """;
            string script = "& {" + Environment.NewLine
                + UseFixtureEnvironment(DiskCleanupScript.Build([], prelude))
                + Environment.NewLine + "} | ConvertTo-Json -Compress";
            File.WriteAllText(scriptPath, script, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var start = new System.Diagnostics.ProcessStartInfo(
                @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.Environment.Remove("PSModulePath");
            using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            bool exited = process.WaitForExit(30_000);
            if (!exited) process.Kill(entireProcessTree: true);
            Assert.True(exited, "Windows PowerShell 5.1 fixture timed out.");
            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("Completed", stdout);
            Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Cleanup_script_skips_sharing_violation_reports_access_denied_and_marks_measurement_unavailable()
    {
        string root = Path.Combine(Path.GetTempPath(), $"VivreCleanupErrors_{Guid.NewGuid():N}");
        string temp = Path.Combine(root, "temp");
        string windows = Path.Combine(root, "windows");
        Directory.CreateDirectory(temp);
        Directory.CreateDirectory(Path.Combine(windows, "Temp"));
        Directory.CreateDirectory(Path.Combine(windows, "SoftwareDistribution", "Download"));
        File.WriteAllText(Path.Combine(temp, "locked.tmp"), "x");
        File.WriteAllText(Path.Combine(temp, "denied.tmp"), "x");
        string removedPath = Path.Combine(temp, "removed.tmp");
        File.WriteAllText(removedPath, "x");
        try
        {
            string prelude = $$"""
                $fixtureTemp = {{PsLiteral(temp)}}
                $fixtureWindows = {{PsLiteral(windows)}}
                $fixtureDrive = 'C:'
                $fixtureProgramData = {{PsLiteral(Path.Combine(root, "program-data"))}}
                function Get-Process { param([Parameter(ValueFromRemainingArguments=$true)] $rest) @() }
                function Get-Service { param([Parameter(ValueFromRemainingArguments=$true)] $rest) throw 'fixture service query failed' }
                function Clear-RecycleBin { param([Parameter(ValueFromRemainingArguments=$true)] $rest) throw 'fixture recycle failed' }
                function Get-CimInstance { param([Parameter(ValueFromRemainingArguments=$true)] $rest) throw 'fixture measurement unavailable' }
                function Remove-Item {
                    param([string] $LiteralPath, [switch] $Force, [Parameter(ValueFromRemainingArguments=$true)] $rest)
                    if ([IO.Path]::GetFileName($LiteralPath) -eq 'denied.tmp') { throw [UnauthorizedAccessException]::new('access denied fixture') }
                    Microsoft.PowerShell.Management\Remove-Item -LiteralPath $LiteralPath -Force:$Force
                }
                """;
            string script = UseFixtureEnvironment(DiskCleanupScript.Build([], prelude));
            using var heldLock = new FileStream(Path.Combine(temp, "locked.tmp"), FileMode.Open, FileAccess.Read, FileShare.None);
            using var ps = System.Management.Automation.PowerShell.Create();
            PSObject row = Assert.Single(ps.AddScript(script).Invoke());

            Assert.Equal("PartiallyCompleted", row.Properties["outcome"].Value?.ToString());
            Assert.Null(row.Properties["reclaimed"].Value);
            string message = row.Properties["message"].Value?.ToString() ?? string.Empty;
            Assert.Contains("access denied fixture", message);
            Assert.Contains("skipped 1 locked item", message);
            Assert.Contains("reclaimed space unavailable", message);
            Assert.Contains("fixture recycle failed", message);
            Assert.Contains("fixture service query failed", message);
            Assert.False(File.Exists(removedPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Cleanup_script_restores_after_stop_succeeds_but_followup_service_query_fails()
    {
        string root = Path.Combine(Path.GetTempPath(), $"VivreCleanupStopQuery_{Guid.NewGuid():N}");
        string windows = Path.Combine(root, "windows");
        Directory.CreateDirectory(Path.Combine(root, "temp"));
        Directory.CreateDirectory(Path.Combine(windows, "Temp"));
        Directory.CreateDirectory(Path.Combine(windows, "SoftwareDistribution", "Download"));
        try
        {
            string prelude = $$"""
                $fixtureTemp = {{PsLiteral(Path.Combine(root, "temp"))}}
                $fixtureWindows = {{PsLiteral(windows)}}
                $fixtureDrive = 'C:'
                $fixtureProgramData = {{PsLiteral(Path.Combine(root, "program-data"))}}
                $script:state = [System.ServiceProcess.ServiceControllerStatus]::Running
                $script:queriesAfterStop = 0
                $script:startCount = 0
                function Get-Process { param([Parameter(ValueFromRemainingArguments=$true)] $rest) @() }
                function Get-Service {
                    param([Parameter(ValueFromRemainingArguments=$true)] $rest)
                    if ($script:state -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
                        $script:queriesAfterStop++
                        if ($script:queriesAfterStop -eq 1) { throw 'fixture post-stop query failure' }
                    }
                    [pscustomobject]@{ Status = $script:state }
                }
                function Stop-Service { param([Parameter(ValueFromRemainingArguments=$true)] $rest) $script:state = [System.ServiceProcess.ServiceControllerStatus]::Stopped }
                function Start-Service { param([Parameter(ValueFromRemainingArguments=$true)] $rest) $script:startCount++; $script:state = [System.ServiceProcess.ServiceControllerStatus]::Running }
                function Clear-RecycleBin { param([Parameter(ValueFromRemainingArguments=$true)] $rest) }
                function Get-CimInstance { param([Parameter(ValueFromRemainingArguments=$true)] $rest) [pscustomobject]@{ FreeSpace = [int64]1000; Size = [int64]2000 } }
                """;
            string script = UseFixtureEnvironment(DiskCleanupScript.Build([], prelude)) + Environment.NewLine
                + "[PSCustomObject]@{ startCount = $script:startCount; finalState = $script:state.ToString() }";
            using var ps = System.Management.Automation.PowerShell.Create();
            IReadOnlyList<PSObject> output = ps.AddScript(script).Invoke();

            Assert.Equal("PartiallyCompleted", output[0].Properties["outcome"].Value?.ToString());
            Assert.True((bool)output[0].Properties["safeToReleaseHost"].Value);
            Assert.Equal(1, Convert.ToInt32(output[1].Properties["startCount"].Value));
            Assert.Equal("Running", output[1].Properties["finalState"].Value?.ToString());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TopProcesses_parses_rows_in_order()
    {
        var host = new FakeHost(new PSExecutionResult(
            [
                Obj(("name", "chrome"), ("id", 1234), ("wsMb", 850.5), ("cpu", 120.0)),
                Obj(("name", "sqlservr"), ("id", 42), ("wsMb", 512.0), ("cpu", 9999.0)),
            ],
            [], [], HadErrors: false));
        var svc = new RemediationService(host);

        IReadOnlyList<ProcessInfo> procs = await svc.GetTopProcessesAsync("NYC-FP1");

        Assert.Equal(2, procs.Count);
        Assert.Equal("chrome", procs[0].Name);
        Assert.Equal(1234, procs[0].Id);
        Assert.Equal(850.5, procs[0].WorkingSetMb);
        Assert.Equal(120.0, procs[0].CpuSeconds);
        Assert.Equal("sqlservr", procs[1].Name);
    }

    [Fact]
    public async Task EndProcess_embeds_pid_and_parses_result()
    {
        var host = new FakeHost(ResultFrom(Obj(("ok", true), ("message", "Ended chrome (PID 1234)"))));
        var svc = new RemediationService(host);

        RemediationResult r = await svc.EndProcessAsync("localhost", 1234);

        Assert.True(r.Ok);
        Assert.Contains("Get-Process -Id 1234", host.LastScript);
    }

    [Fact]
    public async Task Local_host_runs_locally_remote_over_winrm()
    {
        var local = new FakeHost(ResultFrom(Obj(("ok", true), ("message", "ok"))));
        await new RemediationService(local).StartServiceAsync("localhost", "X");
        Assert.True(local.LocalCalled);
        Assert.False(local.RemoteCalled);

        var remote = new FakeHost(ResultFrom(Obj(("ok", true), ("message", "ok"))));
        await new RemediationService(remote).StartServiceAsync("NYC-FP1", "X");
        Assert.True(remote.RemoteCalled);
        Assert.False(remote.LocalCalled);
    }

    private static PSExecutionResult ResultFrom(PSObject row) => new([row], [], [], HadErrors: false);

    private static PSObject Obj(params (string Name, object? Value)[] properties)
    {
        var o = new PSObject();
        foreach ((string name, object? value) in properties)
        {
            o.Properties.Add(new PSNoteProperty(name, value));
        }

        return o;
    }

    private static string PsLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string UseFixtureEnvironment(string script) => script
        .Replace("$env:TEMP", "$fixtureTemp", StringComparison.Ordinal)
        .Replace("$env:SystemRoot", "$fixtureWindows", StringComparison.Ordinal)
        .Replace("$env:SystemDrive", "$fixtureDrive", StringComparison.Ordinal)
        .Replace("$env:ProgramData", "$fixtureProgramData", StringComparison.Ordinal);

    private sealed class FakeHost : IPowerShellHost
    {
        private readonly PSExecutionResult _result;

        public FakeHost(PSExecutionResult result) => _result = result;

        public bool LocalCalled { get; private set; }

        public bool RemoteCalled { get; private set; }

        public string LastScript { get; private set; } = string.Empty;

        public CancellationToken LastCancellationToken { get; private set; }

        public Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken cancellationToken = default)
        {
            LocalCalled = true;
            LastScript = script;
            LastCancellationToken = cancellationToken;
            return Task.FromResult(_result);
        }

        public Task<PSExecutionResult> RunRemoteAsync(
            string host, string script, PSCredential? credential = null, int port = 5985, bool useSsl = false,
            CancellationToken cancellationToken = default, bool background = false)
        {
            RemoteCalled = true;
            LastScript = script;
            LastCancellationToken = cancellationToken;
            return Task.FromResult(_result);
        }

        public Task<PSExecutionResult> RunRemoteStreamingAsync(
            string host, string script, Action<PSObject> onOutput, PSCredential? credential = null, int port = 5985,
            bool useSsl = false, CancellationToken cancellationToken = default, bool background = false) =>
            throw new NotSupportedException("Remediation tests don't exercise the streaming path.");
    }
}
