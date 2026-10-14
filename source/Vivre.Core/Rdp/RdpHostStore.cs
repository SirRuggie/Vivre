using System.IO;
using System.Text.Json;
using Vivre.Core.IO;

namespace Vivre.Core.Rdp;

/// <summary>
/// Reads/writes the Cross-Domain RDP folder/host tree as <c>%APPDATA%\Vivre\rdphosts.json</c>. Holds NO secrets —
/// passwords live in <see cref="RdpCredentialStore"/>. Mirrors <c>AppSettingsStore</c>: System.Text.Json with a
/// cached snapshot. Saves use atomic replacement and retain a last-known-good backup.
/// </summary>
public sealed class RdpHostStore
{
    private RdpHostTree? _cache;
    private readonly string _path;
    private readonly Action<string, string, bool> _write;
    private bool _loadFailed;
    private bool _loadedFromBackup;

    public string? RecoveryMessage { get; private set; }

    public RdpHostStore()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vivre");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "rdphosts.json");
        _write = RecoverableFileWriter.Write;
    }

    internal RdpHostStore(string path, Action<string, string, bool>? write = null)
    {
        _path = path;
        _write = write ?? RecoverableFileWriter.Write;
    }

    public RdpHostTree Load()
    {
        if (_cache is not null)
        {
            return Clone(_cache);
        }

        if (!PathExists(_path))
        {
            if (!PathExists(_path + ".bak"))
            {
                _cache = new RdpHostTree();
                return Clone(_cache);
            }

            try
            {
                _cache = ReadRequired(_path + ".bak");
                _loadedFromBackup = true;
                RecoveryMessage = "The saved RDP host tree was missing. Vivre loaded its last-known-good host-tree backup; the next successful save will restore the primary file.";
                return Clone(_cache);
            }
            catch (Exception backupError)
            {
                _loadFailed = true;
                throw new InvalidDataException("The RDP host tree primary file is missing and its last-known-good host-tree backup is unreadable. Saving is blocked to protect the backup.", backupError);
            }
        }

        try
        {
            _cache = ReadRequired(_path);
            return Clone(_cache);
        }
        catch (Exception primaryError)
        {
            if (PathExists(_path + ".bak") && TryReadBackup(out RdpHostTree? recovered))
            {
                _cache = recovered!;
                _loadedFromBackup = true;
                RecoveryMessage = "The saved RDP host tree was unreadable. Vivre loaded its last-known-good host-tree backup; the next successful save will repair the primary file.";
                return Clone(_cache);
            }

            _loadFailed = true;
            throw new InvalidDataException("The saved RDP host tree is unreadable and no valid last-known-good host-tree backup is available. Saving is blocked to protect the existing files.", primaryError);
        }
    }

    public void Save(RdpHostTree tree)
    {
        if (_loadFailed)
        {
            throw new InvalidOperationException("Saving the RDP host tree is blocked because its existing files could not be loaded. Restore or remove the damaged files before saving.");
        }
        _ = _cache ?? Load();

        RdpHostTree snapshot = Clone(tree);
        ValidateFolder(snapshot.Root, "Root");
        string json = JsonSerializer.Serialize(snapshot, SerializerOptions);
        bool preserveBackup = _loadedFromBackup || (PathExists(_path) && !CanRead(_path));
        _write(_path, json, preserveBackup);
        _cache = snapshot;
        _loadedFromBackup = false;
        RecoveryMessage = null;
    }

    private bool TryReadBackup(out RdpHostTree? tree)
    {
        try
        {
            tree = ReadRequired(_path + ".bak");
            return true;
        }
        catch (Exception) when (PathExists(_path + ".bak"))
        {
            tree = null;
            return false;
        }
    }

    private static bool CanRead(string path)
    {
        try { _ = ReadRequired(path); return true; }
        catch { return false; }
    }

    private static RdpHostTree ReadRequired(string path)
    {
        if (!PathExists(path)) throw new FileNotFoundException("RDP host store not found.", path);
        string json = File.ReadAllText(path);
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty(nameof(RdpHostTree.Root), out JsonElement rootElement))
            throw new InvalidDataException("RDP host store is missing its Root object.");
        ValidateFolderSchema(rootElement);
        RdpHostTree tree = JsonSerializer.Deserialize<RdpHostTree>(json)
            ?? throw new InvalidDataException("RDP host store contained JSON null.");
        ValidateFolder(tree.Root, "Root");
        return tree;
    }

    private static void ValidateFolder(RdpFolder? folder, string location)
    {
        if (folder is null || string.IsNullOrWhiteSpace(folder.Id) || folder.Folders is null || folder.Hosts is null)
            throw new InvalidDataException($"RDP host store has an invalid {location} folder.");
        if (folder.Folders.Any(f => f is null) || folder.Hosts.Any(h => h is null || string.IsNullOrWhiteSpace(h.Id)))
            throw new InvalidDataException($"RDP host store has an invalid item in {location}.");
        foreach (RdpFolder child in folder.Folders) ValidateFolder(child, "nested");
    }

    private static void ValidateFolderSchema(JsonElement folder)
    {
        if (folder.ValueKind != JsonValueKind.Object
            || !folder.TryGetProperty(nameof(RdpFolder.Id), out _)
            || !folder.TryGetProperty(nameof(RdpFolder.Folders), out JsonElement folders)
            || folders.ValueKind != JsonValueKind.Array
            || !folder.TryGetProperty(nameof(RdpFolder.Hosts), out JsonElement hosts)
            || hosts.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("RDP host store has an incomplete folder schema.");
        foreach (JsonElement child in folders.EnumerateArray()) ValidateFolderSchema(child);
        foreach (JsonElement host in hosts.EnumerateArray())
            if (host.ValueKind != JsonValueKind.Object || !host.TryGetProperty(nameof(RdpHost.Id), out _))
                throw new InvalidDataException("RDP host store has an incomplete host schema.");
    }

    private static bool PathExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static RdpHostTree Clone(RdpHostTree tree) =>
        JsonSerializer.Deserialize<RdpHostTree>(JsonSerializer.Serialize(tree, SerializerOptions))
        ?? throw new InvalidDataException("RDP host tree could not be copied.");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
}
