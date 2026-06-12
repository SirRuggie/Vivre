using Vivre.Core.Updates;
using Xunit;

namespace Vivre.Core.Tests.Updates;

/// <summary>
/// The Server 2016 LCU lane's daytime Stage step: it must refuse (with the catalog link) when the CU
/// package isn't present/correct — without touching the box — and, when the package is good, deliver it
/// through the SMB lane. The reboot/wave parts are separate and not covered here.
/// </summary>
public class FullPackageLcuLaneTests
{
    private static readonly HostPatchStatus StagedSentinel =
        new(PatchPhase.PendingReboot, "Staged — reboot-ready.");

    [Fact]
    public async Task Stage_fails_fast_with_catalog_link_when_package_missing()
    {
        using var dir = new TempDir(); // empty — no .msu
        var smb = new RecordingSmb();
        var lane = new FullPackageLcuLane(smb);

        HostPatchStatus result = await lane.StageAsync(
            "VISION-BOX", dir.Path, new LcuTarget("KB5094122", "x64"),
            new PatchOptions(), new SyncProgress(), CancellationToken.None);

        Assert.Equal(PatchPhase.Error, result.Phase);
        Assert.Contains("catalog.update.microsoft.com", result.Message);
        Assert.Contains("KB5094122", result.Message);
        Assert.Null(smb.StagedHost);   // the box must NOT be touched when the package isn't ready
        Assert.Null(smb.StagedPath);
    }

    [Fact]
    public async Task Stage_delivers_the_resolved_package_to_the_smb_lane()
    {
        using var dir = new TempDir();
        string msu = dir.WriteFile("windows10.0-kb5094122-x64_abcdef.msu", sizeBytes: 4096);
        var smb = new RecordingSmb();
        var lane = new FullPackageLcuLane(smb);

        HostPatchStatus result = await lane.StageAsync(
            "VISION-BOX", dir.Path, new LcuTarget("KB5094122", "x64"),
            new PatchOptions(), new SyncProgress(), CancellationToken.None);

        Assert.Equal("VISION-BOX", smb.StagedHost);            // delivered to the box
        Assert.Equal(msu, smb.StagedPath);                     // exactly the resolved package
        Assert.Equal(PatchPhase.PendingReboot, result.Phase);  // SMB lane's terminal flows back
    }

    [Fact]
    public async Task Stage_does_not_touch_the_box_when_the_arch_does_not_match()
    {
        using var dir = new TempDir();
        dir.WriteFile("windows10.0-kb5094122-arm64_x.msu", sizeBytes: 4096); // wrong arch
        var smb = new RecordingSmb();
        var lane = new FullPackageLcuLane(smb);

        HostPatchStatus result = await lane.StageAsync(
            "VISION-BOX", dir.Path, new LcuTarget("KB5094122", "x64"),
            new PatchOptions(), new SyncProgress(), CancellationToken.None);

        Assert.Equal(PatchPhase.Error, result.Phase);
        Assert.Null(smb.StagedHost);
    }

    [Fact]
    public async Task Verify_is_green_when_ubr_matches_the_target()
    {
        var lane = new FullPackageLcuLane(new RecordingSmb(), buildReader: new FakeBuildReader(14393, 9234));

        LcuVerifyResult r = await lane.VerifyAsync("VISION-BOX", targetUbr: 9234, CancellationToken.None);

        Assert.Equal(LcuVerifyOutcome.Verified, r.Outcome);
        Assert.Equal(9234, r.Ubr);
    }

    [Fact]
    public async Task Verify_is_red_when_box_is_still_at_the_old_build()
    {
        // Came back, but at the old build — a rollback. This is the ONLY red verdict.
        var lane = new FullPackageLcuLane(new RecordingSmb(), buildReader: new FakeBuildReader(14393, 9060));

        LcuVerifyResult r = await lane.VerifyAsync("VISION-BOX", targetUbr: 9234, CancellationToken.None);

        Assert.Equal(LcuVerifyOutcome.WrongBuild, r.Outcome);
        Assert.Equal(9060, r.Ubr);
    }

    [Fact]
    public async Task Verify_is_unreachable_not_failed_when_the_build_cannot_be_read_yet()
    {
        // Box pingable but not up enough to read the registry yet — must be "retry", never a red verdict.
        var lane = new FullPackageLcuLane(new RecordingSmb(), buildReader: new FakeBuildReader(null, null));

        LcuVerifyResult r = await lane.VerifyAsync("VISION-BOX", targetUbr: 9234, CancellationToken.None);

        Assert.Equal(LcuVerifyOutcome.Unreachable, r.Outcome);
        Assert.NotEqual(LcuVerifyOutcome.WrongBuild, r.Outcome); // a slow box is never written off
    }

    [Fact]
    public async Task ComponentCleanup_runs_on_the_box_via_the_smb_lane()
    {
        var smb = new RecordingSmb();
        var lane = new FullPackageLcuLane(smb);

        HostPatchStatus result = await lane.ComponentCleanupAsync(
            "VISION-BOX", new PatchOptions(), new SyncProgress(), CancellationToken.None);

        Assert.Equal("VISION-BOX", smb.CleanupHost);
        Assert.Equal(PatchPhase.Done, result.Phase);
    }

    private sealed class FakeBuildReader(int? build, int? ubr) : ILcuBuildReader
    {
        public Task<(int? CurrentBuild, int? Ubr)> ReadAsync(string host, CancellationToken cancellationToken = default) =>
            Task.FromResult((build, ubr));
    }

    private sealed class RecordingSmb : ISmbAgentLane
    {
        public string? StagedHost { get; private set; }
        public string? StagedPath { get; private set; }

        public Task<HostPatchStatus> InstallFullPackageAsync(string host, string sourcePackagePath, PatchOptions options, IProgress<HostPatchStatus> progress, CancellationToken cancellationToken)
        {
            StagedHost = host;
            StagedPath = sourcePackagePath;
            return Task.FromResult(StagedSentinel);
        }

        public Task<HostPatchStatus> ScanAsync(string host, PatchOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<HostPatchStatus> InstallAsync(string host, PatchOptions options, IProgress<HostPatchStatus> progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<HostPatchStatus> UninstallAsync(string host, PatchOptions options, IProgress<HostPatchStatus> progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public string? CleanupHost { get; private set; }

        public Task<HostPatchStatus> RunComponentCleanupAsync(string host, PatchOptions options, IProgress<HostPatchStatus> progress, CancellationToken cancellationToken)
        {
            CleanupHost = host;
            return Task.FromResult(new HostPatchStatus(PatchPhase.Done, "Component cleanup complete."));
        }
    }

    private sealed class SyncProgress : IProgress<HostPatchStatus>
    {
        public void Report(HostPatchStatus value) { }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VivreLcuTest_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Path);
        }

        public string WriteFile(string name, int sizeBytes)
        {
            string full = System.IO.Path.Combine(Path, name);
            System.IO.File.WriteAllBytes(full, new byte[sizeBytes]);
            return full;
        }

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Path, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }
}
