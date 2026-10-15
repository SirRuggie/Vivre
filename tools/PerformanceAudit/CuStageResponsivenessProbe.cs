using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Threading;
using Vivre.Core.Updates;

namespace Vivre.PerformanceAudit;

internal static class CuStageResponsivenessProbe
{
    public static object Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "Vivre-cu-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "windows-kb9999999-x64.msu");
            using (var stream = File.Create(file)) stream.SetLength(128L * 1024 * 1024);
            string hash;
            using (var stream = File.OpenRead(file)) hash = Convert.ToHexString(SHA256.HashData(stream));
            var lane = new FullPackageLcuLane(new InertSmb());
            var target = new LcuTarget("KB9999999", ExpectedSha256: hash);
            // Prediction: synchronous package validation delays queued dispatcher input until hashing completes.
            // This measures actual StageAsync call entry, not network/servicing or a full visible UI.
            var samples = new List<object>();
            foreach (int count in new[] { 1, 8 })
            {
                var clock = Stopwatch.StartNew();
                double callbackMs = -1;
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Normal,
                    new Action(() => callbackMs = clock.Elapsed.TotalMilliseconds));
                var calls = new List<Task<HostPatchStatus>>();
                for (int i = 0; i < count; i++) calls.Add(lane.StageAsync("DUMMY-" + i, root, target,
                    new PatchOptions(), new SilentProgress(), CancellationToken.None));
                double synchronousLaunchMs = clock.Elapsed.TotalMilliseconds;
                Task all = Task.WhenAll(calls);
                while (!all.IsCompleted || callbackMs < 0)
                {
                    if (clock.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("CU probe timed out.");
                    var frame = new DispatcherFrame();
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame);
                    Thread.Sleep(1);
                }
                all.GetAwaiter().GetResult();
                if (calls.Any(c => c.Result.Phase != PatchPhase.PendingReboot)) throw new InvalidOperationException("Dummy stage result changed.");
                samples.Add(new { count, synchronousLaunchMs, queuedDispatcherCallbackMs = callbackMs, totalMs = clock.Elapsed.TotalMilliseconds });
            }
            return new { dummyPackageBytes = new FileInfo(file).Length, samples,
                scope = "Actual CU resolver and StageAsync with local dummy file and inert SMB; no server operations, no visible UI or live incident reproduction" };
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class SilentProgress : IProgress<HostPatchStatus> { public void Report(HostPatchStatus value) { } }
    private sealed class InertSmb : ISmbAgentLane
    {
        public Task<HostPatchStatus> InstallFullPackageAsync(string host, string path, PatchOptions options, IProgress<HostPatchStatus> progress, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(new HostPatchStatus(PatchPhase.PendingReboot, "dummy only")); }
        public Task<HostPatchStatus> ScanAsync(string host, PatchOptions options, CancellationToken token) => throw new NotSupportedException();
        public Task<HostPatchStatus> InstallAsync(string host, PatchOptions options, IProgress<HostPatchStatus> progress, CancellationToken token) => throw new NotSupportedException();
        public Task<HostPatchStatus> UninstallAsync(string host, PatchOptions options, IProgress<HostPatchStatus> progress, CancellationToken token) => throw new NotSupportedException();
        public Task<HostPatchStatus> RunComponentCleanupAsync(string host, PatchOptions options, IProgress<HostPatchStatus> progress, CancellationToken token) => throw new NotSupportedException();
    }
}
