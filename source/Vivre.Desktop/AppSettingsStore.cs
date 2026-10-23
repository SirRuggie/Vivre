using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vivre.Core.Columns;
using Vivre.Core.IO;
using Vivre.Core.Logging;
using Vivre.Core.Updates;

namespace Vivre.Desktop;

public sealed record GridColumnPreference(string Key, double Width, int Unit, int Order);

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

    public Dictionary<string, List<GridColumnPreference>> GridLayouts { get; set; } = [];

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

    /// <summary>Personal cap on CU package checks/copies/verification across tabs in this process.
    /// Defaults to 2 for new and older settings files. Range 1–200. Changing it adjusts waiting work;
    /// active file jobs finish normally. Remote installations do not hold these slots.</summary>
    public int MaxSimultaneousCuTransfers { get; set; } = CuPackageIo.DefaultConcurrency;

    /// <summary>How many WhatsUp Gold devices YOUR state check looks up at once — per operator, not shared.
    /// Tunable in Settings → "WhatsUp Gold state check — simultaneous lookups"; applied to checks started after
    /// the change (an in-flight check keeps the value it launched with). Measured on the live WUG server: wall
    /// time halves going 1→2 and then flatlines 2→4→8 with per-lookup latency creeping up, so the ceiling is
    /// deliberately 4 (<see cref="Vivre.Core.Wug.WugMaintenance.StateReadMaxConcurrency"/>). Range 1–4;
    /// default 2. 1 = sequential (the pre-parallel behaviour).</summary>
    public int WugStateConcurrency { get; set; } = 2;

    /// <summary>How long (minutes) the Reboot Wave WAITS for in-progress Windows servicing to finish before it
    /// issues the ordered reboot — per operator, not shared. The wave re-checks readiness on its poll interval
    /// and reboots the instant servicing settles; if this window elapses first it stops WITHOUT rebooting (the
    /// row says "not rebooted; use Force reboot to override") — it NEVER forces a reboot through active
    /// servicing. Tunable in Settings → "Reboot wave — max wait for in-progress servicing (minutes)". Read +
    /// clamped to 5–120 at wave-launch time; applied to waves started after the change. Default 20.</summary>
    public int ServicingWaitMinutes { get; set; } = 20;

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

    // Every caller for the same personal file shares its cache and one FIFO writer. Callers perform
    // Load/mutate/Save on the UI thread; only detached snapshots cross to the worker.
    private static readonly ConcurrentDictionary<string, StoreState> Stores = new(StringComparer.OrdinalIgnoreCase);
    private readonly StoreState _state;

    internal static IActivityLog? ActivityLog { get; set; }

    public AppSettingsStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vivre", "settings.json"))
    {
    }

    // Tests use isolated dummy files and an optional controlled writer; production uses AtomicFileWriter.
    internal AppSettingsStore(string path, Action<string, string>? write = null)
    {
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _state = Stores.GetOrAdd(_path, p => new StoreState(p, write ?? AtomicFileWriter.Write));
    }

    public AppSettings Load() => _state.Cache ??= ReadFromDisk();

    /// <summary>Waits for the saves accepted before this call, up to the supplied deadline.</summary>
    public SaveFlushResult Flush(TimeSpan timeout) => _state.Queue.Flush(timeout);

    private sealed class StoreState
    {
        public AppSettings? Cache;
        public Action<string, string> Write { get; }
        public OrderedSaveQueue Queue { get; }

        public StoreState(string path, Action<string, string> write)
        {
            Write = write;
            Queue = new OrderedSaveQueue(ex =>
            {
                ActivityLog?.Error(null, $"Couldn't persist a preference change to {path}: {ex.GetType().Name}: {ex.Message}. A later successful save may recover this; otherwise the change may be lost on restart. Resolve continuing storage errors and change a preference to retry.");
                System.Diagnostics.Debug.WriteLine($"AppSettingsStore.Save: failed to write {path}: {ex.Message}");
            });
        }
    }

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
            _state.Cache = new AppSettings();
            throw;
        }
    }

    public void Save(AppSettings settings)
    {
        // Snapshot before returning to the UI: later edits to the same cached object cannot change
        // an already accepted write. FIFO admission happens here, never inside a racing Task.Run.
        JsonObject snapshot = JsonSerializer.SerializeToNode(settings, WriteIndented) as JsonObject ?? new JsonObject();
        _state.Cache = settings;
        _state.Queue.Enqueue(() =>
        {
            JsonObject merged = MergeOntoDisk(_path, snapshot);
            _state.Write(_path, merged.ToJsonString(WriteIndented));
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
