using System.Collections.Concurrent;
using System.Management.Automation;
using System.Runtime.CompilerServices;
using System.Security;

namespace Vivre.Core.PowerShell;

public enum HostTransport
{
    Unknown = 0,
    WinRm,
    SmbDcom,
}

/// <summary>
/// Session-scoped transport decisions. A decision belongs to one WinRM endpoint and credential
/// identity; no password content is inspected, hashed, persisted, or rendered.
/// </summary>
public sealed class HostTransportCache
{
    private sealed record CredentialToken(long Value);
    private sealed record Entry(HostTransport Transport, long Attempt);

    private static readonly ConditionalWeakTable<SecureString, CredentialToken> CredentialTokens = new();
    private static long _nextCredentialToken;
    private readonly ConcurrentDictionary<ConnectionKey, Entry> _transport = new();

    internal readonly record struct ConnectionKey(
        string Host, int Port, bool UseSsl, string UserName, long PasswordToken);
    internal readonly record struct NormalAttempt(HostTransport Transport, long Generation);

    internal ConnectionKey KeyFor(string host, PSCredential? credential, int port, bool useSsl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        long passwordToken = credential is null
            ? 0
            : CredentialTokens.GetValue(
                credential.Password,
                static _ => new CredentialToken(Interlocked.Increment(ref _nextCredentialToken))).Value;
        return new ConnectionKey(
            host.Trim().ToUpperInvariant(),
            port,
            useSsl,
            credential?.UserName.ToUpperInvariant() ?? string.Empty,
            passwordToken);
    }

    internal HostTransport Get(ConnectionKey key) =>
        _transport.TryGetValue(key, out Entry? entry) ? entry.Transport : HostTransport.Unknown;

    /// <summary>Atomically captures routing and recovery generation before an ordinary dispatch.</summary>
    internal NormalAttempt BeginNormal(ConnectionKey key)
    {
        Entry entry = _transport.GetOrAdd(key, static _ => new Entry(HostTransport.Unknown, 0));
        return new NormalAttempt(entry.Transport, entry.Attempt);
    }

    /// <summary>Starts an explicit recovery and fences every ordinary attempt already in flight.</summary>
    internal long BeginRecovery(ConnectionKey key) =>
        _transport.AddOrUpdate(
            key,
            static _ => new Entry(HostTransport.Unknown, 1),
            static (_, prior) => prior with { Attempt = checked(prior.Attempt + 1) }).Attempt;

    internal bool TryCompleteNormal(ConnectionKey key, long generation, HostTransport transport)
    {
        while (_transport.TryGetValue(key, out Entry? current))
        {
            if (current.Attempt != generation)
            {
                return false;
            }

            // Within one generation a Kerberos rejection remains sticky. Only an explicit verified
            // recovery is allowed to clear it.
            HostTransport next = current.Transport == HostTransport.SmbDcom
                ? HostTransport.SmbDcom
                : transport;
            if (_transport.TryUpdate(key, new Entry(next, generation), current))
            {
                return true;
            }
        }

        return false;
    }

    internal bool TryCompleteRecovery(ConnectionKey key, long generation, HostTransport transport)
    {
        while (_transport.TryGetValue(key, out Entry? current))
        {
            if (current.Attempt != generation)
            {
                return false;
            }

            // Advancing again at completion fences ordinary calls that began while this recovery
            // was in flight, so a late result cannot replace the explicit verdict.
            if (_transport.TryUpdate(key, new Entry(transport, checked(generation + 1)), current))
            {
                return true;
            }
        }

        return false;
    }

    public HostTransport Get(string host) => Get(KeyFor(host, null, 5985, false));
    public void MarkWinRm(string host)
    {
        ConnectionKey key = KeyFor(host, null, 5985, false);
        NormalAttempt attempt = BeginNormal(key);
        TryCompleteNormal(key, attempt.Generation, HostTransport.WinRm);
    }

    public void MarkKerberosRejected(string host)
    {
        ConnectionKey key = KeyFor(host, null, 5985, false);
        NormalAttempt attempt = BeginNormal(key);
        TryCompleteNormal(key, attempt.Generation, HostTransport.SmbDcom);
    }

    public bool IsKerberosDegraded(string host) => Get(host) == HostTransport.SmbDcom;
}
