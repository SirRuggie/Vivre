using System.Collections.Concurrent;
using System.Text.Json;
using Vivre.Core.IO;

namespace Vivre.Core.Operations;

/// <summary>Persistent per-host paths that general disk cleanup must preserve.</summary>
public sealed class ProtectedStagingPathRegistry
{
    private static readonly ConcurrentDictionary<string, object> FileLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _path;
    private readonly string _leasePath;
    private readonly object _sync;

    public ProtectedStagingPathRegistry(string? path = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Vivre",
            "protected-staging-paths.json"));
        _leasePath = _path + ".lock";
        _sync = FileLocks.GetOrAdd(_path, static _ => new object());
    }

    /// <summary>
    /// Records a destination before staging starts. Existing unreadable/corrupt data causes an
    /// exception so staging fails closed instead of replacing protection with an empty registry.
    /// </summary>
    public void Register(string host, string targetPath)
        => RegisterMany(host, [targetPath]);

    /// <summary>Records a batch in one read/merge/write and skips unchanged saves.</summary>
    public void RegisterMany(string host, IEnumerable<string> paths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(paths);

        string key = HostOperationCoordinator.NormalizeHost(host);
        HashSet<string> additions = NormalizePaths(paths);
        if (additions.Count == 0) return;

        lock (_sync)
        {
            EnsureParentDirectory();
            using FileStream lease = ExclusiveFileLease.Acquire(_leasePath);
            Dictionary<string, HashSet<string>> entries = LoadExisting();
            if (!entries.TryGetValue(key, out HashSet<string>? existing))
            {
                existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                entries.Add(key, existing);
            }

            int before = existing.Count;
            existing.UnionWith(additions);
            if (existing.Count == before) return;

            SaveAtomically(entries);
        }
    }

    /// <summary>Removes exact paths only after the caller has confirmed cleanup of its owned artifacts.</summary>
    public void UnregisterOwnedConfirmed(string host, IEnumerable<string> paths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(paths);

        string key = HostOperationCoordinator.NormalizeHost(host);
        HashSet<string> removals = NormalizePaths(paths);
        if (removals.Count == 0) return;

        lock (_sync)
        {
            EnsureParentDirectory();
            using FileStream lease = ExclusiveFileLease.Acquire(_leasePath);
            Dictionary<string, HashSet<string>> entries = LoadExisting();
            if (!entries.TryGetValue(key, out HashSet<string>? existing) ||
                existing.RemoveWhere(removals.Contains) == 0)
            {
                return;
            }

            if (existing.Count == 0) entries.Remove(key);
            SaveAtomically(entries);
        }
    }

    /// <summary>Reads protected paths fresh. Any registry failure propagates and blocks cleanup.</summary>
    public IReadOnlyCollection<string> GetProtectedPaths(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        lock (_sync)
        {
            Dictionary<string, HashSet<string>> entries = LoadExisting();
            string key = HostOperationCoordinator.NormalizeHost(host);
            return entries.TryGetValue(key, out HashSet<string>? paths)
                ? paths.ToArray()
                : Array.Empty<string>();
        }
    }

    private Dictionary<string, HashSet<string>> LoadExisting()
    {
        string json;
        try
        {
            json = File.ReadAllText(_path);
        }
        catch (FileNotFoundException)
        {
            return new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        }
        catch (DirectoryNotFoundException)
        {
            return new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        }
        Dictionary<string, List<string>>? raw = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);
        if (raw is null)
        {
            throw new InvalidDataException("The protected staging-path registry has no readable content.");
        }

        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string host, List<string>? paths) in raw)
        {
            if (string.IsNullOrWhiteSpace(host) || paths is null || paths.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidDataException("The protected staging-path registry contains an invalid entry.");
            }

            string key = HostOperationCoordinator.NormalizeHost(host);
            if (!result.TryGetValue(key, out HashSet<string>? existing))
            {
                existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                result.Add(key, existing);
            }
            existing.UnionWith(paths.Select(NormalizePath));
        }

        return result;
    }

    private static string NormalizePath(string path)
    {
        string trimmed = path.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
        {
            throw new ArgumentException("Protected staging paths must be fully qualified.", nameof(path));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
    }

    private static HashSet<string> NormalizePaths(IEnumerable<string> paths)
    {
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            normalized.Add(NormalizePath(path));
        }
        return normalized;
    }

    private void SaveAtomically(Dictionary<string, HashSet<string>> entries)
    {
        string directory = EnsureParentDirectory();
        string temp = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, entries, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private string EnsureParentDirectory()
    {
        string? directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The protected staging-path registry location has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        return directory;
    }
}
