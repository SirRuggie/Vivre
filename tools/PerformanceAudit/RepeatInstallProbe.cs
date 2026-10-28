using System.Reflection;
using Vivre.Core.Models;
using Vivre.Core.Updates;
using Vivre.Core.Vitals;
using Vivre.Desktop.ViewModels;

namespace Vivre.PerformanceAudit;

/// <summary>Real scan/install/vitals paths with inert patch responses; never contacts a target.</summary>
internal static class RepeatInstallProbe
{
    private static readonly MethodInfo Scan = Method("ScanRowAsync");
    private static readonly MethodInfo Install = Method("InstallRowAsync");
    private static readonly MethodInfo Vitals = Method("ApplyVitals");
    private static readonly MethodInfo Replace = typeof(WorkspaceViewModel).GetMethod(
        "ReplaceUpdatesForScope", BindingFlags.Static | BindingFlags.NonPublic)!;

    public static object Run()
    {
        using WorkspaceViewModel workspace = WorkspaceProbe.CreateWorkspace();
        IPatchService patch = DispatchProxy.Create<IPatchService, PatchProxy>();
        var capture = (PatchProxy)(object)patch;
        typeof(WorkspaceViewModel).GetField("_patch", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(workspace, patch);
        var row = new Computer("INERT-REPEAT") { OsBuild = 20348, IsOnline = true };
        SoftwareUpdate first = Update("2267602", "Definition revision 1");
        SoftwareUpdate skipped = Update("9990001", "Deliberately skipped");
        capture.Updates = [first, skipped];
        RunScan(workspace, row);
        row.ApplicableUpdates[1].IsSelected = false;
        RunInstall(workspace, row);
        Require(capture.Installs.Count == 1 && capture.Installs[0].IncludeKbArticleIds?.SequenceEqual(["2267602"]) == true,
            "Initial install did not honor the checklist.");
        Require(row.ApplicableUpdates[0].InstalledThisSession && !row.ApplicableUpdates[0].IsSelected,
            "Successful install must untick its completed selection.");
        RunInstall(workspace, row);
        Require(capture.Installs.Count == 1 && row.UpdateMessage == "No updates selected",
            "Install again without a fresh scan must not silently reselect old results.");

        // Same KB (e.g. a newer definition revision) is still applicable after an explicit fresh scan.
        capture.Updates = [first with { Title = "Definition revision 2" }, skipped, Update("9990002", "New update")];
        RunScan(workspace, row);
        Require(row.ApplicableUpdates[0].IsSelected && !row.ApplicableUpdates[0].InstalledThisSession,
            "Fresh applicability inherited the automatic post-install deselection.");
        Require(!row.ApplicableUpdates[1].IsSelected && row.ApplicableUpdates[2].IsSelected,
            "Fresh scan lost the user's opt-out or failed to select a new update.");
        RunScan(workspace, row);
        RunInstall(workspace, row);
        Require(capture.Installs.Count == 2
            && capture.Installs[1].IncludeKbArticleIds?.SequenceEqual(["2267602", "9990002"]) == true
            && capture.Installs[1].AllowPendingRebootInstall,
            "Repeat Install anyway did not dispatch exactly the fresh selected KBs.");

        // A fresh scan of the exact same update also supersedes session-only completion metadata.
        RunScan(workspace, row);
        Require(row.ApplicableUpdates[0].IsSelected && row.ApplicableUpdates[2].IsSelected,
            "Same-title fresh applicability stayed unchecked.");
        foreach (var item in row.ApplicableUpdates) item.IsSelected = false;
        RunScan(workspace, row);
        RunInstall(workspace, row);
        Require(capture.Installs.Count == 2 && row.ApplicableUpdates.All(u => !u.IsSelected),
            "Explicitly unchecking everything was not preserved.");

        // Failed installs do not invent completion markers or reset the operator's skipped selection.
        row.ApplicableUpdates[0].IsSelected = true;
        capture.InstallResult = new HostPatchStatus(PatchPhase.Done, "One failure", FailedCount: 1);
        RunInstall(workspace, row);
        RunScan(workspace, row);
        Require(row.ApplicableUpdates[0].IsSelected && !row.ApplicableUpdates[0].InstalledThisSession
            && !row.ApplicableUpdates[1].IsSelected, "Failed-install rescan changed selection intent.");

        // Installed scope stays opt-in and still refuses to preselect non-removable updates.
        row.InstalledUpdates.Add(new SelectableUpdate(first, true) { InstalledThisSession = true });
        Replace.Invoke(null, [row, UpdateScope.Installed, new[] { first, skipped }]);
        Require(row.InstalledUpdates[0].IsSelected && !row.InstalledUpdates[1].IsSelected,
            "Installed-scope opt-in selection changed.");
        Replace.Invoke(null, [row, UpdateScope.Installed, new[] { first with { IsUninstallable = false } }]);
        Require(!row.InstalledUpdates[0].IsSelected, "Non-removable update became selected.");

        var builds = new List<int>();
        foreach (var (version, expected) in new[] { ("6.2.9200", 9200), ("6.3.9600", 9600), ("10.0.20348", 20348) })
        {
            var legacy = new Computer("INERT-OS") { IsOnline = true };
            Vitals.Invoke(workspace, [legacy, new MachineVitals(CpuLoadPercent: 1) { OperatingSystem = "Windows Server — " + version }]);
            Require(legacy.OsBuild == expected, "Vitals did not classify " + version);
            // Partial vitals must preserve the known build.
            Vitals.Invoke(workspace, [legacy, new MachineVitals(CpuLoadPercent: 1)]);
            Require(legacy.OsBuild == expected, "Partial vitals cleared the known build.");
            int before = capture.Installs.Count;
            RunInstall(workspace, legacy);
            Require(capture.Installs.Count == before + 1, "Known non-2016 build did not reach WUA.");
            builds.Add(expected);
        }
        int count = capture.Installs.Count;
        var unknown = new Computer("INERT-UNKNOWN");
        RunInstall(workspace, unknown);
        Require(capture.Installs.Count == count && unknown.UpdateMessage!.StartsWith("Unknown OS build"),
            "Unknown OS was guessed onto an update lane.");
        var staged = new Computer("INERT-CU")
        {
            OsBuild = 14393, RequiresStagedPatching = true, StagedThisSession = true, RebootRequired = true,
        };
        RunInstall(workspace, staged);
        Require(capture.Installs.Count == count && staged.UpdateMessage!.StartsWith("CU staged"),
            "Install anyway bypassed the staged-CU guard.");

        return new { pass = true, builds, scans = capture.Scans, installs = capture.Installs.Count,
            scope = "Actual WorkspaceViewModel; synthetic WUA responses, no target operations" };
    }

