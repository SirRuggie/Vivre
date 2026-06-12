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
