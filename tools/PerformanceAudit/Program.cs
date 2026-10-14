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
        if (args.Length != 1) throw new ArgumentException("Provide the JSON output path.");
        // No Vivre.App startup, windows, credentials, server operations or production writes.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string root = Path.Combine(Path.GetTempPath(), "VivrePerformanceAudit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
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
                manifest = Measure("Manifest", () => ManifestProbe(root))
            };
            File.WriteAllText(Path.GetFullPath(args[0]), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Performance evidence saved: " + Path.GetFullPath(args[0]));
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(root).StartsWith("VivrePerformanceAudit_", StringComparison.Ordinal))
                Directory.Delete(root, true);
            app.Shutdown();
        }
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
            object plan = prepare.Invoke(null, [source, true, Path.Combine(root, "target-" + count), true])!;
            double prepareMs = sw.Elapsed.TotalMilliseconds;
            object manifest = plan.GetType().GetProperty("Manifest")!.GetValue(plan)!;
            string encoded = (string)encode.Invoke(null, [manifest])!;
            results.Add(new { files = count, totalPayloadBytes = 0, prepareMs,
                encodedManifestCharacters = encoded.Length, managedAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before });
        }
        return results;
    }
}
