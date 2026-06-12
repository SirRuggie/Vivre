using System.IO;
using System.Text.Json;
using Vivre.Core.Columns;

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

    /// <summary>Folder the Server 2016 full-package CU lane reads the monthly cumulative-update <c>.msu</c>
    /// from (the operator drops the catalog download here; auto-fetch is off by design). Defaults to
    /// <c>C:\Vivre\VivrePackages</c>; configurable in Settings.</summary>
    public string LcuPackagesFolder { get; set; } = @"C:\Vivre\VivrePackages";

    /// <summary>This cycle's Server 2016 cumulative update — the KB the lane stages and the UBR it verifies
    /// after the reboot. Surfaced in the 2016 panel and confirmed by the operator each month; defaults to
    /// the cycle in flight when the lane shipped.</summary>
    public MonthlyCu MonthlyCu { get; set; } = new();

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

    /// <summary>User-defined custom machine-grid columns (name + PowerShell one-liner). Each runs on every
    /// machine and its output fills that column. Empty by default; grows as the user adds columns.</summary>
    public List<CustomColumnSpec> CustomColumns { get; set; } = [];

    /// <summary>Built-in machine-grid column headers the user has hidden (the Name column is never
    /// hideable). Empty by default.</summary>
    public List<string> HiddenColumns { get; set; } = [];

    /// <summary>When true (default), loading a list (or adding machines) auto-pings + checks vitals on
    /// those machines so the grid's data is "just there". Off = manual (a frozen snapshot until you
    /// click Ping All / Check Vitals). Always scoped to the loaded list; never the wider fleet.</summary>
    public bool AutoCheckOnLoad { get; set; } = true;

    /// <summary>Whether the left NavigationView pane is expanded (true) or collapsed/compact (false).
    /// Default false — starts collapsed so the icon-only pane takes minimal horizontal space.</summary>
    public bool NavPaneOpen { get; set; } = false;

    /// <summary>Last height (device-independent pixels) the bottom dock was explicitly sized to —
    /// either by dragging the splitter or on close. Stored RAW (the exact user-dragged value);
    /// the fraction clamp in ShowDock applies at open time, so a value above 40% of the section
    /// height will open at the clamped height rather than this raw value until the user resizes
    /// further. Default 170.</summary>
    public double BottomDockHeight { get; set; } = 170;
}

/// <summary>
/// The month's Server 2016 cumulative update the operator confirms each cycle. Kept deliberately small —
/// the KB to stage, the architecture token expected in the <c>.msu</c> name, and the UBR the box should
/// report once the CU commits (what Verify / the Reboot Wave check). Maps to <c>LcuTarget</c> in the lane.
/// </summary>
public sealed class MonthlyCu
{
    /// <summary>The CU article, e.g. "KB5094122" (bare "5094122" also accepted by the lane).</summary>
    public string Kb { get; set; } = "KB5094122";

    /// <summary>Architecture token expected in the .msu filename (Server 2016 is x64).</summary>
    public string Arch { get; set; } = "x64";

    /// <summary>The build revision (UBR) the box should report after the CU commits, e.g. 9234 → the box
    /// reads 14393.9234. Verify and the Reboot Wave use this as the pass/fail check.</summary>
    public int TargetUbr { get; set; } = 9234;

    /// <summary>The display Vivre shows in the 2016 panel, e.g. "KB5094122 / 9234".</summary>
    public string Display => $"{Kb} / {TargetUbr}";
}

/// <summary>
/// Reads/writes <see cref="AppSettings"/> as <c>%APPDATA%\Vivre\settings.json</c>. Load returns
/// defaults when the file is absent; a corrupt file or an IO error throws so the caller can log it
/// (no silent swallow).
/// </summary>
public sealed class AppSettingsStore
{
    private readonly string _path;

    // Process-wide in-memory snapshot of settings.json. Every AppSettingsStore instance points at the same
    // file (per-tab view-models + the main window), and every read/write runs on the UI thread, so a single
    // static cache is safe without locking. Load() touches disk only to populate the cache the first time;
    // Save() rewrites the file AND re-seats the cache, so a runtime change (e.g. the Auto-check-on-load
    // toggle) is visible to the next Load() without another disk read.
    private static AppSettings? _cache;

    public AppSettingsStore()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vivre");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
    }

    public AppSettings Load() => _cache ??= ReadFromDisk();

    private AppSettings ReadFromDisk() =>
        File.Exists(_path)
            ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings()
            : new AppSettings();

    public void Save(AppSettings settings)
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        _cache = settings;
    }
}
