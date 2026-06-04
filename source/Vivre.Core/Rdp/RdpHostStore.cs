using System.IO;
using System.Text.Json;

namespace Vivre.Core.Rdp;

/// <summary>
/// Reads/writes the Cross-Domain RDP folder/host tree as <c>%APPDATA%\Vivre\rdphosts.json</c>. Holds NO secrets —
/// passwords live in <see cref="RdpCredentialStore"/>. Mirrors <c>AppSettingsStore</c>: System.Text.Json with a
/// process-wide cached snapshot (Load reads disk once; Save writes + reseats the cache). All access is on the
/// UI thread, so the static cache needs no locking. A corrupt file throws so the caller can log it.
/// </summary>
public sealed class RdpHostStore
{
    private static RdpHostTree? _cache;
    private readonly string _path;

    public RdpHostStore()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vivre");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "rdphosts.json");
    }

    public RdpHostTree Load() => _cache ??= ReadFromDisk();

    private RdpHostTree ReadFromDisk() =>
        File.Exists(_path)
            ? JsonSerializer.Deserialize<RdpHostTree>(File.ReadAllText(_path)) ?? new RdpHostTree()
            : new RdpHostTree();

    public void Save(RdpHostTree tree)
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(tree, new JsonSerializerOptions { WriteIndented = true }));
        _cache = tree;
    }
}
