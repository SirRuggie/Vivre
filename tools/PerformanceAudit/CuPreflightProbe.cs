using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using Vivre.Core.Configuration;
using Vivre.Core.Updates;
using Vivre.Desktop.ViewModels;

namespace Vivre.PerformanceAudit;

/// <summary>
/// Exercises the real WorkspaceViewModel CU preflight operation boundary with a deliberately blocked,
/// local-only package check. This verifies dispatcher liveness and Stop cancellation without touching a
/// package share or host. Program intentionally owns whether/how this probe is exposed on the command line.
/// </summary>
internal static class CuPreflightProbe
{
    public static object Run()
    {
        string settingsRoot = Path.Combine(Path.GetTempPath(), "Vivre-cu-preflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsRoot);
        var blocked = DispatchProxy.Create<IPatchService, BlockingPatchProxy>();
        var proxy = (BlockingPatchProxy)(object)blocked;

        try
        {
            File.WriteAllText(Path.Combine(settingsRoot, "settings.json"), JsonSerializer.Serialize(new SharedSettings
            {
                LcuPackagesFolder = @"X:\inert-cu-share",
                MonthlyCu = new MonthlyCu { Kb = "KB9999999", Arch = "x64", TargetUbr = 9999 },
            }));

            using WorkspaceViewModel workspace = CreateWorkspaceWith(blocked, new SharedSettingsStore(settingsRoot));
            var clock = Stopwatch.StartNew();
            double dispatcherCallbackMs = -1;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Normal,
                new Action(() => dispatcherCallbackMs = clock.Elapsed.TotalMilliseconds));

            Task<LcuStageReadiness> preflight = workspace.CheckLcuStageReadinessAsync();
            Require(PumpUntil(() => proxy.Entered.IsSet && dispatcherCallbackMs >= 0, TimeSpan.FromSeconds(5)),
                "The package check did not enter or the dispatcher callback did not run.");
            Require(workspace.IsBusy, "CU preflight was not registered as an active operation.");
            Require(workspace.StopCommand.CanExecute(null), "Stop was disabled during CU preflight.");

            double stopIssuedMs = clock.Elapsed.TotalMilliseconds;
            workspace.StopCommand.Execute(null);
            Require(PumpUntil(() => preflight.IsCompleted, TimeSpan.FromSeconds(2)),
                "Stop did not complete the CU preflight await promptly.");
            double stopCompletedMs = clock.Elapsed.TotalMilliseconds;
            Require(preflight.IsCanceled, $"Stopped CU preflight ended as {preflight.Status}, not Canceled.");
            Require(!workspace.IsBusy, "Stopped CU preflight left WorkspaceViewModel busy.");

            Task<LcuStageReadiness> retry = workspace.CheckLcuStageReadinessAsync();
            var retryClock = Stopwatch.StartNew();
            PumpUntil(() => retryClock.ElapsedMilliseconds >= 150, TimeSpan.FromSeconds(2));
            Require(proxy.CallCount == 1, "Retry accumulated another blocked package reader.");
            workspace.StopCommand.Execute(null);
            Require(PumpUntil(() => retry.IsCompleted, TimeSpan.FromSeconds(2)) && retry.IsCanceled,
                "Queued retry did not honor Stop.");

            return new
            {
                dispatcherCallbackMs,
                stopCompletionMs = stopCompletedMs - stopIssuedMs,
                preflightStatus = preflight.Status.ToString(),
                busyCleared = !workspace.IsBusy,
                cancelledRetryDoesNotAccumulateReaders = true,
                scope = "Actual WorkspaceViewModel operation tracking with an injected blocked local package check; no SMB, remoting, hosts, production settings, or visible windows",
            };
        }
        finally
        {
            proxy.Release.Set();
            if (proxy.Entered.IsSet && !proxy.Finished.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Dummy worker did not stop.");
            proxy.Entered.Dispose(); proxy.Release.Dispose(); proxy.Finished.Dispose();
            Directory.Delete(settingsRoot, recursive: true);
        }
    }

    private static WorkspaceViewModel CreateWorkspaceWith(IPatchService patch, SharedSettingsStore shared)
    {
        WorkspaceViewModel workspace = WorkspaceProbe.CreateWorkspace();
        typeof(WorkspaceViewModel).GetField("_patch", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(workspace, patch);
        typeof(WorkspaceViewModel).GetField("_shared", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(workspace, shared);
        return workspace;
    }

    private static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < timeout)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(1);
        }
        return condition();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public class BlockingPatchProxy : DispatchProxy
    {
        private int _callCount;
        internal int CallCount => Volatile.Read(ref _callCount);
        internal ManualResetEventSlim Finished { get; } = new(false);
        internal ManualResetEventSlim Entered { get; } = new(false);
        internal ManualResetEventSlim Release { get; } = new(false);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IPatchService.CheckLcuPackage))
                throw new NotSupportedException($"Unexpected probe call: {targetMethod?.Name}");

            Interlocked.Increment(ref _callCount);
            Entered.Set();
            try
            {
                if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Dummy check not released.");
                return new LcuPackageResolution(LcuPackageStatus.Missing, null, "inert probe", "https://example.invalid");
            }
            finally { Finished.Set(); }
        }
    }
}
