namespace Vivre.Core.Operations;

/// <summary>
/// App-wide, per-host ownership used to keep general disk cleanup away from operations whose
/// working files or target-side servicing state it could disturb. Ordinary operations share the
/// host and retain their existing concurrency; cleanup is exclusive.
/// </summary>
public sealed class HostOperationCoordinator
{
    private readonly object _sync = new();
    private readonly Dictionary<string, HostState> _hosts = new(StringComparer.OrdinalIgnoreCase);

    public HostOperationClaim TryBeginOperation(string host, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        string key = NormalizeHost(host);
        lock (_sync)
        {
            HostState state = GetState(key);
            if (state.UncertainCleanup is not null)
            {
                return HostOperationClaim.Refused(
                    $"Blocked — {state.UncertainCleanup} ended without confirming that its remote recovery finished; restart Vivre after confirming the host is idle.");
            }

            if (state.Cleanup is not null)
            {
                return HostOperationClaim.Refused(
                    $"Blocked — {state.Cleanup} is already in progress on this host.");
            }

            state.Operations.Add(operation);
            return HostOperationClaim.Granted(this, key, operation, cleanup: false);
        }
    }

    public HostOperationClaim TryBeginCleanup(string host, string operation = "Free up disk space")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        string key = NormalizeHost(host);
        lock (_sync)
        {
            HostState state = GetState(key);
            if (state.UncertainCleanup is not null)
            {
                return HostOperationClaim.Refused(
                    $"Blocked — {state.UncertainCleanup} ended without confirming that its remote recovery finished; restart Vivre after confirming the host is idle.");
            }

            if (state.Cleanup is not null)
            {
                return HostOperationClaim.Refused(
                    $"Blocked — {state.Cleanup} is already in progress on this host.");
            }

            if (state.Operations.Count > 0)
            {
                return HostOperationClaim.Refused(
                    $"Blocked — {state.Operations[0]} is already in progress on this host.");
            }

            if (state.UncertainOperations.Count > 0)
            {
                return HostOperationClaim.Refused(
                    $"Blocked — {state.UncertainOperations[0]} ended without confirming that its remote work stopped; restart Vivre after confirming the host is idle.");
            }

            state.Cleanup = operation;
            return HostOperationClaim.Granted(this, key, operation, cleanup: true);
        }
    }

    private HostState GetState(string key)
    {
        if (!_hosts.TryGetValue(key, out HostState? state))
        {
            state = new HostState();
            _hosts.Add(key, state);
        }

        return state;
    }

    private void End(string key, string operation, bool cleanup, bool uncertain)
    {
        lock (_sync)
        {
            if (!_hosts.TryGetValue(key, out HostState? state))
            {
                return;
            }

            if (cleanup)
            {
                state.Cleanup = null;
                if (uncertain)
                {
                    state.UncertainCleanup = operation;
                }
            }
            else
            {
                state.Operations.Remove(operation);
                if (uncertain && !state.UncertainOperations.Contains(operation, StringComparer.Ordinal))
                {
                    state.UncertainOperations.Add(operation);
                }
            }

            if (state.Cleanup is null && state.UncertainCleanup is null && state.Operations.Count == 0 && state.UncertainOperations.Count == 0)
            {
                _hosts.Remove(key);
            }
        }
    }

    internal static string NormalizeHost(string host)
    {
        string trimmed = host.Trim();
        bool dotAlias = trimmed == ".";
        if (!dotAlias)
        {
            trimmed = trimmed.TrimEnd('.');
        }

        bool localAlias = dotAlias
            || string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase)
            || trimmed is "127.0.0.1" or "::1"
            || string.Equals(trimmed, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        return localAlias ? "<local>" : trimmed;
    }

    private sealed class HostState
    {
        public List<string> Operations { get; } = [];
        public List<string> UncertainOperations { get; } = [];
        public string? Cleanup { get; set; }
        public string? UncertainCleanup { get; set; }
    }

    public sealed class HostOperationClaim : IDisposable
    {
        private readonly HostOperationCoordinator? _owner;
        private readonly string? _key;
        private readonly string? _operation;
        private readonly bool _cleanup;
        private int _ended;
        private bool _uncertain;

        private HostOperationClaim(HostOperationCoordinator? owner, string? key, string? operation, bool cleanup, string? refusal)
        {
            _owner = owner;
            _key = key;
            _operation = operation;
            _cleanup = cleanup;
            RefusalMessage = refusal;
        }

        public bool Acquired => _owner is not null;
        public string? RefusalMessage { get; }

        public void MarkRemoteStateUncertain() => _uncertain = true;

        public void Dispose()
        {
            if (_owner is not null && Interlocked.Exchange(ref _ended, 1) == 0)
            {
                _owner.End(_key!, _operation!, _cleanup, _uncertain);
            }
        }

        internal static HostOperationClaim Granted(HostOperationCoordinator owner, string key, string operation, bool cleanup) =>
            new(owner, key, operation, cleanup, null);

        internal static HostOperationClaim Refused(string message) => new(null, null, null, false, message);
    }
}
