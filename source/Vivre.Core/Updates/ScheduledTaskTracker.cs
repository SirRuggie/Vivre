namespace Vivre.Core.Updates;

/// <summary>Session-local schedule observations, keyed by host and actual task identity.
/// Expiry is a display decision only; it does not establish that a task executed.</summary>
public sealed class ScheduledTaskTracker
{
    private readonly object gate = new();
    private readonly Dictionary<string, Dictionary<string, List<Entry>>> hosts = new(StringComparer.OrdinalIgnoreCase);
    private sealed record Entry(string Action, DateTime At, bool Unconfirmed);
    public sealed record Snapshot(string Action, DateTime At, int TaskCount, bool Unconfirmed);

    public void Record(string host, string task, string action, DateTime at, bool confirmed = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(task);
        lock (gate)
        {
            if (!hosts.TryGetValue(host, out var tasks)) hosts[host] = tasks = new(StringComparer.OrdinalIgnoreCase);
            // A confirmed same-name registration replaces that task, never another task.
            // On lost acknowledgement the previous trigger may survive; retain both possibilities.
            if (confirmed || !tasks.TryGetValue(task, out var alternatives))
                tasks[task] = [new(action, at, !confirmed)];
            else
            {
                tasks[task] = alternatives.Select(e => e with { Unconfirmed = true })
                    .Append(new(action, at, true)).Distinct().ToList();
            }
        }
    }

    public Snapshot? Next(string host, DateTime now)
    {
        lock (gate)
        {
            if (!hosts.TryGetValue(host, out var tasks)) return null;
            foreach (string key in tasks.Keys.ToArray())
            {
                tasks[key].RemoveAll(e => e.At <= now);
                if (tasks[key].Count == 0) tasks.Remove(key);
            }
            if (tasks.Count == 0) { hosts.Remove(host); return null; }
            Entry next = tasks.Values.SelectMany(e => e).OrderBy(e => e.At).First();
            return new(next.Action, next.At, tasks.Count, tasks.Values.SelectMany(e => e).Any(e => e.Unconfirmed));
        }
    }

    /// <summary>Call only after verified absence, or when removing the local row.</summary>
    public void Remove(string host) { lock (gate) hosts.Remove(host); }
}
