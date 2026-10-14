using System.Text.Json;
using System.Text.Json.Nodes;
using Vivre.Core.IO;
using Vivre.Desktop;
using Xunit;

namespace Vivre.Core.Tests.Configuration;

public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Vivre-F10-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "settings.json");
    private readonly List<AppSettingsStore> _stores = [];

    private AppSettingsStore Store(Action<string, string>? writer = null, string? path = null)
    {
        var store = new AppSettingsStore(path ?? SettingsPath, writer);
        _stores.Add(store);
        return store;
    }

    [Fact]
    public void SeparateCallers_ShareOrderedWritesAndLatestFields_PreserveFutureKeys()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "{\"FutureOption\":{\"enabled\":true}}");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int writes = 0;
        var first = Store((path, json) =>
        {
            if (Interlocked.Increment(ref writes) == 1) { entered.Set(); release.Wait(); }
            AtomicFileWriter.Write(path, json);
        });
        try
        {
            var settings = first.Load();
            settings.Theme = "Light";
            first.Save(settings);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var second = Store();
            Assert.Same(settings, second.Load());
            settings.MaxSimultaneousInstalls = 17;
            second.Save(settings);
            settings.Theme = "Dark";
            first.Save(settings);
            release.Set();
            Assert.Equal(SaveFlushStatus.Success, second.Flush(TimeSpan.FromSeconds(5)).Status);
            var disk = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
            Assert.Equal("Dark", disk["Theme"]!.GetValue<string>());
            Assert.Equal(17, disk["MaxSimultaneousInstalls"]!.GetValue<int>());
            Assert.True(disk["FutureOption"]!["enabled"]!.GetValue<bool>());
            Assert.Equal(3, writes);
        }
        finally { release.Set(); first.Flush(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public void AcceptedSnapshot_IsDetachedFromLaterUnsavedMutations()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var store = Store((path, json) => { entered.Set(); release.Wait(); AtomicFileWriter.Write(path, json); });
        try
        {
            var settings = store.Load();
            settings.Theme = "Light";
            store.Save(settings);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            settings.Theme = "Dark";
            settings.HiddenColumns.Add("DummyColumn");
            release.Set();
            Assert.Equal(SaveFlushStatus.Success, store.Flush(TimeSpan.FromSeconds(5)).Status);
            var restored = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath))!;
            Assert.Equal("Light", restored.Theme);
            Assert.Empty(restored.HiddenColumns);
        }
        finally { release.Set(); store.Flush(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public void ImmediateExitFlush_PersistsLatestPreferenceForReload()
    {
        var store = Store();
        var settings = store.Load();
        settings.AutoCheckOnLoad = false;
        settings.BottomDockHeight = 231;
        store.Save(settings);
        Assert.Equal(SaveFlushStatus.Success, store.Flush(TimeSpan.FromSeconds(3)).Status);
        var restored = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath))!;
        Assert.False(restored.AutoCheckOnLoad);
        Assert.Equal(231, restored.BottomDockHeight);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void LockedFile_ReportsFailurePreservesFile_AndLaterSaveRecovers()
    {
        var store = Store();
        const string original = "{\"Theme\":\"Dark\",\"FutureOption\":42}";
        File.WriteAllText(SettingsPath, original);
        var settings = store.Load();
        using (var locked = new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            settings.Theme = "Light";
            store.Save(settings);
            SaveFlushResult failed = store.Flush(TimeSpan.FromSeconds(5));
            Assert.Equal(SaveFlushStatus.Failed, failed.Status);
            Assert.IsAssignableFrom<IOException>(failed.Failure);
        }
        Assert.Equal(original, File.ReadAllText(SettingsPath));
        store.Save(settings);
        Assert.Equal(SaveFlushStatus.Success, store.Flush(TimeSpan.FromSeconds(5)).Status);
        var disk = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Equal("Light", disk["Theme"]!.GetValue<string>());
        Assert.Equal(42, disk["FutureOption"]!.GetValue<int>());
    }

    [Fact]
    public void MalformedExistingFile_IsNotOverwrittenByDefaults()
    {
        var store = Store();
        const string corrupt = "{not valid JSON";
        File.WriteAllText(SettingsPath, corrupt);
        Assert.Throws<JsonException>(() => store.Load());
        store.Save(store.Load());
        Assert.Equal(SaveFlushStatus.Failed, store.Flush(TimeSpan.FromSeconds(5)).Status);
        Assert.Equal(corrupt, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void StalledWrite_DoesNotBlockSave_AndFlushReturnsTimeout()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var store = Store((path, json) => { entered.Set(); release.Wait(); AtomicFileWriter.Write(path, json); });
        try
        {
            store.Save(store.Load());
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(SaveFlushStatus.TimedOut, store.Flush(TimeSpan.FromMilliseconds(30)).Status);
            release.Set();
            Assert.Equal(SaveFlushStatus.Success, store.Flush(TimeSpan.FromSeconds(5)).Status);
        }
        finally { release.Set(); store.Flush(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public void DifferentPaths_DoNotSharePreferenceCache()
    {
        var first = Store();
        var other = Store(path: Path.Combine(_directory, "other.json"));
        first.Load().Theme = "Light";
        Assert.Equal("Dark", other.Load().Theme);
        Assert.Equal(SaveFlushStatus.Success, other.Flush(TimeSpan.Zero).Status);
    }

    public void Dispose()
    {
        foreach (var store in _stores) store.Flush(TimeSpan.FromSeconds(5));
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
