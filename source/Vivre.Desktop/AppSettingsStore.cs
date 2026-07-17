using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vivre.Core.Columns;
using Vivre.Core.IO;
using Vivre.Core.Logging;

namespace Vivre.Desktop;

/// <summary>Persisted app-level preferences (the first thing in Vivre that's saved to disk besides
/// computer lists/scripts — credentials stay in-memory by design).</summary>
public sealed class AppSettings
{
    /// <summary>"Light" | "Dark" | "System". Applied on startup.</summary>
    public string Theme { get; set; } = "Dark";

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

    /// <summary>Cap on how many hosts YOU install/uninstall/stage/clean-up at once — per operator, not shared
    /// (your change affects only your sweeps; other operators keep their own). Tunable in Settings → "Max
    /// simultaneous installs"; applied to every install sweep started after the change (in-flight sweeps
    /// continue at the cap they were started with). The practical governor is update-download bandwidth (N hosts
    /// pulling cumulative updates simultaneously), not the client. Range 1–200; default 50.</summary>
    public int MaxSimultaneousInstalls { get; set; } = 50;

    /// <summary>How many WhatsUp Gold devices YOUR state check looks up at once — per operator, not shared.
    /// Tunable in Settings → "WhatsUp Gold state check — simultaneous lookups"; applied to checks started after
    /// the change (an in-flight check keeps the value it launched with). Measured on the live WUG server: wall
    /// time halves going 1→2 and then flatlines 2→4→8 with per-lookup latency creeping up, so the ceiling is
    /// deliberately 4 (<see cref="Vivre.Core.Wug.WugMaintenance.StateReadMaxConcurrency"/>). Range 1–4;
    /// default 2. 1 = sequential (the pre-parallel behaviour).</summary>
    public int WugStateConcurrency { get; set; } = 2;

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
/// Reads/writes <see cref="AppSettings"/> as <c>%APPDATA%\Vivre\settings.json</c>. Load returns
/// defaults when the file is absent; a corrupt file or an IO error throws so the caller can log it
/// (no silent swallow). The write is atomic (temp file + File.Replace swap) — a crash mid-write
/// leaves the prior good file intact, so Vivre can no longer produce a torn settings.json itself.
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

    // Serializes the background disk writes so two near-simultaneous Save() calls can't interleave and
    // corrupt settings.json. The last writer wins (its serialized snapshot is what lands on disk). The
    // write itself is atomic (temp file + File.Replace swap), so a crash mid-write leaves the prior good
    // file intact rather than a torn settings.json.
    private static readonly object _writeLock = new();

    // Set once at App startup (static like _cache, so every construction site shares it — the store is
    // new()-constructed in several places). Surfaces a failed background save to the operator: a
    // silently-lost StagedHosts write would mis-route a 2016 box down the wrong WUA lane on the next
    // restart, so this must not stay Debug-only (which compiles out of Release).
    internal static IActivityLog? ActivityLog { get; set; }

    public AppSettingsStore()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vivre");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
    }

    public AppSettings Load() => _cache ??= ReadFromDisk();

    private AppSettings ReadFromDisk()
    {
        try
        {
            AppSettings settings = File.Exists(_path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings()
                : new AppSettings();
            return settings;
        }
        catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException)
        {
            // Content-shaped failure (corrupt JSON / bad shape): it can never self-heal, so seat
            // defaults so later Loads — including the startup-critical WorkspaceViewModel ctor
            // Load — stop re-throwing for the whole session. Still throw ONCE so the guarded
            // first Load logs it (the class contract). Transient IO failures (an AV lock) are
            // deliberately NOT seated: the next Load retries the intact file, and seating
            // defaults here would let a later Save overwrite the real settings.json.
            _cache = new AppSettings();
            throw;
        }
    }

    public void Save(AppSettings settings)
    {
        // Re-seat the cache synchronously (on the calling/UI thread) so the next Load() sees the new value
        // immediately, and snapshot the POCO as a DETACHED JsonObject here so a later mutation of `settings`
        // can't race the background merge. Only the disk write is pushed off the UI thread, so saving a
        // setting never blocks the UI on file I/O.
        _cache = settings;
        JsonObject poco = JsonSerializer.SerializeToNode(settings, WriteIndented) as JsonObject ?? new JsonObject();
        string path = _path;
        _ = Task.Run(() =>
        {
            try
            {
                lock (_writeLock)
                {
                    // Merge onto the on-disk JSON so a key this build doesn't declare (e.g. a newer build's
                    // setting) survives a save by this one — mirrors the shared store's sibling-safe write.
                    JsonObject merged = MergeOntoDisk(path, poco);
                    AtomicFileWriter.Write(path, merged.ToJsonString(WriteIndented));
                }
            }
            catch (Exception ex)
            {
                // The in-memory cache still holds the change, so a failed write means the change is lost
                // ONLY across restart — exactly how a StagedHosts flag silently reverts. Error (not Warn):
                // it is not self-healing and the operator must re-do the action to persist it. This ALSO fires
                // when the on-disk file exists but can't be read/parsed: the write is refused (below) rather
                // than overwriting keys we couldn't read back with the POCO alone.
                ActivityLog?.Error(null, $"Couldn't save settings to {path} — the change will be lost on restart: {ex.GetType().Name}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"AppSettingsStore.Save: failed to write {path}: {ex.Message}");
            }
        });
    }

    private static readonly JsonSerializerOptions WriteIndented = new() { WriteIndented = true };

    /// <summary>Overlays the POCO's keys onto the raw on-disk JSON, preserving any key the POCO doesn't declare
    /// (a newer/older build's setting). An ABSENT file starts from an empty object (first-run write is legal).
    /// A PRESENT-but-unreadable/unparseable file THROWS — the write is refused rather than writing the POCO
    /// alone and silently dropping keys we couldn't read back (surfaced via the caller's ActivityLog.Error).</summary>
    private static JsonObject MergeOntoDisk(string path, JsonObject poco)
    {
        JsonObject onDisk;
        if (File.Exists(path))
        {
            // An IO failure (locked file) throws straight out → refused + surfaced, never a POCO-only fallback.
            string raw = File.ReadAllText(path);
            onDisk = JsonNode.Parse(raw) as JsonObject
                     ?? throw new InvalidDataException(
                         $"{path} exists but its root isn't a JSON object; refusing to overwrite it (would drop keys "
                         + "that couldn't be read back). Fix or delete the file, then retry.");
        }
        else
        {
            onDisk = new JsonObject();
        }

        // DeepClone so each node detaches from `poco` — a JsonNode can't have two parents.
        foreach (KeyValuePair<string, JsonNode?> kv in poco)
        {
            onDisk[kv.Key] = kv.Value?.DeepClone();
        }

        return onDisk;
    }
}