    private static SoftwareUpdate Update(string kb, string title) => new(title, kb, false, 0, 0);
    private static MethodInfo Method(string name) => typeof(WorkspaceViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void RunScan(WorkspaceViewModel vm, Computer row) =>
        ((Task)Scan.Invoke(vm, [row, CancellationToken.None])!).GetAwaiter().GetResult();
    private static void RunInstall(WorkspaceViewModel vm, Computer row) =>
        ((Task)Install.Invoke(vm, [row, CancellationToken.None, null, false, true])!).GetAwaiter().GetResult();
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public class PatchProxy : DispatchProxy
    {
        internal IReadOnlyList<SoftwareUpdate> Updates { get; set; } = [];
        internal List<PatchOptions> Installs { get; } = [];
        internal int Scans { get; private set; }
        internal HostPatchStatus InstallResult { get; set; } = new(PatchPhase.PendingReboot,
            "Installed — reboot pending", InstalledCount: 1, RebootPending: true);
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IPatchService.ScanAsync))
            {
                Scans++;
                return Task.FromResult(new HostPatchStatus(PatchPhase.Available,
                    $"{Updates.Count} updates available", AvailableCount: Updates.Count) { Updates = Updates });
            }
            if (targetMethod?.Name == nameof(IPatchService.InstallAsync))
            {
                Installs.Add(((PatchOptions)args![1]!).Clone());
                return Task.FromResult(InstallResult);
            }
            throw new NotSupportedException("Unexpected remote-capable call: " + targetMethod?.Name);
        }
    }
}
