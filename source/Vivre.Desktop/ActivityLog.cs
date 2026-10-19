using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Vivre.Core.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Vivre.Desktop;

/// <summary>
/// App-wide <see cref="IActivityLog"/>: keeps the most recent entries in memory
/// (newest first) for the log panel, and writes everything to a rolling daily file
/// at <c>%LOCALAPPDATA%\Vivre\logs\</c> via Serilog.
/// </summary>
public sealed class ActivityLog : IActivityLog
{
    private const int MaxEntries = 2000;
    private const int MaxPendingEntries = 512;

    private readonly Logger _file;
    private readonly object _pendingGate = new();
    private readonly Queue<LogEntry> _pending = new(MaxPendingEntries);
    private long _omittedPending;
    private long _clearGeneration;
    private bool _clearPending;
    private bool _drainScheduled;

    public ActivityLog() : this(CreateFileLogger())
    {
    }

    private ActivityLog(Logger file)
    {
        _file = file;
    }

    private static Logger CreateFileLogger()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Vivre", "logs");
        Directory.CreateDirectory(dir);

        return new LoggerConfiguration()
            .WriteTo.File(
                Path.Combine(dir, "vivre-.log"),
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}")
            .CreateLogger();
    }

    public ObservableCollection<LogEntry> Entries { get; } = [];

    public void Info(string? machine, string message) => Add(LogSeverity.Info, machine, message);

    public void Warn(string? machine, string message) => Add(LogSeverity.Warning, machine, message);

    public void Error(string? machine, string message) => Add(LogSeverity.Error, machine, message);

    public void Info(string? machine, string message, string? origin) => Add(LogSeverity.Info, machine, message, origin);

    public void Warn(string? machine, string message, string? origin) => Add(LogSeverity.Warning, machine, message, origin);

    public void Error(string? machine, string message, string? origin) => Add(LogSeverity.Error, machine, message, origin);

    public void Clear()
    {
        bool drainNow;
        lock (_pendingGate)
        {
            _pending.Clear();
            _omittedPending = 0;
            _clearGeneration++;
            _clearPending = true;
            drainNow = ScheduleDrainLocked();
        }

        if (drainNow)
            DrainPending();
    }

    /// <summary>
    /// Writes a fatal line straight to the rolling file, bypassing the UI dispatcher. For the
    /// <see cref="AppDomain.UnhandledException"/> net, which fires (usually off the UI thread) while
    /// the process is tearing down — <see cref="System.Windows.Threading.Dispatcher.Invoke(Action)"/>
    /// can throw or block then, losing the very line you most want. The in-memory list isn't updated
    /// (the window won't render as the app dies); the durable record is the file.
    /// </summary>
    public void Fatal(string? machine, string message)
    {
        try
        {
            _file.Write(LogEventLevel.Error, "{Machine} {Message}", machine ?? "-", message);
        }
        catch
        {
            // Last-ditch: nothing more we can do as the process is ending.
        }
    }

    /// <summary>
    /// Writes a high-volume diagnostic breadcrumb straight to the rolling file ONLY — never to the in-memory
    /// <see cref="Entries"/> (the UI panel), which is for operator-facing history. Emitted at Information level
    /// because the file sink's minimum level is Information (a Debug write would be dropped). Best-effort: a
    /// sink failure must never propagate out of the reboot wave's poll loop.
    /// </summary>
    public void Trace(string? machine, string message)
    {
        try
        {
            _file.Write(LogEventLevel.Information, "{Machine} {Message}", machine ?? "-", message);
        }
        catch
        {
            // Diagnostic tracing must never become a failure source; it's a breadcrumb, not the record.
        }
    }

    private void Add(LogSeverity severity, string? machine, string message, string? origin = null)
    {
        var entry = new LogEntry(DateTime.Now, severity, machine, message) { Origin = origin };

        // The UI entry is the user-visible record; the file write is best-effort and
        // must NEVER throw here. Info/Warn/Error are called from inside callers' catch blocks, so a
        // Serilog sink failure (disk full, file locked by AV) would otherwise propagate out of that
        // catch and bury the original failure — the very "never die silently" mechanism hiding the
        // cause. The message is already in Entries, so on a sink failure there's nothing to surface.
        try
        {
            // The tag is pre-formatted into a suffix (rather than a bare "{Origin}" slot) so an untagged call
            // writes EXACTLY the line it wrote before — no trailing separator, no empty brackets.
            string tag = string.IsNullOrWhiteSpace(origin) ? string.Empty : $"  [{origin}]";
            _file.Write(ToLevel(severity), "{Machine} {Message}{Origin}", machine ?? "-", message, tag);
        }
        catch
        {
            // Logging must not become a new failure source. Continue with the in-memory entry.
        }

        // Keep UI dispatch failures from preventing the durable write attempted above.
        try
        {
            EnqueueForUi(entry);
        }
        catch (InvalidOperationException)
        {
            // The dispatcher can reject work during shutdown; there is no panel left to update.
        }
        catch (TaskCanceledException)
        {
            // The dispatcher is shutting down; the file write above was still attempted.
        }
    }

    private void EnqueueForUi(LogEntry entry)
    {
        bool drainNow;
        lock (_pendingGate)
        {
            if (_pending.Count == MaxPendingEntries)
            {
                _pending.Dequeue();
                _omittedPending++;
            }
            _pending.Enqueue(entry);

            drainNow = ScheduleDrainLocked();
        }

        if (drainNow)
            DrainPending();
    }

    private bool ScheduleDrainLocked()
    {
        if (_drainScheduled)
            return false;

        _drainScheduled = true;
        Application? app = Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess())
        {
            // One callback drains the bounded buffer in a batch. Producers keep writing the complete
            // durable record independently, even if the UI dispatcher remains stalled.
            app.Dispatcher.BeginInvoke(DrainPending);
            return false;
        }

        return true;
    }

    private void DrainPending()
    {
        LogEntry[] batch;
        long omitted;
        long generation;
        bool clear;
        lock (_pendingGate)
        {
            batch = _pending.ToArray();
            _pending.Clear();
            omitted = _omittedPending;
            _omittedPending = 0;
            generation = _clearGeneration;
            clear = _clearPending;
            _clearPending = false;
        }

        if (clear)
            Entries.Clear();

        foreach (LogEntry entry in batch)
        {
            if (WasClearedAfter(generation))
                break;
            Entries.Insert(0, entry);
        }

        if (omitted > 0 && !WasClearedAfter(generation))
        {
            Entries.Insert(0, new LogEntry(
                DateTime.Now,
                LogSeverity.Warning,
                null,
                $"{omitted:N0} earlier activity entries omitted from this panel while the UI was busy. See the log file for full activity details."));
        }

        while (Entries.Count > MaxEntries)
            Entries.RemoveAt(Entries.Count - 1);

        bool drainAgain;
        lock (_pendingGate)
        {
            drainAgain = _clearPending || _pending.Count > 0 || _omittedPending > 0;
            if (!drainAgain)
                _drainScheduled = false;
        }

        if (drainAgain)
        {
            Application? app = Application.Current;
            if (app is not null)
            {
                // Yield between bounded batches. _drainScheduled remains true, so producers cannot
                // enqueue a second callback while this continuation is pending.
                app.Dispatcher.BeginInvoke(DrainPending);
            }
            else
            {
                // Headless/test hosts have no dispatcher. Finish queued reentrant work after the
                // captured batch rather than recursively from the collection callback.
                DrainPending();
            }
        }
    }

    private bool WasClearedAfter(long generation)
    {
        lock (_pendingGate)
            return _clearGeneration != generation;
    }

    private static LogEventLevel ToLevel(LogSeverity severity) => severity switch
    {
        LogSeverity.Error => LogEventLevel.Error,
        LogSeverity.Warning => LogEventLevel.Warning,
        _ => LogEventLevel.Information,
    };
}
