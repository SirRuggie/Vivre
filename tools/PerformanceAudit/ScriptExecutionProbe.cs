using System.Collections.ObjectModel;
using System.IO;
using System.Management.Automation;
using Vivre.Core.Credentials;
using Vivre.Core.Logging;
using Vivre.Core.Models;
using Vivre.Core.Operations;
using Vivre.Core.PowerShell;
using Vivre.Core.Scripts;
using Vivre.Desktop.ViewModels;

namespace Vivre.PerformanceAudit;

/// <summary>Isolated execution-contract checks. Uses temporary scripts and a fake transport only.</summary>
internal static class ScriptExecutionProbe
{
    public static object Run() => Task.Run(RunAsync).GetAwaiter().GetResult();

    private static async Task<object> RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "Vivre-script-execution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var library = CreateLibrary(root, out ScriptFile customScript);
            var activity = new ProbeLog();

            var conflictGate = new HostOperationCoordinator();
            using HostOperationCoordinator.HostOperationClaim held = conflictGate.TryBeginOperation("BLOCKED", "Patch install");
            Require(held.Acquired, "Probe could not establish same-host conflict.");
            var conflictHost = new ProbePowerShellHost();
            var conflictVm = CreateVm([new Computer("BLOCKED"), new Computer("OTHER")], conflictHost,
                library, customScript, activity, conflictGate);
            await conflictVm.RunCommand.ExecuteAsync("Set-Service dummy");
            Require(!conflictHost.RemoteHosts.Contains("BLOCKED", StringComparer.OrdinalIgnoreCase),
                "Same-host coordinator conflict reached transport execution.");
            Require(conflictHost.RemoteHosts.Contains("OTHER", StringComparer.OrdinalIgnoreCase),
                "A conflict on one host blocked an independent host.");

            var errorGate = new HostOperationCoordinator();
            var errorHost = new ProbePowerShellHost { ErrorHost = "ERROR" };
            var errorVm = CreateVm([new Computer("ERROR")], errorHost, library, customScript, activity, errorGate);
            await errorVm.RunCommand.ExecuteAsync("Set-Service dummy");
            using (HostOperationCoordinator.HostOperationClaim afterError = errorGate.TryBeginOperation("ERROR", "Follow-up"))
                Require(!afterError.Acquired, "Mutating script errors released uncertain host ownership.");

            var cancelGate = new HostOperationCoordinator();
            var cancelHost = new ProbePowerShellHost { BlockingHost = "CANCEL" };
            var cancelVm = CreateVm([new Computer("CANCEL")], cancelHost, library, customScript, activity, cancelGate);
            Task cancelRun = cancelVm.RunCommand.ExecuteAsync("Set-Service dummy");
            await cancelHost.BlockingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelVm.RunCancelCommand.Execute(null);
            await cancelRun.WaitAsync(TimeSpan.FromSeconds(5));
            using (HostOperationCoordinator.HostOperationClaim afterCancel = cancelGate.TryBeginCleanup("CANCEL", "Follow-up"))
                Require(!afterCancel.Acquired, "Cancelled mutating script released uncertain host ownership.");

            var capturedTargets = Enumerable.Range(0, 6).Select(i => new Computer($"REMOTE-{i}")).ToList();
            capturedTargets.Add(new Computer("."));
            var dispatchHost = new ProbePowerShellHost { LargeOutputAll = true };
            var dispatchVm = CreateVm(capturedTargets, dispatchHost, library, customScript, activity,
                new HostOperationCoordinator());
            capturedTargets.Add(new Computer("ADDED-LATER"));
            await dispatchVm.RunCommand.ExecuteAsync("Set-Service dummy");
            Require(dispatchHost.RemoteHosts.SequenceEqual(Enumerable.Range(0, 6).Select(i => $"REMOTE-{i}"), StringComparer.OrdinalIgnoreCase),
                "Remote capture dispatch or captured target set changed.");
            Require(dispatchHost.LocalCalls == 1, "Local target did not use bounded local capture exactly once.");
            Require(!dispatchHost.RemoteHosts.Contains("ADDED-LATER", StringComparer.OrdinalIgnoreCase),
                "Target added after view-model construction was executed.");
            Require(dispatchVm.Output.Length <= PSScriptCapturePresentation.AggregateMaximumCharacters,
                "Combined script output exceeded its aggregate presentation bound.");
            Require(dispatchVm.Output.Contains("output truncated", StringComparison.OrdinalIgnoreCase),
                "Bounded capture truncation was not disclosed.");
            Require(capturedTargets.Take(6).All(t => (t.CommandResult?.Length ?? 0) <= PSScriptCapturePresentation.PerHostMaximumCharacters),
                "A per-host script result exceeded its presentation bound.");

