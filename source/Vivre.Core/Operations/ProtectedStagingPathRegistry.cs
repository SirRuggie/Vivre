using System.Text.Json;

namespace Vivre.Core.Operations;

/// <summary>Persistent per-host paths that general disk cleanup must preserve.</summary>
public sealed class ProtectedStagingPathRegistry
{
    private readonly object _sync = new();
    private readonly string _path;

    public ProtectedStagingPathRegistry(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Vivre",
            "protected-staging-paths.json");
    }

    /// <summary>
    /// Records a destination before staging starts. Existing unreadable/corrupt data causes an
    /// exception so staging fails closed instead of replacing protection with an empty registry.
    /// </summary>
    public void Register(string host, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        lock (_sync)
        {
            Dictionary<string, List<string>> entries = LoadExisting();
            string key = HostOperationCoordinator.NormalizeHost(host);
            if (!entries.TryGetValue(key, out List<string>? paths))
            {
                paths = [];
                entries.Add(key, paths);
            }

            string normalized = NormalizePath(targetPath);
            if (!paths.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(normalized);
            }

            SaveAtomically(entries);
        }
    }

    /// <summary>Reads protected paths fresh. Any registry failure propagates and blocks cleanup.</summary>
    public IReadOnlyCollection<string> GetProtectedPaths(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        lock (_sync)
        {
            Dictionary<string, List<string>> entries = LoadExisting();
            string key = HostOperationCoordinator.NormalizeHost(host);
            return entries.TryGetValue(key, out List<string>? paths)
                ? paths.ToArray()
                : Array.Empty<string>();
        }
    }

    private Dictionary<string, List<string>> LoadExisting()
    {
        string json;
        try
        {
            json = File.ReadAllText(_path);
        }
        catch (FileNotFoundException)
        {
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }
        catch (DirectoryNotFoundException)
        {
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }
        Dictionary<string, List<string>>? raw = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);
        if (raw is null)
        {
            throw new InvalidDataException("The protected staging-path registry has no readable content.");
        }

        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string host, List<string>? paths) in raw)
        {
            if (string.IsNullOrWhiteSpace(host) || paths is null || paths.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidDataException("The protected staging-path registry contains an invalid entry.");
            }

            string key = HostOperationCoordinator.NormalizeHost(host);
            if (!result.TryGetValue(key, out List<string>? existing))
            {
                existing = [];
                result.Add(key, existing);
            }
            foreach (string normalized in paths.Select(NormalizePath))
            {
                if (!existing.Contains(normalized, StringComparer.OrdinalIgnoreCase)) existing.Add(normalized);
            }
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

    private void SaveAtomically(Dictionary<string, List<string>> entries)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The protected staging-path registry location has no parent directory.");
        }

        Directory.CreateDirectory(directory);
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
}
