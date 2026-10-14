using Vivre.Core.Operations;

namespace Vivre.Core.Tests.Operations;

public sealed class CleanupGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VivreGuardTests_" + Guid.NewGuid().ToString("N"));
    private string RegistryPath => Path.Combine(_root, "protected.json");

    [Theory]
    [InlineData("localhost", "LOCALHOST.")]
    [InlineData("localhost", ".")]
    [InlineData("localhost", "127.0.0.1")]
    [InlineData("localhost", "::1")]
    [InlineData("server01", " SERVER01. ")]
    public void Equivalent_host_names_cannot_bypass_exclusive_cleanup(string first, string alias)
    {
        var gate = new HostOperationCoordinator();
        using var claim = gate.TryBeginCleanup(first);
        Assert.True(claim.Acquired);
        using var conflict = gate.TryBeginOperation(alias, "Install");
        Assert.False(conflict.Acquired);
    }

    [Fact]
    public void Ordinary_operations_remain_parallel_but_all_must_finish_before_cleanup()
    {
        var gate = new HostOperationCoordinator();
        var first = gate.TryBeginOperation("server01", "Scan");
        var second = gate.TryBeginOperation("server01", "Scan");
        Assert.True(first.Acquired && second.Acquired);
        first.Dispose();
        using (var blocked = gate.TryBeginCleanup("server01")) Assert.False(blocked.Acquired);
        first.Dispose(); // Duplicate disposal must not release another operation's ownership.
        using (var blocked = gate.TryBeginCleanup("server01")) Assert.False(blocked.Acquired);
        second.Dispose();
        using var allowed = gate.TryBeginCleanup("server01");
        Assert.True(allowed.Acquired);
    }

    [Fact]
    public void Uncertain_patch_blocks_cleanup_but_allows_normal_recovery_work()
    {
        var gate = new HostOperationCoordinator();
        using (var patch = gate.TryBeginOperation("server01", "Install")) patch.MarkRemoteStateUncertain();
        using var blocked = gate.TryBeginCleanup("server01");
        Assert.False(blocked.Acquired);
        Assert.Contains("Install", blocked.RefusalMessage);
        using var allowed = gate.TryBeginOperation("server01", "Scan");
        Assert.True(allowed.Acquired);
    }

    [Fact]
    public void Uncertain_cleanup_blocks_both_cleanup_and_patching()
    {
        var gate = new HostOperationCoordinator();
        using (var cleanup = gate.TryBeginCleanup("server01")) cleanup.MarkRemoteStateUncertain();
        using var another = gate.TryBeginCleanup("server01");
        using var patch = gate.TryBeginOperation("server01", "Install");
        using var otherHost = gate.TryBeginOperation("server02", "Install");
        Assert.False(another.Acquired);
        Assert.False(patch.Acquired);
        Assert.True(otherHost.Acquired);
    }

    [Fact]
    public void Custom_paths_survive_new_registry_instance_and_drive_root_is_not_made_relative()
    {
        var first = new ProtectedStagingPathRegistry(RegistryPath);
        first.Register("server01", @"C:\Windows\Temp\Custom\package.msi");
        first.Register("SERVER01.", @"D:\");
        var reloaded = new ProtectedStagingPathRegistry(RegistryPath).GetProtectedPaths("SERVER01");
        Assert.Contains(@"C:\Windows\Temp\Custom\package.msi", reloaded);
        Assert.Contains(@"D:\", reloaded);
    }

    [Fact]
    public void Batch_registration_deduplicates_and_duplicate_batch_does_not_rewrite()
    {
        var registry = new ProtectedStagingPathRegistry(RegistryPath);
        registry.RegisterMany("server01", [@"C:\stage\one", @"c:\STAGE\one\", @"C:\stage\two"]);
        DateTime sentinel = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(RegistryPath, sentinel);

        registry.RegisterMany("SERVER01.", [@"C:\STAGE\ONE", @"c:\stage\two\"]);

        Assert.Equal(sentinel, File.GetLastWriteTimeUtc(RegistryPath));
        Assert.Equal(2, registry.GetProtectedPaths("server01").Count);
    }

    [Fact]
    public void Separate_instances_synchronize_updates_to_the_same_file()
    {
        var first = new ProtectedStagingPathRegistry(RegistryPath);
        var second = new ProtectedStagingPathRegistry(RegistryPath);

        Parallel.Invoke(
            () => first.RegisterMany("server01", Enumerable.Range(0, 50).Select(i => $@"C:\stage\a{i}")),
            () => second.RegisterMany("server01", Enumerable.Range(0, 50).Select(i => $@"C:\stage\b{i}")));

        Assert.Equal(100, first.GetProtectedPaths("server01").Count);
    }

    [Fact]
    public void Confirmed_owned_unregister_removes_only_named_paths_and_skips_missing_rewrite()
    {
        var registry = new ProtectedStagingPathRegistry(RegistryPath);
        registry.RegisterMany("server01", [@"C:\stage\keep", @"C:\stage\remove"]);

        registry.UnregisterOwnedConfirmed("SERVER01.", [@"c:\STAGE\remove\"]);
        Assert.Equal([@"C:\stage\keep"], registry.GetProtectedPaths("server01"));

        DateTime sentinel = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(RegistryPath, sentinel);
        registry.UnregisterOwnedConfirmed("server01", [@"C:\stage\missing"]);
        Assert.Equal(sentinel, File.GetLastWriteTimeUtc(RegistryPath));
    }

    [Fact]
    public void Corrupt_registry_cannot_be_treated_as_empty_or_overwritten()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(RegistryPath, "{broken");
        var registry = new ProtectedStagingPathRegistry(RegistryPath);
        Assert.ThrowsAny<Exception>(() => registry.GetProtectedPaths("server01"));
        Assert.ThrowsAny<Exception>(() => registry.Register("server01", @"C:\package.msi"));
        Assert.Equal("{broken", File.ReadAllText(RegistryPath));
    }

    [Fact]
    public void Unreadable_registry_location_does_not_fall_back_to_empty()
    {
        Directory.CreateDirectory(RegistryPath);
        var registry = new ProtectedStagingPathRegistry(RegistryPath);
        Assert.Throws<UnauthorizedAccessException>(() => registry.GetProtectedPaths("server01"));
    }

    [Theory]
    [InlineData("relative\\package.msi")]
    [InlineData("C:package.msi")]
    public void Relative_protection_paths_are_rejected_before_any_registry_write(string path)
    {
        var registry = new ProtectedStagingPathRegistry(RegistryPath);
        Assert.Throws<ArgumentException>(() => registry.Register("server01", path));
        Assert.False(File.Exists(RegistryPath));
    }

    [Fact]
    public void Syntactically_valid_registry_with_relative_path_still_blocks_cleanup()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(RegistryPath, """{"server01":["C:package.msi"]}""");
        var registry = new ProtectedStagingPathRegistry(RegistryPath);
        Assert.Throws<ArgumentException>(() => registry.GetProtectedPaths("server01"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
