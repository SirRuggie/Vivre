using System.Collections.ObjectModel;

namespace Vivre.Core.Logging;

/// <summary>Adds workspace identity at emission time without creating another queue or subscription.</summary>
public sealed class WorkspaceActivityLog(IActivityLog inner, Func<string> origin) : IActivityLog
{
    public ObservableCollection<LogEntry> Entries => inner.Entries;
    public void Info(string? machine, string message) => inner.Info(machine, message, origin());
    public void Warn(string? machine, string message) => inner.Warn(machine, message, origin());
    public void Error(string? machine, string message) => inner.Error(machine, message, origin());
    public void Info(string? machine, string message, string? tag) => inner.Info(machine, message, tag ?? origin());
    public void Warn(string? machine, string message, string? tag) => inner.Warn(machine, message, tag ?? origin());
    public void Error(string? machine, string message, string? tag) => inner.Error(machine, message, tag ?? origin());
    public void Trace(string? machine, string message) => inner.Trace(machine, message);
    public void Clear() => inner.Clear();
}
