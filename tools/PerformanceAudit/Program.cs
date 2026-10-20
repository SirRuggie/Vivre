using Vivre.PerformanceAudit;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Vivre.Core.Deploy;
using Vivre.Core.Operations;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--share-smoke")
        {
            File.WriteAllText(args[2], JsonSerializer.Serialize(SharedDriveProbe.Run(args[1]), new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        if (args.Length == 2 && args[0] == "--people-directory-check")
        {
            object result;
            try
            {
                var found = new Vivre.Core.People.AdPeopleLookup().LookupAsync("VIVRETEST" + Guid.NewGuid().ToString("N"), CancellationToken.None).GetAwaiter().GetResult();
                var lookup = new Vivre.Core.People.AdPeopleLookup();
                var self = lookup.LookupAsync(Environment.UserName, CancellationToken.None).GetAwaiter().GetResult();
                var qualified = lookup.LookupAsync(Environment.UserDomainName + "\\" + Environment.UserName, CancellationToken.None).GetAwaiter().GetResult();
                result = new { connected = true, syntheticQuery = true, candidateCount = found.Candidates.Count,
                    currentAccountMatches = self.Candidates.Count, currentAccountExact = self.Exact,
                    currentAccountHasEmail = self.Candidates.Count == 1 && !string.IsNullOrWhiteSpace(self.Candidates[0].Person.Email),
                    qualifiedMatchesSameAccount = self.Candidates.Count == 1 && qualified.Candidates.Count == 1
                        && self.Candidates[0].Person.Id == qualified.Candidates[0].Person.Id };
            }
            catch (Exception ex) { result = new { connected = false, error = ex.Message }; }
            File.WriteAllText(args[1], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        bool peopleOnly = args.Length == 2 && args[0] == "--people-only";
        bool actionsMenuOnly = args.Length == 2 && args[0] == "--actions-menu-only";
        bool layoutAccessibilityOnly = args.Length == 2 && args[0] == "--layout-accessibility-only";
        bool tabHandoffOnly = args.Length == 2 && args[0] == "--tab-handoff-only";
        bool librarySearchOnly = args.Length == 2 && args[0] == "--library-search-only";
        bool settingsClarityOnly = args.Length == 2 && args[0] == "--settings-clarity-only";
        bool patchingClarityOnly = args.Length == 2 && args[0] == "--patching-clarity-only";
        bool operationalClarityOnly = args.Length == 2 && args[0] == "--operational-clarity-only";
        bool workspaceSafetyOnly = args.Length == 2 && args[0] == "--workspace-safety-only";
        bool tabOptionsOnly = args.Length == 2 && args[0] == "--tab-options-only";
        bool adLinuxOnly = args.Length == 2 && args[0] == "--ad-linux-only";
        bool softwareDisplayOnly = args.Length == 2 && args[0] == "--software-display-only";
        bool cuStageOnly = args.Length == 2 && args[0] == "--cu-stage-only";
        bool auditFixesOnly = args.Length == 2 && args[0] == "--audit-fixes-only";
        bool bannersOnly = args.Length == 2 && args[0] == "--banners-only";
        bool installOverrideOnly = args.Length == 2 && args[0] == "--install-override-only";
        bool filterExperiment = args.Length == 2 && args[0] == "--filter-experiment";
        if (args.Length != 1 && !operationalClarityOnly && !filterExperiment && !bannersOnly && !auditFixesOnly && !cuStageOnly && !installOverrideOnly && !softwareDisplayOnly && !adLinuxOnly && !tabOptionsOnly && !workspaceSafetyOnly && !patchingClarityOnly && !settingsClarityOnly && !librarySearchOnly && !tabHandoffOnly && !layoutAccessibilityOnly && !actionsMenuOnly && !peopleOnly) throw new ArgumentException("Provide [--filter-experiment] and the JSON output path.");
        string outputPath = args[^1];
        // No production App startup, visible windows, credentials, server operations or production writes.
        var app = new AuditApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Read the exact production dictionaries without instantiating its x:Class/composition root.
        using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("Vivre.App.xaml")!)
        {
            Assembly.Load("Wpf.Ui"); // Register its XML namespace before loose-XAML resource parsing.
            var document = System.Xml.Linq.XDocument.Load(source);
            var application = document.Root!;
            var resources = application.Element(application.Name.Namespace + "Application.Resources")!.Elements().Single();
            foreach (var attribute in application.Attributes().Where(a => a.IsNamespaceDeclaration))
                resources.SetAttributeValue(attribute.Name, attribute.Value.Replace("clr-namespace:Vivre.Desktop", "clr-namespace:Vivre.Desktop;assembly=Vivre"));
            app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resources.ToString());
        }
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (!app.StartupSuppressed || app.Windows.Count != 0)
            throw new InvalidOperationException("The isolated audit must suppress production startup and open no application windows.");
        string root = Path.Combine(Path.GetTempPath(), "VivrePerformanceAudit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        WorkspaceProbe.SettingsRoot = Path.Combine(root, "workspaces");
        try
        {
            if (operationalClarityOnly)
            {
                File.WriteAllText(outputPath, JsonSerializer.Serialize(OperationalClarityProbe.Run(), new JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            if (peopleOnly)
            {
                string renders = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, "people-renders");
                Directory.CreateDirectory(renders);
                File.WriteAllText(outputPath, JsonSerializer.Serialize(PeopleProbe.Run(renders), new JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            if (actionsMenuOnly)
            {
                File.WriteAllText(outputPath, JsonSerializer.Serialize(ActionsMenuProbe.Run(), new JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            if (layoutAccessibilityOnly)
            {
                string renders = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, "u21-renders");
                File.WriteAllText(outputPath, JsonSerializer.Serialize(new {
                    dark = LayoutAccessibilityProbe.Run(Path.Combine(renders, "dark")),
                    light = LayoutAccessibilityProbe.Run(Path.Combine(renders, "light"), dark: false)
                }, new JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            if (tabHandoffOnly)
            {
                File.WriteAllText(outputPath, JsonSerializer.Serialize(TabHandoffProbe.Run(root), new JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            if (librarySearchOnly)
            {
                File.WriteAllText(outputPath, JsonSerializer.Serialize(new { lists = LibrarySearchProbe.Run(), scripts = ScriptEditorProbe.Run() }, new JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            if (settingsClarityOnly)
            {
                File.WriteAllText(outputPath, JsonSerializer.Serialize(new { settings = SettingsClarityProbe.Run(root), gridsAndServices = GridPreferencesProbe.Run(root) }, new JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            if (patchingClarityOnly)
            {
                File.WriteAllText(outputPath, JsonSerializer.Serialize(PatchingClarityProbe.Run(), new JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            if (workspaceSafetyOnly)
            {
                File.WriteAllText(outputPath, JsonSerializer.Serialize(WorkspaceSafetyProbe.Run(root), new JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            if (tabOptionsOnly)
            {
                File.WriteAllText(outputPath, JsonSerializer.Serialize(TabOptionsProbe.Run(), new JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            if (adLinuxOnly)
            {
                File.WriteAllText(outputPath, JsonSerializer.Serialize(AdLinuxLabelProbe.Run(), new JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            if (softwareDisplayOnly)
            {
                File.WriteAllText(outputPath, JsonSerializer.Serialize(SoftwareDisplayProbe.Run(), new JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            if (cuStageOnly)
            {
                File.WriteAllText(outputPath, JsonSerializer.Serialize(new { stage = CuStageResponsivenessProbe.Run(), preflight = CuPreflightProbe.Run() }, new JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            if (auditFixesOnly)
            {
                File.WriteAllText(outputPath, JsonSerializer.Serialize(new { auditFixes = AuditFixesProbe.Run(), scriptExecution = ScriptExecutionProbe.Run(), scriptEditor = ScriptEditorProbe.Run() }, new JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            if (bannersOnly)
            {
                File.WriteAllText(outputPath, JsonSerializer.Serialize(CompletionBannerProbe.Run(), new JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            if (installOverrideOnly)
            {
                File.WriteAllText(outputPath, JsonSerializer.Serialize(InstallOverrideProbe.Run(), new JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            if (filterExperiment)
            {
                File.WriteAllText(Path.GetFullPath(outputPath), JsonSerializer.Serialize(FilterExperiment(), new JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            var result = new
            {
                measuredAtUtc = DateTime.UtcNow,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                logicalProcessors = Environment.ProcessorCount,
                configuration = "Release; isolated synthetic data; no production App startup",
                workspace = Measure("Workspace", WorkspaceProbe.Run),
                logging = Measure("Log dispatcher", LogProbe.Run),
                throttle = Measure("Throttle", () => Task.Run(ThrottleProbe.RunAsync).GetAwaiter().GetResult()),
                registry = Measure("Registry", () => RegistryProbe(root)),
                manifest = Measure("Manifest", () => ManifestProbe(root)),
                completionBanners = Measure("Completion banners", CompletionBannerProbe.Run),
                renderedWorkspace = Measure("Rendered workspace", RenderedWorkspaceProbe.Run),
                auditFixes = Measure("Audit fixes", AuditFixesProbe.Run),
                scriptExecution = Measure("Script execution", ScriptExecutionProbe.Run),
                scriptEditor = Measure("Script editor", ScriptEditorProbe.Run)
            };
            File.WriteAllText(Path.GetFullPath(outputPath), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Performance evidence saved: " + Path.GetFullPath(outputPath));
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(root).StartsWith("VivrePerformanceAudit_", StringComparison.Ordinal))
                Directory.Delete(root, true);
            app.Shutdown();
        }
    }

    // Application queues OnStartup from its constructor: pumping a dispatcher can run it even
    // without Application.Run. Never construct production services, load sessions, or show the shell.
    private sealed class AuditApplication : Application
    {
        public bool StartupSuppressed { get; private set; }
        protected override void OnStartup(StartupEventArgs e) => StartupSuppressed = true;
    }

    private static object FilterExperiment()
    {
        var factory = typeof(WorkspaceProbe).GetMethod("CreateWorkspace", BindingFlags.Static | BindingFlags.NonPublic)!;
        using var workspace = (Vivre.Desktop.ViewModels.WorkspaceViewModel)factory.Invoke(null, [null])!;
        for (int i = 0; i < 3190; i++) workspace.Computers.Add(new Vivre.Core.Models.Computer($"MACHINE-{i:D5}"));
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(workspace.Computers);
        var live = (System.ComponentModel.ICollectionViewLiveShaping)view;
        string[] original = ["Name", "IsOnline", "PatchState", "RebootRequired", "LastError", "UpdateError",
            "UpdatesAvailable", "MissingUpdates", "VitalityBand", "OsBuild", "UpdatePhase", "ScheduledNextRun"];
        var rows = new List<object>();
        foreach (string mode in new[] { "all12", "nameOnly", "all12-control" })
        {
            using (view.DeferRefresh())
            {
                live.LiveFilteringProperties.Clear();
                foreach (string prop in mode == "nameOnly" ? new[] { "Name" } : original) live.LiveFilteringProperties.Add(prop);
            }
            var times = new List<double>();
            for (int i = 0; i < 4; i++)
            {
                var watch = Stopwatch.StartNew(); view.Refresh(); watch.Stop();
                times.Add(watch.Elapsed.TotalMilliseconds);
            }
            rows.Add(new { mode, properties = live.LiveFilteringProperties.Count, refreshMs = times, count = workspace.VisibleRowCount });
        }
        return rows;
    }

    private static object Measure(string name, Func<object> measure)
    {
        Console.WriteLine("Measuring " + name);
        return measure();
    }

    private static object RegistryProbe(string root)
    {
        var measurements = new List<object>();
        foreach (int count in new[] { 100, 1000, 5000, 10000 })
        {
            string file = Path.Combine(root, "registry-" + count + ".json");
            string[] paths = Enumerable.Range(0, count).Select(i => Path.Combine(root, "dummy-path-" + i)).ToArray();
            File.WriteAllText(file, JsonSerializer.Serialize(new Dictionary<string, string[]> { ["DUMMY"] = paths }));
            var registry = new ProtectedStagingPathRegistry(file);
            registry.GetProtectedPaths("DUMMY"); // warm JIT and filesystem cache
            var reads = new List<double>();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int repeat = 0; repeat < 3; repeat++)
            {
                var sw = Stopwatch.StartNew();
                if (registry.GetProtectedPaths("DUMMY").Count != count) throw new InvalidOperationException("Registry count mismatch.");
                reads.Add(sw.Elapsed.TotalMilliseconds);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var write = Stopwatch.StartNew();
            registry.Register("DUMMY", paths[0]); // production rewrites even an existing path
            measurements.Add(new { paths = count, readMs = reads, allocatedBytesAcrossThreeReads = allocated,
                duplicateRegisterMs = write.Elapsed.TotalMilliseconds, fileBytes = new FileInfo(file).Length });
        }
        return measurements;
    }

    private static object ManifestProbe(string root)
    {
        Type publication = typeof(DeploymentService).Assembly.GetType("Vivre.Core.Deploy.PackagePublication")!;
        MethodInfo prepare = publication.GetMethod("Prepare", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        MethodInfo encode = publication.GetMethod("ManifestBase64", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        var results = new List<object>();
        foreach (int count in new[] { 100, 1000, 10000 })
        {
            string source = Path.Combine(root, "manifest-" + count);
            Directory.CreateDirectory(source);
            foreach (int i in Enumerable.Range(0, count)) File.WriteAllBytes(Path.Combine(source, $"dummy-{i:D6}.bin"), []);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            object plan = prepare.Invoke(null, [source, true, Path.Combine(root, "target-" + count), true, CancellationToken.None])!;
            double prepareMs = sw.Elapsed.TotalMilliseconds;
            object manifest = plan.GetType().GetProperty("Manifest")!.GetValue(plan)!;
            string encoded = (string)encode.Invoke(null, [manifest])!;
            results.Add(new { files = count, totalPayloadBytes = 0, prepareMs,
                encodedManifestCharacters = encoded.Length, managedAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before });
        }
        return results;
    }
}
