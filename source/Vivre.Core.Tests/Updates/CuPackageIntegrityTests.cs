using System.Security.Cryptography;
using Vivre.Core.Updates;

namespace Vivre.Core.Tests.Updates;

public sealed class CuPackageIntegrityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "VivreCuIntegrity_" + Guid.NewGuid().ToString("N"));
    public CuPackageIntegrityTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void CopyPreservesBytesAndRejectsSameSizeCorruption()
    {
        string source = Path.Combine(root, "dummy.msu"), target = Path.Combine(root, "copy.msu");
        byte[] payload = Enumerable.Range(0, 300000).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(source, payload);
        SmbAgentLane.CopyPackage(source, target);
        Assert.Equal(payload, File.ReadAllBytes(target));
        string expected = Convert.ToHexString(SHA256.HashData(payload));
        payload[150000] ^= 1;
        File.WriteAllBytes(target, payload);
        Assert.Throws<IOException>(() => SmbAgentLane.VerifyPackageCopy(target, payload.Length, expected));
    }

    [Fact]
    public void TruncationIsRejected()
    {
        string target = Path.Combine(root, "short.msu");
        File.WriteAllBytes(target, [1, 2]);
        Assert.Throws<IOException>(() => SmbAgentLane.VerifyPackageCopy(target, 3, "unused"));
    }

    [Fact]
    public void CancellationDoesNotOverwriteExistingDestination()
    {
        string source = Path.Combine(root, "source.msu"), target = Path.Combine(root, "target.msu");
        File.WriteAllBytes(source, [1, 2]); File.WriteAllBytes(target, [3, 4]);
        Assert.Throws<OperationCanceledException>(() => SmbAgentLane.CopyPackage(source, target, new CancellationToken(true)));
        Assert.Equal(new byte[] { 3, 4 }, File.ReadAllBytes(target));
    }
}
