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
            exePath: @"C:\Windows\Temp\Vivre_WUA_test.exe",
            configPath: @"C:\Windows\Temp\Vivre_WUA_test_config.json",
            progressPath: @"C:\Windows\Temp\Vivre_WUA_test_progress.json",
            base64Exe: "QUJD",
            base64Config: "e30=",
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
}
