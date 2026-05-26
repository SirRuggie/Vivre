namespace Vivre.Core.Logging;

/// <summary>Severity of an activity-log entry.</summary>
public enum LogSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>One line in the activity log / history.</summary>
/// <param name="Timestamp">When it happened.</param>
/// <param name="Severity">Info / Warning / Error (drives colour).</param>
/// <param name="Machine">Target machine, or null for app-level entries.</param>
/// <param name="Message">What happened.</param>
public sealed record LogEntry(DateTime Timestamp, LogSeverity Severity, string? Machine, string Message);
