using System.IO;
using System.Text.Json;

namespace Vivre.Desktop;

/// <summary>Persisted app-level preferences (the first thing in Vivre that's saved to disk besides
/// computer lists/scripts — credentials stay in-memory by design).</summary>
public sealed class AppSettings
{
    /// <summary>"Light" | "Dark" | "System". Applied on startup.</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>WhatsUp Gold server address, remembered for the maintenance-mode dialog (the
    /// credentials are NOT saved — only this address).</summary>
    public string WugServer { get; set; } = "10.70.25.111";

    /// <summary>Folder holding the stageable software packages (each subfolder or lone .msi/.exe is
    /// one package). Populates the Stage software window's package dropdown; empty by default.</summary>
    public string PackagesFolder { get; set; } = string.Empty;

    /// <summary>Remembered "product name → Windows service name" pairs for the Check software dialog, so
    /// once you tell Vivre that (e.g.) CrowdStrike's service is CSFalconService it pre-fills it next time.
    /// Seeded with the common security agents; grows as you check more. Lookups are case-insensitive
    /// (do them via a helper — a JSON round-trip resets the dictionary's comparer to ordinal).</summary>
    public Dictionary<string, string> SoftwareServiceMap { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CrowdStrike"] = "CSFalconService",
        // SentinelOne installs as "Sentinel Agent" but its publisher is "SentinelOne, Inc." — the probe
        // matches DisplayName OR Publisher, so "SentinelOne" finds it. Service: SentinelAgent.
        ["SentinelOne"] = "SentinelAgent",
    };
}

/// <summary>
/// Reads/writes <see cref="AppSettings"/> as <c>%APPDATA%\Vivre\settings.json</c>. Load returns
/// defaults when the file is absent; a corrupt file or an IO error throws so the caller can log it
/// (no silent swallow).
/// </summary>
public sealed class AppSettingsStore
{
    private readonly string _path;

    public AppSettingsStore()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vivre");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
    }

    public AppSettings Load() =>
        File.Exists(_path)
            ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings()
            : new AppSettings();

    public void Save(AppSettings settings) =>
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
}
