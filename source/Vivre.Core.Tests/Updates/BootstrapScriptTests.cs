using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// Guards the SYSTEM scheduled-task settings the controller registers. -StartWhenAvailable must be
/// absent so a missed one-time ScheduleAt trigger can never silently re-fire the agent at next
/// boot (during offline servicing); the run-now path never had a trigger to miss either way.
/// </summary>
public class BootstrapScriptTests
{
    private static string Bootstrap(RunBehavior behavior, DateTime? at = null)
    {
        var options = new PatchOptions { RunBehavior = behavior, ScheduleAt = at };
        return WuaUpdateLane.BuildBootstrapScript(
            taskName: "Vivre_WUA_test",
            exePath: @"C:\ProgramData\Vivre\agent\Vivre_WUA_test.exe",
            configPath: @"C:\ProgramData\Vivre\agent\Vivre_WUA_test_config.json",
            progressPath: @"C:\ProgramData\Vivre\agent\Vivre_WUA_test_progress.json",
            base64Exe: "QUJD",
            base64Config: "e30=",
            expectedSha256: "0000000000000000000000000000000000000000000000000000000000000000",
            options: options);
    }

    [Fact]
    public void RunNow_task_settings_do_not_use_StartWhenAvailable()
    {
        string script = Bootstrap(RunBehavior.InstallNow);

        Assert.DoesNotContain("-StartWhenAvailable", script);
        Assert.Contains("-ExecutionTimeLimit", script);
    }

    [Fact]
    public void ScheduleAt_task_settings_do_not_use_StartWhenAvailable()
    {
        string script = Bootstrap(RunBehavior.ScheduleAt, DateTime.Today.AddDays(1).AddHours(1));

        Assert.DoesNotContain("-StartWhenAvailable", script);
        Assert.Contains("-ExecutionTimeLimit", script);
    }

    [Fact]
    public void Bootstrap_verifies_the_dropped_agent_hash_before_running_it_as_system()
    {
        string script = Bootstrap(RunBehavior.InstallNow);

        // The EXE runs as SYSTEM, so the bootstrap must hash-check it against the expected SHA-256
        // before launch.
        Assert.Contains("Get-FileHash", script);
        Assert.Contains("integrity check failed", script);
    }

    [Fact]
    public void Startup_failure_check_is_latched_behind_progress_seen()
    {
        string script = Bootstrap(RunBehavior.InstallNow);

        // The "Worker did not start writing progress" error may only fire while NO progress line has
        // ever been relayed. Once progress was seen, a vanished file means cleanup deleted it (a client
        // cancel) — the tail must exit quietly, never emit the startup-failure line. The unlatched check
        // mislabeled a cancelled 3-hour mid-run install as "did not start".
        Assert.Contains("$progressSeen = $false", script);
        Assert.Contains("$progressSeen = $true", script);

        int quietExit = script.IndexOf("if ($progressSeen)", StringComparison.Ordinal);
        int startupError = script.IndexOf("Worker did not start writing progress", StringComparison.Ordinal);
        Assert.True(quietExit >= 0, "the tail loop should branch on the latched progressSeen flag");
        Assert.True(startupError >= 0, "the genuine startup-failure message must remain");
        Assert.True(quietExit < startupError, "the quiet-exit (progress was seen) branch must gate the startup-failure emit");

        // Pin the shape, not just the ordering: the 2-minute check must be the ELSE branch of the
        // progressSeen gate, so it is structurally unreachable once progress was relayed.
        Assert.Contains("} elseif (((Get-Date) - $started) -gt [TimeSpan]::FromMinutes(2)) {", script);
    }

    [Theory]
    [InlineData(RunBehavior.InstallNow)]
    [InlineData(RunBehavior.ScheduleAt)]
    public void Bootstrap_parses_as_valid_powershell(RunBehavior behavior)
    {
        // dotnet build can't catch a syntax error inside the embedded controller script — it only fails
        // at runtime on the target. Lock parse-validity in here. (The PS7 parser; the 5.1 subset used by
        // the script is parse-compatible.)
        string script = Bootstrap(
            behavior,
            behavior == RunBehavior.ScheduleAt ? DateTime.Today.AddDays(1).AddHours(1) : null);

        System.Management.Automation.Language.Parser.ParseInput(
            script, out _, out System.Management.Automation.Language.ParseError[] errors);

        Assert.Empty(errors);
    }

    [Fact]
    public void Bootstrap_creates_and_hardens_the_drop_dir_before_writing_the_agent()
    {
        string script = Bootstrap(RunBehavior.InstallNow);

        // The drop dir must be created and ACL-hardened to SYSTEM (S-1-5-18) + Administrators
        // (S-1-5-32-544) with inheritance broken, so a non-privileged local user can't plant the
        // binary we run as SYSTEM. And it must happen BEFORE the EXE is written.
        int dirCreate = script.IndexOf("New-Item -ItemType Directory", StringComparison.Ordinal);
        int acl = script.IndexOf("SetAccessRuleProtection", StringComparison.Ordinal);
        int write = script.IndexOf("WriteAllBytes", StringComparison.Ordinal);

        Assert.True(dirCreate >= 0, "bootstrap should create the drop dir");
        Assert.True(acl >= 0, "bootstrap should harden the drop dir ACL");
        Assert.Contains("S-1-5-18", script);
        Assert.Contains("S-1-5-32-544", script);
        Assert.True(dirCreate < write && acl < write, "the dir must be created + hardened before the EXE is dropped");
    }
}
