using System.IO;
using System.Reflection;
using Vivre.Core.Deploy;
using Vivre.Core.Updates;

namespace Vivre.PerformanceAudit;

internal static class SharedDriveProbe
{
    public static object Run(string root)
    {
        string full = Path.GetFullPath(root).TrimEnd('\\');
        if (!full.StartsWith(@"Z:\Vivre\testing\", StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("release-", StringComparison.Ordinal))
            throw new ArgumentException("Use a fresh release-* folder under Z:\\Vivre\\testing.");
        if (Directory.Exists(full)) throw new IOException("Refusing to reuse an existing test folder.");
        Directory.CreateDirectory(full);
        string source = Path.Combine(full, "dummy-source.msu"), target = Path.Combine(full, "dummy-copy.msu");
        byte[] bytes = Enumerable.Range(0, 1048576).Select(i => (byte)(i % 239)).ToArray();
        File.WriteAllBytes(source, bytes);
        var copy = typeof(SmbAgentLane).GetMethod("CopyPackage", BindingFlags.Static | BindingFlags.NonPublic)!;
        copy.Invoke(null, [source, target, CancellationToken.None]);
        if (!File.ReadAllBytes(target).SequenceEqual(bytes)) throw new IOException("Copy mismatch");
        string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        bytes[500000] ^= 1; File.WriteAllBytes(target, bytes);
        var verify = typeof(SmbAgentLane).GetMethod("VerifyPackageCopy", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool rejected = false;
        try { verify.Invoke(null, [target, (long)bytes.Length, digest, CancellationToken.None]); }
        catch (TargetInvocationException ex) when (ex.InnerException is IOException) { rejected = true; }
        if (!rejected) throw new IOException("Same-length corruption accepted");
        copy.Invoke(null, [source, target, CancellationToken.None]);

        Type publication = typeof(DeploymentService).Assembly.GetType("Vivre.Core.Deploy.PackagePublication")!;
        var prepare = publication.GetMethod("Prepare", BindingFlags.Static | BindingFlags.NonPublic)!;
        var publish = publication.GetMethod("PublishLocal", BindingFlags.Static | BindingFlags.NonPublic)!;
        string folder = Path.Combine(full, "dummy-source-folder"), destination = Path.Combine(full, "dummy-published-folder");
        Directory.CreateDirectory(folder); Directory.CreateDirectory(destination);
        Directory.CreateDirectory(Path.Combine(folder, "empty"));
        File.WriteAllText(Path.Combine(folder, "new.txt"), "Dummy package contents");
        File.WriteAllText(Path.Combine(destination, "old.txt"), "Dummy previous contents");
        object plan = prepare.Invoke(null, [folder, true, destination, true, CancellationToken.None])!;
        StageResult result = (StageResult)publish.Invoke(null, [plan, CancellationToken.None, null])!;
        if (!result.Ok || File.Exists(Path.Combine(destination, "old.txt"))
            || !Directory.Exists(Path.Combine(destination, "empty"))) throw new IOException("Folder publication failed: " + result);
        File.WriteAllText(Path.Combine(folder, "new.txt"), "Dummy replacement contents");
        plan = prepare.Invoke(null, [folder, true, destination, true, CancellationToken.None])!;
        Action<string> fault = phase => { if (phase == "before-publish") throw new IOException("Injected dummy publication fault"); };
        result = (StageResult)publish.Invoke(null, [plan, CancellationToken.None, fault])!;
        if (result.Ok || File.ReadAllText(Path.Combine(destination, "new.txt")) != "Dummy package contents")
            throw new IOException("Rollback failed");
        return new { root = full, cuCopyVerified = true, sameLengthCorruptionRejected = true,
            folderReplacementVerified = true, rollbackVerified = true,
            scope = "Production file-copy/publication methods over mapped SMB drive. No remoting, agent launch, installation or reboot. Dummy files retained for inspection." };
    }
}