            var unsupportedGate = new HostOperationCoordinator();
            var unsupportedTarget = new Computer("UNSUPPORTED");
            var unsupportedVm = new ScriptRunnerViewModel([unsupportedTarget], new UnsupportedPowerShellHost(), library,
                new CredentialStore(), activity, unsupportedGate) { SelectedScript = customScript };
            await unsupportedVm.RunCommand.ExecuteAsync("Set-Service dummy");
            Require(unsupportedTarget.CommandResult?.Contains("FAILED", StringComparison.OrdinalIgnoreCase) == true,
                "Unsupported bounded capture was presented as success.");
            using (HostOperationCoordinator.HostOperationClaim afterUnsupported = unsupportedGate.TryBeginOperation("UNSUPPORTED", "Follow-up"))
                Require(!afterUnsupported.Acquired, "Unsupported mutating dispatch released uncertain host ownership.");

            return new
            {
                sameHostConflictBlocked = true,
                otherHostContinued = true,
                mutatingErrorRetainedUncertainGuard = true,
                mutatingCancellationRetainedUncertainGuard = true,
                localAndRemoteBoundedCaptureDispatch = true,
                targetSetCaptured = true,
                aggregateOutputBounded = true,
                unsupportedCaptureFailsClosed = true,
                scope = "Temporary ScriptLibrary, dummy computers and fake IPowerShellHost; no production paths or operations",
            };
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static ScriptLibrary CreateLibrary(string root, out ScriptFile customScript)
    {
        string live = Path.Combine(root, "live");
        string bundle = Path.Combine(root, "bundle");
        Directory.CreateDirectory(bundle);
        var library = new ScriptLibrary(live, bundle);
        customScript = library.Save("Probe mutation", "Set-Service dummy", "Probe");
        return library;
    }

    private static ScriptRunnerViewModel CreateVm(IReadOnlyList<Computer> targets, ProbePowerShellHost host,
        ScriptLibrary library, ScriptFile selected, IActivityLog activity, HostOperationCoordinator operations)
    {
        var vm = new ScriptRunnerViewModel(targets, host, library, new CredentialStore(), activity, operations)
        {
            SelectedScript = selected,
        };
        return vm;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ProbePowerShellHost : IPowerShellHost
    {
        public List<string> RemoteHosts { get; } = [];
        public int LocalCalls { get; private set; }
        public string? ErrorHost { get; init; }
        public string? BlockingHost { get; init; }
        public bool LargeOutputAll { get; init; }
        public TaskCompletionSource BlockingEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Script runner bypassed bounded local capture.");
        public Task<PSExecutionResult> RunRemoteAsync(string host, string script, PSCredential? credential = null,
            int port = 5985, bool useSsl = false, CancellationToken cancellationToken = default, bool background = false) =>
            throw new InvalidOperationException("Script runner bypassed bounded remote capture.");
        public Task<PSExecutionResult> RunRemoteStreamingAsync(string host, string script, Action<PSObject> onOutput,
            PSCredential? credential = null, int port = 5985, bool useSsl = false,
            CancellationToken cancellationToken = default, bool background = false) =>
            throw new InvalidOperationException("Script runner used streaming transport.");

        public Task<PSScriptCaptureResult> RunLocalScriptCaptureAsync(string script, PSScriptCaptureLimits limits,
            CancellationToken cancellationToken = default)
        {
            LocalCalls++;
            return Task.FromResult(Success());
        }

        public async Task<PSScriptCaptureResult> RunRemoteScriptCaptureAsync(string host, string script,
            PSScriptCaptureLimits limits, PSCredential? credential = null, int port = 5985, bool useSsl = false,
            CancellationToken cancellationToken = default)
        {
            RemoteHosts.Add(host);
            if (string.Equals(host, BlockingHost, StringComparison.OrdinalIgnoreCase))
            {
                BlockingEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            if (string.Equals(host, ErrorHost, StringComparison.OrdinalIgnoreCase))
                return new([], ["simulated error"], [], [], [], [], [], HadErrors: true, WasTruncated: false);
            if (LargeOutputAll)
                return new([new string('x', PSScriptCapturePresentation.PerHostMaximumCharacters)], [], [], [], [], [], [],
                    HadErrors: false, WasTruncated: true);
            return Success();
        }

        private static PSScriptCaptureResult Success() =>
            new(["ok"], [], [], [], [], [], [], HadErrors: false, WasTruncated: false);
    }

    private sealed class UnsupportedPowerShellHost : IPowerShellHost
    {
        public Task<PSExecutionResult> RunLocalAsync(string script, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("unbounded execution intentionally unsupported");
        public Task<PSExecutionResult> RunRemoteAsync(string host, string script, PSCredential? credential = null,
            int port = 5985, bool useSsl = false, CancellationToken cancellationToken = default, bool background = false) =>
            throw new NotSupportedException("unbounded execution intentionally unsupported");
        public Task<PSExecutionResult> RunRemoteStreamingAsync(string host, string script, Action<PSObject> onOutput,
            PSCredential? credential = null, int port = 5985, bool useSsl = false,
            CancellationToken cancellationToken = default, bool background = false) =>
            throw new NotSupportedException("streaming intentionally unsupported");
    }

    private sealed class ProbeLog : IActivityLog
    {
        public ObservableCollection<LogEntry> Entries { get; } = [];
        public void Info(string? machine, string message) { }
        public void Warn(string? machine, string message) { }
        public void Error(string? machine, string message) { }
        public void Clear() { }
    }
}
