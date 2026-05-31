using System.IO;
using System.Text.Json;

namespace Vivre.Desktop;

/// <summary>Persisted app-level preferences (the first thing in Vivre that's saved to disk besides
/// computer lists/scripts — credentials stay in-memory by design).</summary>
public sealed class AppSettings
{
    /// <summary>"Light" | "Dark" | "System". Applied on startup.</summary>
    public string Theme { get; set; } = "Dark";
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
