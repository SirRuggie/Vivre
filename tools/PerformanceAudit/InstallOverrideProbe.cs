using System.Reflection;
using Vivre.Core.Models;
using Vivre.Core.Updates;
using Vivre.Desktop.ViewModels;

namespace Vivre.PerformanceAudit;

/// <summary>Local-only probe of the real WorkspaceViewModel install option-clone boundary.</summary>
internal static class InstallOverrideProbe
{
    public static object Run()
    {
        using WorkspaceViewModel workspace = WorkspaceProbe.CreateWorkspace();
        IPatchService patch = DispatchProxy.Create<IPatchService, CapturingPatchProxy>();
        var capture = (CapturingPatchProxy)(object)patch;
        typeof(WorkspaceViewModel).GetField("_patch", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(workspace, patch);

        var row = new Computer("INERT-PROBE") { OsBuild = 20348 };
        MethodInfo installRow = typeof(WorkspaceViewModel).GetMethod(
            "InstallRowAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Invoke(installRow, workspace, row, scheduleAt: null, allow: true);
        Invoke(installRow, workspace, row, scheduleAt: null, allow: false);
        Invoke(installRow, workspace, row, scheduleAt: DateTime.Now.AddHours(1), allow: true);

        bool[] observed = capture.Options.Select(o => o.AllowPendingRebootInstall).ToArray();
        RunBehavior[] behaviors = capture.Options.Select(o => o.RunBehavior).ToArray();
        Require(observed.SequenceEqual([true, false, false]), "Approval leaked across install operations.");
        Require(behaviors.SequenceEqual([RunBehavior.InstallNow, RunBehavior.InstallNow, RunBehavior.ScheduleAt]),
            "Schedule boundary was not exercised.");

        return new
        {
            pass = true,
            observedApproval = observed,
            runBehavior = behaviors.Select(b => b.ToString()).ToArray(),
            scope = "Actual WorkspaceViewModel with inert IPatchService; no remoting, target I/O, credentials, or visible windows",
        };
    }

    private static void Invoke(MethodInfo method, WorkspaceViewModel workspace, Computer row, DateTime? scheduleAt, bool allow)
    {
        ((Task)method.Invoke(workspace, [row, CancellationToken.None, scheduleAt, false, allow])!)
            .GetAwaiter().GetResult();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public class CapturingPatchProxy : DispatchProxy
    {
        internal List<PatchOptions> Options { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IPatchService.InstallAsync))
                throw new NotSupportedException($"Unexpected probe call: {targetMethod?.Name}");

            Options.Add(((PatchOptions)args![1]!).Clone());
            return Task.FromResult(new HostPatchStatus(PatchPhase.Done, "Inert probe complete", InstalledCount: 1));
        }
    }
}
