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

    private readonly Logger _file;

    public ActivityLog()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Vivre", "logs");
        Directory.CreateDirectory(dir);

        _file = new LoggerConfiguration()
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

    public void Clear()
    {
        OnUi(Entries.Clear);
    }

    private void Add(LogSeverity severity, string? machine, string message)
    {
        var entry = new LogEntry(DateTime.Now, severity, machine, message);

        OnUi(() =>
        {
            Entries.Insert(0, entry);
            while (Entries.Count > MaxEntries)
            {
                Entries.RemoveAt(Entries.Count - 1);
            }
        });

        // The in-memory entry above is the user-visible record; the file write is best-effort and
        // must NEVER throw here. Info/Warn/Error are called from inside callers' catch blocks, so a
        // Serilog sink failure (disk full, file locked by AV) would otherwise propagate out of that
        // catch and bury the original failure — the very "never die silently" mechanism hiding the
        // cause. The message is already in Entries, so on a sink failure there's nothing to surface.
        try
        {
            _file.Write(ToLevel(severity), "{Machine} {Message}", machine ?? "-", message);
        }
        catch
        {
            // Logging must not become a new failure source; the in-memory entry already has it.
        }
    }

    private static void OnUi(Action action)
    {
        Application? app = Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(action);
        }
        else
        {
            action();
        }
    }

    private static LogEventLevel ToLevel(LogSeverity severity) => severity switch
    {
        LogSeverity.Error => LogEventLevel.Error,
        LogSeverity.Warning => LogEventLevel.Warning,
        _ => LogEventLevel.Information,
    };
}
