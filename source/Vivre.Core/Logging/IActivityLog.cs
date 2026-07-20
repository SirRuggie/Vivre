using System.Collections.ObjectModel;

namespace Vivre.Core.Logging;

/// <summary>
/// App-wide activity history (shared across all tabs): an in-memory, newest-first
/// collection bound to the log panel, plus a persisted file behind the scenes. View
/// models call <see cref="Info"/>/<see cref="Warn"/>/<see cref="Error"/> as operations
/// complete; Core services stay log-free.
/// </summary>
public interface IActivityLog
{
    /// <summary>Entries, newest first (bound to the UI; capped in length).</summary>
    ObservableCollection<LogEntry> Entries { get; }

    void Info(string? machine, string message);

    void Warn(string? machine, string message);

    void Error(string? machine, string message);

    /// <summary>
    /// A high-volume diagnostic breadcrumb: file-only in the Desktop implementation, NEVER mirrored to the UI
    /// panel (it would drown the operator-facing history). Used by long-running state machines (the reboot
    /// wave) to leave a durable per-beat trace in the daily log for post-hoc diagnosis. Default no-op so
    /// implementations and test doubles need no change.
    /// </summary>
    void Trace(string? machine, string message) { }

    /// <summary>Clears the in-memory entries (the file keeps the full history).</summary>
    void Clear();
}
